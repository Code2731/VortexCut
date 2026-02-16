//! 링 버퍼 기반 프레임 큐 (재생 프리페치용)
//! Playback 시 N프레임 미리 디코딩하여 스크럽 지연 제거

use crate::rendering::RenderedFrame;
use std::collections::VecDeque;

const QUEUE_CAPACITY: usize = 16;

/// 재생용 프레임 큐 (Mutex로 감싸서 사용)
pub struct FrameQueue {
    buffer: VecDeque<RenderedFrame>,
    max_len: usize,
}

impl FrameQueue {
    pub fn new() -> Self {
        Self {
            buffer: VecDeque::with_capacity(QUEUE_CAPACITY),
            max_len: QUEUE_CAPACITY,
        }
    }

    /// 큐에 프레임 추가 (용량 초과 시 가장 오래된 것 evict)
    pub fn push(&mut self, frame: RenderedFrame) {
        while self.buffer.len() >= self.max_len {
            let _ = self.buffer.pop_front();
        }
        self.buffer.push_back(frame);
    }

    /// timestamp에 가장 가까운 프레임 조회 (소비하지 않음)
    /// tolerance_ms: 허용 오차 (50ms = ~1.5프레임 @30fps)
    /// 인덱스 기반 검색으로 최종 선택 프레임만 clone (중간 clone 제거)
    pub fn peek_nearest(&self, timestamp_ms: i64, tolerance_ms: i64) -> Option<RenderedFrame> {
        let mut best_idx: Option<(i64, usize)> = None;
        for (i, f) in self.buffer.iter().enumerate() {
            let diff = (f.timestamp_ms - timestamp_ms).abs();
            if diff <= tolerance_ms {
                if best_idx.as_ref().map_or(true, |(d, _)| diff < *d) {
                    best_idx = Some((diff, i));
                }
            }
        }
        best_idx.map(|(_, i)| self.buffer[i].clone())
    }

    /// 가장 오래된 프레임 제거 후 반환
    pub fn pop(&mut self) -> Option<RenderedFrame> {
        self.buffer.pop_front()
    }

    pub fn clear(&mut self) {
        self.buffer.clear();
    }

    pub fn len(&self) -> usize {
        self.buffer.len()
    }
}
