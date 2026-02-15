// 타임라인 엔진 모듈
// 클립, 트랙, 타임라인 관리

pub mod clip;
pub mod track;
pub mod timeline;

pub use clip::{ClipType, VideoClip, AudioClip, TransitionType};
pub use track::{VideoTrack, AudioTrack};
pub use timeline::Timeline;
