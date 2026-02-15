// 트랜지션 블렌딩 함수 — RGBA 픽셀 연산
// outgoing (나가는 클립) 버퍼에 in-place로 결과 기록

use crate::timeline::TransitionType;

/// 트랜지션 적용 (메인 디스패처)
/// outgoing/incoming: 동일 크기 RGBA 버퍼 (width * height * 4)
/// progress: 0.0 = outgoing만, 1.0 = incoming만
pub fn apply_transition(
    outgoing: &mut [u8],
    incoming: &[u8],
    width: u32,
    height: u32,
    progress: f64,
    transition_type: TransitionType,
) {
    match transition_type {
        TransitionType::None | TransitionType::Crossfade => {
            blend_crossfade(outgoing, incoming, width, height, progress);
        }
        TransitionType::FadeBlack => {
            blend_fade_black(outgoing, incoming, width, height, progress);
        }
        TransitionType::WipeLeft => {
            blend_wipe_horizontal(outgoing, incoming, width, height, progress, false);
        }
        TransitionType::WipeRight => {
            blend_wipe_horizontal(outgoing, incoming, width, height, progress, true);
        }
        TransitionType::WipeUp => {
            blend_wipe_vertical(outgoing, incoming, width, height, progress, false);
        }
        TransitionType::WipeDown => {
            blend_wipe_vertical(outgoing, incoming, width, height, progress, true);
        }
    }
}

/// 크로스페이드 (디졸브): pixel = A*(1-p) + B*p
fn blend_crossfade(
    outgoing: &mut [u8],
    incoming: &[u8],
    width: u32,
    height: u32,
    progress: f64,
) {
    let pixel_count = (width * height) as usize;
    let p = progress as f32;
    let inv_p = 1.0 - p;

    for i in 0..pixel_count {
        let idx = i * 4;
        if idx + 3 >= outgoing.len() || idx + 3 >= incoming.len() { break; }

        outgoing[idx]     = (outgoing[idx] as f32 * inv_p + incoming[idx] as f32 * p) as u8;
        outgoing[idx + 1] = (outgoing[idx + 1] as f32 * inv_p + incoming[idx + 1] as f32 * p) as u8;
        outgoing[idx + 2] = (outgoing[idx + 2] as f32 * inv_p + incoming[idx + 2] as f32 * p) as u8;
        outgoing[idx + 3] = 255;
    }
}

/// 페이드 스루 블랙
/// p < 0.5: outgoing → 검정 (alpha = 1 - 2p)
/// p >= 0.5: 검정 → incoming (alpha = 2*(p - 0.5))
fn blend_fade_black(
    outgoing: &mut [u8],
    incoming: &[u8],
    width: u32,
    height: u32,
    progress: f64,
) {
    let pixel_count = (width * height) as usize;

    if progress <= 0.5 {
        let alpha = (1.0 - progress * 2.0) as f32;
        for i in 0..pixel_count {
            let idx = i * 4;
            if idx + 3 >= outgoing.len() { break; }
            outgoing[idx]     = (outgoing[idx] as f32 * alpha) as u8;
            outgoing[idx + 1] = (outgoing[idx + 1] as f32 * alpha) as u8;
            outgoing[idx + 2] = (outgoing[idx + 2] as f32 * alpha) as u8;
            outgoing[idx + 3] = 255;
        }
    } else {
        let alpha = ((progress - 0.5) * 2.0) as f32;
        for i in 0..pixel_count {
            let idx = i * 4;
            if idx + 3 >= outgoing.len() || idx + 3 >= incoming.len() { break; }
            outgoing[idx]     = (incoming[idx] as f32 * alpha) as u8;
            outgoing[idx + 1] = (incoming[idx + 1] as f32 * alpha) as u8;
            outgoing[idx + 2] = (incoming[idx + 2] as f32 * alpha) as u8;
            outgoing[idx + 3] = 255;
        }
    }
}

/// 와이프 수평: progress 위치의 수직선 기준으로 분리
/// reverse=false (WipeLeft): 왼쪽에서 incoming 밀고 들어옴
/// reverse=true (WipeRight): 오른쪽에서 incoming 밀고 들어옴
fn blend_wipe_horizontal(
    outgoing: &mut [u8],
    incoming: &[u8],
    width: u32,
    height: u32,
    progress: f64,
    reverse: bool,
) {
    let w = width as usize;
    let h = height as usize;
    let boundary = (w as f64 * progress) as usize;

    for row in 0..h {
        for col in 0..w {
            let use_incoming = if reverse {
                col >= w.saturating_sub(boundary)
            } else {
                col < boundary
            };

            if use_incoming {
                let idx = (row * w + col) * 4;
                if idx + 3 >= outgoing.len() || idx + 3 >= incoming.len() { continue; }
                outgoing[idx]     = incoming[idx];
                outgoing[idx + 1] = incoming[idx + 1];
                outgoing[idx + 2] = incoming[idx + 2];
                outgoing[idx + 3] = 255;
            }
        }
    }
}

/// 와이프 수직
/// reverse=false (WipeUp): 위에서 incoming 밀고 들어옴
/// reverse=true (WipeDown): 아래에서 incoming 밀고 들어옴
fn blend_wipe_vertical(
    outgoing: &mut [u8],
    incoming: &[u8],
    width: u32,
    height: u32,
    progress: f64,
    reverse: bool,
) {
    let w = width as usize;
    let h = height as usize;
    let boundary = (h as f64 * progress) as usize;

    for row in 0..h {
        let use_incoming = if reverse {
            row >= h.saturating_sub(boundary)
        } else {
            row < boundary
        };

        if use_incoming {
            let row_start = row * w * 4;
            let row_end = row_start + w * 4;
            if row_end <= outgoing.len() && row_end <= incoming.len() {
                outgoing[row_start..row_end].copy_from_slice(&incoming[row_start..row_end]);
            }
        }
    }
}
