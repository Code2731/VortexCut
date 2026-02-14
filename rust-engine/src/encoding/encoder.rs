// 비디오/오디오 인코더 - FFmpeg 기반 H.264 + AAC 인코딩
// RGBA 프레임 → YUV420P → H.264 인코딩
// f32 PCM → FLTP → AAC 인코딩
// → MP4 먹싱
// GPU 하드웨어 가속: NVENC / QSV / AMF 지원

use ffmpeg_next as ffmpeg;
use ffmpeg::format::Pixel;
use ffmpeg::codec;
use ffmpeg::software::scaling;

/// 인코더 타입 (FFI u32 매핑)
#[repr(u32)]
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum EncoderType {
    Auto = 0,       // NVENC → QSV → AMF → libx264 순서 시도
    Software = 1,   // libx264
    Nvenc = 2,      // h264_nvenc (NVIDIA)
    Qsv = 3,        // h264_qsv (Intel)
    Amf = 4,        // h264_amf (AMD)
}

impl EncoderType {
    pub fn from_u32(v: u32) -> Self {
        match v {
            1 => EncoderType::Software,
            2 => EncoderType::Nvenc,
            3 => EncoderType::Qsv,
            4 => EncoderType::Amf,
            _ => EncoderType::Auto,
        }
    }
}

/// 사용 가능한 인코더 탐지 (비트마스크 반환)
/// bit 0 = libx264, bit 1 = NVENC, bit 2 = QSV, bit 3 = AMF
pub fn detect_available_encoders() -> u32 {
    ffmpeg::init().ok();
    let mut mask = 0u32;
    if ffmpeg::encoder::find_by_name("libx264").is_some() { mask |= 1; }
    if ffmpeg::encoder::find_by_name("h264_nvenc").is_some() { mask |= 2; }
    if ffmpeg::encoder::find_by_name("h264_qsv").is_some() { mask |= 4; }
    if ffmpeg::encoder::find_by_name("h264_amf").is_some() { mask |= 8; }
    eprintln!("[ENCODER] 탐지된 인코더: mask=0b{:04b} (x264={}, nvenc={}, qsv={}, amf={})",
        mask, mask & 1 != 0, mask & 2 != 0, mask & 4 != 0, mask & 8 != 0);
    mask
}

/// 비디오+오디오 인코더 (H.264 + AAC + MP4 컨테이너)
pub struct VideoEncoder {
    output_ctx: ffmpeg::format::context::Output,
    encoder: ffmpeg::encoder::Video,
    audio_encoder: Option<ffmpeg::encoder::Audio>,
    scaler: scaling::Context,
    video_stream_index: usize,
    audio_stream_index: Option<usize>,
    frame_count: i64,
    audio_pts: i64,
    time_base: ffmpeg::Rational,
    audio_time_base: Option<ffmpeg::Rational>,
    width: u32,
    height: u32,
    // 오디오 버퍼링 (AAC 프레임 크기 정렬)
    audio_buffer: Vec<f32>,       // interleaved stereo (L, R, L, R, ...)
    audio_frame_size: usize,      // AAC 프레임당 채널당 샘플 수 (보통 1024)
    audio_channels: u32,
}

impl VideoEncoder {
    /// 비디오 인코더 생성 (오디오는 init_audio로 추가)
    pub fn new(
        output_path: &str,
        width: u32,
        height: u32,
        fps: f64,
        crf: u32,
        encoder_type: EncoderType,
    ) -> Result<Self, String> {
        ffmpeg::init().map_err(|e| format!("FFmpeg init failed: {}", e))?;

        // 출력 컨텍스트 생성 (MP4 포맷)
        let mut output_ctx = ffmpeg::format::output(output_path)
            .map_err(|e| format!("Failed to create output: {}", e))?;

        // H.264 인코더 찾기 (타입별 분기 + 자동 폴백)
        let (codec, codec_name) = Self::find_h264_encoder(encoder_type)?;

        eprintln!(
            "[ENCODER] 사용 인코더: {} (요청={:?})",
            codec_name,
            encoder_type
        );

        // 글로벌 헤더 플래그 사전 확인 (borrow 충돌 방지)
        let needs_global_header = output_ctx.format().flags()
            .contains(ffmpeg::format::flag::Flags::GLOBAL_HEADER);

        // 비디오 스트림 추가
        let mut video_stream = output_ctx.add_stream(codec)
            .map_err(|e| format!("Failed to add video stream: {}", e))?;

        let video_stream_index = video_stream.index();

        // time_base 설정 (1/fps 기반)
        let fps_num = (fps * 1000.0) as i32;
        let fps_den = 1000i32;
        let time_base = ffmpeg::Rational::new(fps_den, fps_num);

        // 인코더 설정 (new_with_codec으로 코덱을 컨텍스트에 연결)
        let mut encoder = codec::context::Context::new_with_codec(codec)
            .encoder()
            .video()
            .map_err(|e| format!("Failed to get video encoder: {}", e))?;

        encoder.set_width(width);
        encoder.set_height(height);
        encoder.set_format(Pixel::YUV420P);
        encoder.set_time_base(time_base);
        encoder.set_frame_rate(Some(ffmpeg::Rational::new(fps_num, fps_den)));

        // 인코더별 옵션 설정
        let mut opts = ffmpeg::Dictionary::new();
        match codec_name.as_str() {
            "libx264" => {
                opts.set("crf", &crf.to_string());
                opts.set("preset", "medium");
            }
            "h264_nvenc" => {
                // NVENC: VBR + CQ (Constant Quality) 모드
                opts.set("rc", "vbr");
                opts.set("cq", &crf.to_string());
                opts.set("preset", "p4"); // medium 상당
                eprintln!("[ENCODER] NVENC CQ={}", crf);
            }
            "h264_qsv" => {
                opts.set("global_quality", &crf.to_string());
                opts.set("preset", "medium");
                eprintln!("[ENCODER] QSV global_quality={}", crf);
            }
            "h264_amf" => {
                let bitrate = Self::crf_to_bitrate(crf, width, height);
                encoder.set_bit_rate(bitrate);
                eprintln!("[ENCODER] AMF bitrate={}kbps", bitrate / 1000);
            }
            _ => {
                let bitrate = Self::crf_to_bitrate(crf, width, height);
                encoder.set_bit_rate(bitrate);
                eprintln!("[ENCODER] {} bitrate={}kbps", codec_name, bitrate / 1000);
            }
        }

        // 글로벌 헤더 플래그 (MP4 컨테이너 호환)
        if needs_global_header {
            unsafe {
                (*encoder.as_mut_ptr()).flags |= codec::flag::Flags::GLOBAL_HEADER.bits() as i32;
            }
            eprintln!("[ENCODER] GLOBAL_HEADER 플래그 설정");
        }

        eprintln!(
            "[ENCODER] 인코더 열기: {}x{}, fmt={:?}, tb={}/{}",
            encoder.width(), encoder.height(), encoder.format(),
            time_base.numerator(), time_base.denominator(),
        );

        // open_as_with: 코덱 포인터를 명시적 전달
        let encoder = encoder.open_as_with(codec, opts)
            .map_err(|e| format!("Failed to open encoder: {}", e))?;

        eprintln!("[ENCODER] 비디오 인코더 열기 성공");

        // 스트림 파라미터 업데이트 (open 후 — extradata/SPS/PPS 반영)
        video_stream.set_parameters(&encoder);

        // RGBA → YUV420P 스케일러 (BICUBIC: 색상 변환 품질 최적화)
        let scaler = scaling::Context::get(
            Pixel::RGBA,
            width,
            height,
            Pixel::YUV420P,
            width,
            height,
            scaling::Flags::BICUBIC,
        )
        .map_err(|e| format!("Failed to create scaler: {}", e))?;

        Ok(Self {
            output_ctx,
            encoder,
            audio_encoder: None,
            scaler,
            video_stream_index,
            audio_stream_index: None,
            frame_count: 0,
            audio_pts: 0,
            time_base,
            audio_time_base: None,
            width,
            height,
            audio_buffer: Vec::new(),
            audio_frame_size: 1024,
            audio_channels: 2,
        })
    }

    /// AAC 오디오 인코더 초기화 (write_header 전에 호출)
    /// - sample_rate: 48000
    /// - channels: 2 (stereo)
    /// - bitrate: 192000 (192kbps)
    pub fn init_audio(&mut self, sample_rate: u32, channels: u32, bitrate: usize) -> Result<(), String> {
        let codec = ffmpeg::encoder::find(codec::Id::AAC)
            .ok_or("AAC 인코더를 찾을 수 없습니다")?;

        eprintln!("[ENCODER] AAC 인코더: {}", codec.name());

        let needs_global_header = self.output_ctx.format().flags()
            .contains(ffmpeg::format::flag::Flags::GLOBAL_HEADER);

        // 오디오 스트림 추가
        let mut audio_stream = self.output_ctx.add_stream(codec)
            .map_err(|e| format!("Failed to add audio stream: {}", e))?;

        let audio_stream_index = audio_stream.index();
        let audio_time_base = ffmpeg::Rational::new(1, sample_rate as i32);

        // 오디오 인코더 설정
        let mut audio_enc = codec::context::Context::new_with_codec(codec)
            .encoder()
            .audio()
            .map_err(|e| format!("Failed to get audio encoder: {}", e))?;

        audio_enc.set_rate(sample_rate as i32);
        audio_enc.set_channel_layout(ffmpeg::ChannelLayout::STEREO);
        audio_enc.set_format(ffmpeg::format::Sample::F32(ffmpeg::format::sample::Type::Planar));
        audio_enc.set_bit_rate(bitrate);
        audio_enc.set_time_base(audio_time_base);

        if needs_global_header {
            unsafe {
                (*audio_enc.as_mut_ptr()).flags |= codec::flag::Flags::GLOBAL_HEADER.bits() as i32;
            }
        }

        let audio_enc = audio_enc.open_as_with(codec, ffmpeg::Dictionary::new())
            .map_err(|e| format!("Failed to open audio encoder: {}", e))?;

        // AAC 프레임 크기 (보통 1024)
        let frame_size = unsafe { (*audio_enc.as_ptr()).frame_size as usize };
        let frame_size = if frame_size > 0 { frame_size } else { 1024 };

        eprintln!(
            "[ENCODER] AAC 오디오 인코더 성공: {}Hz {}ch, {}kbps, frame_size={}",
            sample_rate, channels, bitrate / 1000, frame_size
        );

        audio_stream.set_parameters(&audio_enc);

        self.audio_encoder = Some(audio_enc);
        self.audio_stream_index = Some(audio_stream_index);
        self.audio_time_base = Some(audio_time_base);
        self.audio_frame_size = frame_size;
        self.audio_channels = channels;

        Ok(())
    }

    /// H.264 인코더 찾기 (EncoderType에 따라 분기 + 자동 폴백)
    /// 반환: (Codec, codec_name)
    fn find_h264_encoder(encoder_type: EncoderType) -> Result<(ffmpeg::Codec, String), String> {
        match encoder_type {
            EncoderType::Auto => {
                // GPU 우선: NVENC → QSV → AMF → libx264 → generic
                let try_order = ["h264_nvenc", "h264_qsv", "h264_amf", "libx264"];
                for name in &try_order {
                    if let Some(codec) = ffmpeg::encoder::find_by_name(name) {
                        return Ok((codec, name.to_string()));
                    }
                }
                // 최후의 폴백: generic H264
                if let Some(codec) = ffmpeg::encoder::find(codec::Id::H264) {
                    return Ok((codec, codec.name().to_string()));
                }
                Err("H.264 인코더를 찾을 수 없습니다".to_string())
            }
            EncoderType::Software => {
                if let Some(codec) = ffmpeg::encoder::find_by_name("libx264") {
                    return Ok((codec, "libx264".to_string()));
                }
                if let Some(codec) = ffmpeg::encoder::find(codec::Id::H264) {
                    return Ok((codec, codec.name().to_string()));
                }
                Err("libx264 인코더를 찾을 수 없습니다".to_string())
            }
            EncoderType::Nvenc => {
                if let Some(codec) = ffmpeg::encoder::find_by_name("h264_nvenc") {
                    return Ok((codec, "h264_nvenc".to_string()));
                }
                eprintln!("[ENCODER] h264_nvenc 없음 → libx264 폴백");
                Self::find_h264_encoder(EncoderType::Software)
            }
            EncoderType::Qsv => {
                if let Some(codec) = ffmpeg::encoder::find_by_name("h264_qsv") {
                    return Ok((codec, "h264_qsv".to_string()));
                }
                eprintln!("[ENCODER] h264_qsv 없음 → libx264 폴백");
                Self::find_h264_encoder(EncoderType::Software)
            }
            EncoderType::Amf => {
                if let Some(codec) = ffmpeg::encoder::find_by_name("h264_amf") {
                    return Ok((codec, "h264_amf".to_string()));
                }
                eprintln!("[ENCODER] h264_amf 없음 → libx264 폴백");
                Self::find_h264_encoder(EncoderType::Software)
            }
        }
    }

    /// CRF → 대략적 bitrate 변환 (비 libx264 인코더용)
    /// 1080p 기준: CRF18→15Mbps, CRF23→8Mbps, CRF28→4Mbps
    fn crf_to_bitrate(crf: u32, width: u32, height: u32) -> usize {
        let pixels = (width as f64) * (height as f64);
        // 기준: 1080p (2073600px) → base 4Mbps
        let base_rate = pixels * 2.0; // pixels * 2 bps
        let multiplier = match crf {
            0..=15 => 8.0,   // ~16 Mbps @ 1080p
            16..=18 => 5.0,  // ~10 Mbps @ 1080p (고품질)
            19..=23 => 3.0,  // ~6 Mbps @ 1080p (표준)
            24..=28 => 1.5,  // ~3 Mbps @ 1080p
            _ => 1.0,        // ~2 Mbps @ 1080p
        };
        (base_rate * multiplier) as usize
    }

    /// 출력 파일 헤더 작성 (init_audio 후, 첫 프레임 인코딩 전에 호출)
    pub fn write_header(&mut self) -> Result<(), String> {
        eprintln!("[ENCODER] write_header 호출...");
        self.output_ctx.write_header()
            .map_err(|e| format!("Failed to write header: {}", e))?;
        eprintln!("[ENCODER] write_header 성공");
        Ok(())
    }

    /// RGBA 프레임 인코딩 → MP4에 기록
    pub fn encode_frame(&mut self, rgba_data: &[u8], width: u32, height: u32) -> Result<(), String> {
        // 해상도 검증
        if width != self.width || height != self.height {
            return Err(format!(
                "Frame dimensions mismatch: got {}x{}, expected {}x{}",
                width, height, self.width, self.height
            ));
        }

        let expected_size = (width * height * 4) as usize;
        if rgba_data.len() != expected_size {
            return Err(format!(
                "Invalid frame data size: got {}, expected {} ({}x{}x4)",
                rgba_data.len(), expected_size, width, height
            ));
        }

        // RGBA 데이터 → ffmpeg Video 프레임
        let mut src_frame = ffmpeg::frame::Video::new(Pixel::RGBA, width, height);
        {
            let linesize = src_frame.stride(0);
            let dst = src_frame.data_mut(0);
            for y in 0..height as usize {
                let src_offset = y * (width as usize * 4);
                let dst_offset = y * linesize;
                let row_size = width as usize * 4;
                dst[dst_offset..dst_offset + row_size]
                    .copy_from_slice(&rgba_data[src_offset..src_offset + row_size]);
            }
        }

        // RGBA → YUV420P 변환
        let mut yuv_frame = ffmpeg::frame::Video::empty();
        self.scaler.run(&src_frame, &mut yuv_frame)
            .map_err(|e| format!("Scaler failed: {}", e))?;

        // PTS 설정
        yuv_frame.set_pts(Some(self.frame_count));
        self.frame_count += 1;

        // 인코더에 프레임 전송
        self.encoder.send_frame(&yuv_frame)
            .map_err(|e| format!("Failed to send frame (pts={}): {}", self.frame_count, e))?;

        // 인코딩된 패킷 수신 → 출력에 기록
        self.receive_and_write_video_packets()?;

        // 처음 5프레임만 로그
        if self.frame_count <= 5 {
            eprintln!("[ENCODER] 비디오 프레임 {} 인코딩 완료 ({}x{})", self.frame_count, width, height);
        }

        Ok(())
    }

    /// YUV420P 프레임 직접 인코딩 (Export용 — RGBA→YUV 변환 건너뜀)
    /// yuv_data 레이아웃: [Y: w*h][U: w/2*h/2][V: w/2*h/2]
    pub fn encode_frame_yuv(&mut self, yuv_data: &[u8], width: u32, height: u32) -> Result<(), String> {
        if width != self.width || height != self.height {
            return Err(format!(
                "Frame dimensions mismatch: got {}x{}, expected {}x{}",
                width, height, self.width, self.height
            ));
        }

        let w = width as usize;
        let h = height as usize;
        let y_size = w * h;
        let half_w = w / 2;
        let half_h = h / 2;
        let uv_size = half_w * half_h;
        let expected_size = y_size + uv_size * 2;

        if yuv_data.len() != expected_size {
            return Err(format!(
                "YUV data size mismatch: got {}, expected {} ({}x{} YUV420P)",
                yuv_data.len(), expected_size, width, height
            ));
        }

        let mut yuv_frame = ffmpeg::frame::Video::new(Pixel::YUV420P, width, height);

        // Y plane 복사
        {
            let y_stride = yuv_frame.stride(0);
            let y_dst = yuv_frame.data_mut(0);
            for row in 0..h {
                let src_offset = row * w;
                let dst_offset = row * y_stride;
                y_dst[dst_offset..dst_offset + w]
                    .copy_from_slice(&yuv_data[src_offset..src_offset + w]);
            }
        }

        // U plane 복사
        {
            let u_stride = yuv_frame.stride(1);
            let u_dst = yuv_frame.data_mut(1);
            for row in 0..half_h {
                let src_offset = y_size + row * half_w;
                let dst_offset = row * u_stride;
                u_dst[dst_offset..dst_offset + half_w]
                    .copy_from_slice(&yuv_data[src_offset..src_offset + half_w]);
            }
        }

        // V plane 복사
        {
            let v_stride = yuv_frame.stride(2);
            let v_dst = yuv_frame.data_mut(2);
            for row in 0..half_h {
                let src_offset = y_size + uv_size + row * half_w;
                let dst_offset = row * v_stride;
                v_dst[dst_offset..dst_offset + half_w]
                    .copy_from_slice(&yuv_data[src_offset..src_offset + half_w]);
            }
        }

        // PTS 설정
        yuv_frame.set_pts(Some(self.frame_count));
        self.frame_count += 1;

        // 인코더에 프레임 전송
        self.encoder.send_frame(&yuv_frame)
            .map_err(|e| format!("Failed to send YUV frame (pts={}): {}", self.frame_count, e))?;

        self.receive_and_write_video_packets()?;

        if self.frame_count <= 5 {
            eprintln!("[ENCODER] YUV 프레임 {} 인코딩 완료 ({}x{})", self.frame_count, width, height);
        }

        Ok(())
    }

    /// f32 PCM 오디오 인코딩 → MP4에 기록
    /// samples: interleaved stereo f32 (L, R, L, R, ...)
    pub fn encode_audio_samples(&mut self, samples: &[f32]) -> Result<(), String> {
        // 오디오 인코더 없으면 스킵
        let mut audio_enc = match self.audio_encoder.take() {
            Some(e) => e,
            None => return Ok(()),
        };

        self.audio_buffer.extend_from_slice(samples);

        let result = self.flush_audio_buffer(&mut audio_enc);

        // 인코더를 다시 넣기 (에러 여부 무관)
        self.audio_encoder = Some(audio_enc);

        result
    }

    /// 오디오 버퍼에서 완전한 AAC 프레임만큼 인코딩
    fn flush_audio_buffer(&mut self, audio_enc: &mut ffmpeg::encoder::Audio) -> Result<(), String> {
        let frame_size = self.audio_frame_size;
        let channels = self.audio_channels as usize;
        let samples_per_frame = frame_size * channels; // interleaved 기준
        let audio_stream_idx = match self.audio_stream_index {
            Some(idx) => idx,
            None => return Ok(()),
        };
        let audio_tb = match self.audio_time_base {
            Some(tb) => tb,
            None => return Ok(()),
        };

        while self.audio_buffer.len() >= samples_per_frame {
            // FLTP 오디오 프레임 생성
            let mut frame = ffmpeg::frame::Audio::new(
                ffmpeg::format::Sample::F32(ffmpeg::format::sample::Type::Planar),
                frame_size,
                ffmpeg::ChannelLayout::STEREO,
            );
            frame.set_pts(Some(self.audio_pts));
            frame.set_rate(48000);
            self.audio_pts += frame_size as i64;

            // Deinterleave: (L,R,L,R,...) → plane0=[L,L,...], plane1=[R,R,...]
            for ch in 0..channels {
                let plane = frame.data_mut(ch);
                let plane_f32 = unsafe {
                    std::slice::from_raw_parts_mut(
                        plane.as_mut_ptr() as *mut f32,
                        frame_size,
                    )
                };
                for i in 0..frame_size {
                    plane_f32[i] = self.audio_buffer[i * channels + ch];
                }
            }

            self.audio_buffer.drain(..samples_per_frame);

            // 인코더에 프레임 전송
            audio_enc.send_frame(&frame)
                .map_err(|e| format!("Failed to send audio frame: {}", e))?;

            // 인코딩된 패킷 수신 → 출력에 기록
            let mut packet = ffmpeg::Packet::empty();
            while audio_enc.receive_packet(&mut packet).is_ok() {
                packet.set_stream(audio_stream_idx);
                packet.rescale_ts(
                    audio_tb,
                    self.output_ctx.stream(audio_stream_idx)
                        .ok_or("Audio stream not found")?
                        .time_base(),
                );
                packet.write_interleaved(&mut self.output_ctx)
                    .map_err(|e| format!("Failed to write audio packet: {}", e))?;
            }
        }

        Ok(())
    }

    /// 인코딩 완료 (flush + trailer)
    pub fn finish(&mut self) -> Result<(), String> {
        eprintln!("[ENCODER] finish 호출 (비디오 {}프레임, 오디오 {}샘플)",
            self.frame_count, self.audio_pts);

        // 비디오 flush
        self.encoder.send_eof()
            .map_err(|e| format!("Failed to send video EOF: {}", e))?;
        self.receive_and_write_video_packets()?;
        eprintln!("[ENCODER] 비디오 flush 완료");

        // 오디오 flush (잔여 버퍼 + EOF)
        if let Some(mut audio_enc) = self.audio_encoder.take() {
            // 잔여 샘플을 0으로 패딩하여 마지막 프레임 완성
            let channels = self.audio_channels as usize;
            let remaining = self.audio_buffer.len() / channels;
            if remaining > 0 {
                let pad = (self.audio_frame_size - remaining) * channels;
                self.audio_buffer.extend(std::iter::repeat(0.0f32).take(pad));
                self.flush_audio_buffer(&mut audio_enc)?;
            }

            // 오디오 EOF
            audio_enc.send_eof()
                .map_err(|e| format!("Failed to send audio EOF: {}", e))?;

            // 잔여 오디오 패킷 기록
            let audio_stream_idx = self.audio_stream_index.unwrap();
            let audio_tb = self.audio_time_base.unwrap();
            let mut packet = ffmpeg::Packet::empty();
            while audio_enc.receive_packet(&mut packet).is_ok() {
                packet.set_stream(audio_stream_idx);
                packet.rescale_ts(
                    audio_tb,
                    self.output_ctx.stream(audio_stream_idx)
                        .ok_or("Audio stream not found")?
                        .time_base(),
                );
                packet.write_interleaved(&mut self.output_ctx)
                    .map_err(|e| format!("Failed to write audio packet: {}", e))?;
            }

            self.audio_encoder = Some(audio_enc);
            eprintln!("[ENCODER] 오디오 flush 완료");
        }

        // 파일 트레일러 작성
        self.output_ctx.write_trailer()
            .map_err(|e| format!("Failed to write trailer: {}", e))?;
        eprintln!("[ENCODER] write_trailer 성공 → 파일 완성");

        Ok(())
    }

    /// 비디오 패킷 수신 → 출력 파일에 기록
    fn receive_and_write_video_packets(&mut self) -> Result<(), String> {
        let mut packet = ffmpeg::Packet::empty();
        while self.encoder.receive_packet(&mut packet).is_ok() {
            packet.set_stream(self.video_stream_index);
            packet.rescale_ts(
                self.time_base,
                self.output_ctx.stream(self.video_stream_index)
                    .ok_or("Video stream not found")?
                    .time_base(),
            );
            packet.write_interleaved(&mut self.output_ctx)
                .map_err(|e| format!("Failed to write video packet: {}", e))?;
        }
        Ok(())
    }

    /// 너비 반환
    pub fn width(&self) -> u32 { self.width }
    /// 높이 반환
    pub fn height(&self) -> u32 { self.height }
}
