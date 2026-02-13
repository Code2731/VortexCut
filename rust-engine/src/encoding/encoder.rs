// 비디오/오디오 인코더 - FFmpeg 기반 H.264 + AAC 인코딩
// RGBA 프레임 → YUV420P 변환 → H.264 인코딩 → MP4 먹싱

use ffmpeg_next as ffmpeg;
use ffmpeg::format::Pixel;
use ffmpeg::codec;
use ffmpeg::software::scaling;

/// 비디오 인코더 (H.264 + MP4 컨테이너)
pub struct VideoEncoder {
    output_ctx: ffmpeg::format::context::Output,
    encoder: ffmpeg::encoder::Video,
    scaler: scaling::Context,
    video_stream_index: usize,
    frame_count: i64,
    time_base: ffmpeg::Rational,
    width: u32,
    height: u32,
}

impl VideoEncoder {
    /// 비디오 인코더 생성
    /// - output_path: MP4 출력 경로
    /// - width/height: 출력 해상도
    /// - fps: 프레임레이트
    /// - crf: 품질 (0=무손실, 23=기본, 51=최저)
    pub fn new(
        output_path: &str,
        width: u32,
        height: u32,
        fps: f64,
        crf: u32,
    ) -> Result<Self, String> {
        ffmpeg::init().map_err(|e| format!("FFmpeg init failed: {}", e))?;

        // 출력 컨텍스트 생성 (MP4 포맷)
        let mut output_ctx = ffmpeg::format::output(output_path)
            .map_err(|e| format!("Failed to create output: {}", e))?;

        // H.264 인코더 찾기
        let codec = ffmpeg::encoder::find(codec::Id::H264)
            .ok_or("H.264 encoder not found")?;

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

        // 인코더 설정
        let mut encoder = codec::context::Context::from_parameters(video_stream.parameters())
            .map_err(|e| format!("Failed to create encoder context: {}", e))?
            .encoder()
            .video()
            .map_err(|e| format!("Failed to get video encoder: {}", e))?;

        encoder.set_width(width);
        encoder.set_height(height);
        encoder.set_format(Pixel::YUV420P);
        encoder.set_time_base(time_base);
        encoder.set_frame_rate(Some(ffmpeg::Rational::new(fps_num, fps_den)));

        // CRF 품질 설정 + 인코딩 속도
        let mut opts = ffmpeg::Dictionary::new();
        opts.set("crf", &crf.to_string());
        opts.set("preset", "medium");
        // 글로벌 헤더 플래그 (MP4 컨테이너 호환)
        if needs_global_header {
            unsafe {
                (*encoder.as_mut_ptr()).flags |= codec::flag::Flags::GLOBAL_HEADER.bits() as i32;
            }
        }

        let encoder = encoder.open_with(opts)
            .map_err(|e| format!("Failed to open encoder: {}", e))?;

        // 스트림 파라미터 업데이트
        video_stream.set_parameters(&encoder);

        // RGBA → YUV420P 스케일러
        let scaler = scaling::Context::get(
            Pixel::RGBA,
            width,
            height,
            Pixel::YUV420P,
            width,
            height,
            scaling::Flags::FAST_BILINEAR,
        )
        .map_err(|e| format!("Failed to create scaler: {}", e))?;

        Ok(Self {
            output_ctx,
            encoder,
            scaler,
            video_stream_index,
            frame_count: 0,
            time_base,
            width,
            height,
        })
    }

    /// 출력 파일 헤더 작성 (첫 프레임 인코딩 전에 호출)
    pub fn write_header(&mut self) -> Result<(), String> {
        self.output_ctx.write_header()
            .map_err(|e| format!("Failed to write header: {}", e))
    }

    /// RGBA 프레임 인코딩 → MP4에 기록
    pub fn encode_frame(&mut self, rgba_data: &[u8], width: u32, height: u32) -> Result<(), String> {
        // 입력 크기 검증
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
            .map_err(|e| format!("Failed to send frame: {}", e))?;

        // 인코딩된 패킷 수신 → 출력에 기록
        self.receive_and_write_packets()?;

        Ok(())
    }

    /// 인코딩 완료 (flush + trailer)
    pub fn finish(&mut self) -> Result<(), String> {
        // flush: EOF 전송
        self.encoder.send_eof()
            .map_err(|e| format!("Failed to send EOF: {}", e))?;

        // 남은 패킷 모두 기록
        self.receive_and_write_packets()?;

        // 파일 트레일러 작성
        self.output_ctx.write_trailer()
            .map_err(|e| format!("Failed to write trailer: {}", e))?;

        Ok(())
    }

    /// 인코더에서 패킷 수신 → 출력 파일에 기록
    fn receive_and_write_packets(&mut self) -> Result<(), String> {
        let mut packet = ffmpeg::Packet::empty();
        while self.encoder.receive_packet(&mut packet).is_ok() {
            packet.set_stream(self.video_stream_index);
            // time_base 변환 (인코더 → 스트림)
            packet.rescale_ts(
                self.time_base,
                self.output_ctx.stream(self.video_stream_index)
                    .ok_or("Video stream not found")?
                    .time_base(),
            );
            packet.write_interleaved(&mut self.output_ctx)
                .map_err(|e| format!("Failed to write packet: {}", e))?;
        }
        Ok(())
    }

    /// 너비 반환
    pub fn width(&self) -> u32 { self.width }
    /// 높이 반환
    pub fn height(&self) -> u32 { self.height }
}
