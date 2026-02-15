# VortexCut

> **Rust** 렌더링 엔진 + **C# Avalonia** UI 기반 크로스 플랫폼 영상 편집 프로그램

[![Build Status](https://img.shields.io/badge/build-passing-brightgreen)]()
[![Rust](https://img.shields.io/badge/Rust-2021-orange)]()
[![.NET](https://img.shields.io/badge/.NET-8.0-blue)]()
[![License](https://img.shields.io/badge/license-MIT-green)]()

## 프로젝트 개요

VortexCut은 고성능 **Rust 렌더링 엔진**(ffmpeg-next)과 현대적인 **C# Avalonia UI**를 결합한 영상 편집 소프트웨어입니다.

### 주요 특징

- 🚀 **고성능 렌더링**: Rust + FFmpeg 기반 네이티브 렌더링 엔진 (LRU FrameCache, 상태 머신 디코더)
- 🎨 **현대적인 UI**: C# Avalonia로 구현된 크로스 플랫폼 UI (DaVinci Resolve 스타일)
- 📝 **타임라인 편집**: 멀티 트랙 비디오/오디오/자막 편집, Razor 분할, 스냅, 링크 클립
- 🎬 **고품질 Export**: YUV420P 직접 전달 + GPU 가속 인코딩 (NVENC/QSV/AMF 자동 탐지)
- 🔊 **실시간 오디오**: cpal WASAPI 재생 + AudioMixer 다중 클립 합성
- 🎨 **색보정 이펙트**: Brightness, Contrast, Saturation, Temperature (Rust RGBA 픽셀 연산)
- 📝 **자막 시스템**: SRT 임포트, 타임라인 편집, Export 번인 (Avalonia→RGBA→Rust 알파 블렌딩)
- ↩️ **Undo/Redo**: Command 패턴 기반, Razor/이동/트림/삭제 모두 지원
- 🎥 **Clip Monitor**: Source Monitor 독립 프리뷰, Mark In/Out, 스마트 타임라인 삽입

### 기술 스택

| 구성 요소 | 기술 |
|----------|------|
| 렌더링 엔진 | Rust 2021, ffmpeg-next 8.0 |
| 인코딩 | H.264 (libx264/NVENC/QSV/AMF) + AAC, YUV420P 직접 파이프라인 |
| 오디오 | cpal 0.15 (WASAPI), 48kHz stereo |
| UI 프레임워크 | C# .NET 8, Avalonia UI 11 |
| 연동 방식 | FFI (P/Invoke) |
| 타겟 플랫폼 | Windows, macOS |

## 프로젝트 구조

```
VortexCut/
├── rust-engine/              # Rust 렌더링 엔진 (cdylib)
│   ├── src/
│   │   ├── ffi/              # FFI 인터페이스
│   │   ├── ffmpeg/           # FFmpeg 래퍼
│   │   ├── timeline/         # 타임라인 엔진
│   │   ├── rendering/        # 렌더링 파이프라인 (LRU 캐시 + 이펙트 + 트랜지션)
│   │   ├── encoding/         # Export (H.264+AAC 인코딩, 오디오 믹서)
│   │   ├── audio/            # 실시간 오디오 재생 (cpal)
│   │   └── subtitle/         # 자막 처리
│   └── Cargo.toml
├── VortexCut.Core/           # C# 공통 모델 + 서비스 인터페이스
│   ├── Models/               # ClipModel, VideoInfo, Project, ...
│   └── Interfaces/           # IProjectService, IRenderService, IExportService, ...
├── VortexCut.Interop/        # Rust-C# P/Invoke 레이어
│   └── Services/             # RenderService, ExportService, AudioPlaybackService (인터페이스 구현)
├── VortexCut.UI/             # Avalonia UI (DI 컨테이너, MVVM)
│   ├── ViewModels/           # MainViewModel (partial), PreviewViewModel, TimelineViewModel, ...
│   ├── Views/                # MainWindow, InspectorView, ...
│   ├── Controls/             # ClipCanvasPanel (partial, 7 파일), TimelineHeader, ...
│   └── Services/             # ProjectService (IProjectService 구현), Actions (Undo/Redo)
├── VortexCut.Tests/          # C# 단위 테스트
└── docs/
    └── TECHSPEC.md           # 기술 명세서
```

### 아키텍처 원칙

- **SOLID 원칙**: 서비스 인터페이스 분리 (IProjectService, IRenderService, IExportService, IAudioPlaybackService)
- **DI 컨테이너**: Microsoft.Extensions.DependencyInjection — App.axaml.cs Composition Root
- **partial class 분할**: 대형 클래스 SRP 적용 (ClipCanvasPanel 7파일, MainViewModel 3파일)
- **계층 분리**: Core(모델+인터페이스) → Interop(FFI 구현) → UI(ViewModel+View)

## 빌드 방법

### 필수 요구사항

- **Rust** 1.70 이상 ([설치 링크](https://rustup.rs/))
- **.NET SDK** 8.0 이상 ([설치 링크](https://dotnet.microsoft.com/download))
- **FFmpeg** 개발 라이브러리 (나중에 필요)

### 1. Rust 엔진 빌드

```bash
cd rust-engine
cargo build --release
```

생성된 DLL:
- Windows: `target/release/rust_engine.dll`
- macOS: `target/release/librust_engine.dylib`

### 2. C# 프로젝트 빌드

```bash
# 솔루션 빌드
dotnet build VortexCut.sln -c Release

# 테스트 실행
dotnet test VortexCut.Tests
```

### 3. 통합 빌드 (권장)

**Windows (PowerShell)**:
```powershell
.\scripts\build-all.ps1
```

**macOS/Linux (Bash)**:
```bash
chmod +x scripts/build-all.sh
./scripts/build-all.sh
```

## 개발 시작하기

### 1. 저장소 클론

```bash
git clone https://github.com/your-username/VortexCut.git
cd VortexCut
```

### 2. 의존성 설치

```bash
# Rust 의존성
cd rust-engine
cargo fetch

# .NET 의존성
cd ..
dotnet restore
```

### 3. 개발 환경 설정

**Visual Studio Code** (권장):
- Rust Analyzer 확장 설치
- C# Dev Kit 확장 설치

**Visual Studio 2022**:
- Rust 플러그인 설치 (선택사항)
- .NET 8 워크로드 설치

### 4. FFI 테스트 실행

```bash
# Rust 빌드
cd rust-engine
cargo build --release

# DLL 복사
cp target/release/rust_engine.dll ../VortexCut.Tests/bin/Debug/net8.0/

# 테스트 실행
cd ..
dotnet test VortexCut.Tests
```

## 현재 상태

### ✅ Phase 10 완료 (2026-02-15) - 아키텍처 리팩토링 (SOLID + DI)

- [x] **ClipCanvasPanel partial class 분할** - 2,541줄 God Object → 7개 파일 (Rendering, Input, DragDrop 등)
- [x] **서비스 인터페이스 추출** - IProjectService, IRenderService, IExportService, IAudioPlaybackService, IRenderedFrame
- [x] **DI 컨테이너** - Microsoft.Extensions.DependencyInjection, App.axaml.cs Composition Root
- [x] **InspectorView 분리** - 383줄 code-behind → InspectorViewModel 비즈니스 로직 분리
- [x] **MainViewModel 분할** - 630줄 → 3개 partial class (Core, FileOperations, ClipOperations)
- [x] **계층 의존성 정리** - ViewModel에서 Interop 직접 참조 제거, Core 인터페이스만 의존

### ✅ Phase 9 완료 (2026-02-15) - GPU 하드웨어 가속 인코딩

- [x] **NVENC/QSV/AMF 자동 탐지** - `exporter_detect_encoders()` 비트마스크
- [x] **Auto 모드** - NVENC → QSV → AMF → libx264 순서 시도 + 자동 폴백
- [x] **인코더별 품질 옵션** - NVENC(VBR+CQ), QSV(global_quality), AMF(bitrate)
- [x] **Export Dialog UI** - 인코더 선택 ComboBox

### ✅ Phase 8 완료 (2026-02-14) - 색보정 이펙트 시스템

- [x] **4가지 색보정 이펙트** - Brightness, Contrast, Saturation, Temperature
- [x] **Rust RGBA 픽셀 연산** - 디코딩 후 캐시 전 적용, BT.709 luminance
- [x] **실시간 프리뷰** - Inspector Color 탭 Slider 조작 → 즉시 반영
- [x] **프로젝트 직렬화** - 이펙트 값 저장/복원

### ✅ Phase 7 완료 (2026-02-14) - 자막 + Clip Monitor

- [x] **자막 편집 시스템** - SRT 임포트, 타임라인 편집, Export 번인
- [x] **Clip Monitor (Source Monitor)** - Project Bin 더블클릭 → 독립 프리뷰
- [x] **Mark In/Out** - 범위 지정 후 타임라인 삽입 (겹침 감지 → 빈 트랙 자동 선택)

### ✅ Phase 6 완료 (2026-02-14) - Export 파이프라인 완성

- [x] **고품질 Export 파이프라인**
  - YUV420P 직접 전달 (RGBA 이중 변환 제거 → 무손실 색공간)
  - H.264 인코딩 (libx264 CRF / 시스템 인코더 bitrate 자동 선택)
  - AAC 오디오 인코딩 (48kHz stereo 192kbps)
  - 비ASCII(한글) 출력 경로 지원
  - Export 프리셋 (1080p 고품질/표준, 720p, 4K UHD)

### ✅ Phase 5 완료 (2026-02-13) - Undo/Redo + 렌더링 재설계

- [x] **Undo/Redo 시스템** - Command 패턴, Ctrl+Z/Ctrl+Shift+Z
- [x] **렌더링 파이프라인 재설계** - 상태 머신 디코더, LRU FrameCache, Scrub/Playback 모드 분리
- [x] **실시간 오디오 재생** - cpal WASAPI, AudioMixer, leftover 캐리 버퍼

### ✅ Phase 1~4 완료 (2026-02-10) - 타임라인 편집

- [x] **Rust FFI 렌더링 엔진** - FFmpeg 통합, Timeline 엔진
- [x] **DaVinci Resolve 스타일 타임라인 UI** - 그라데이션, 60FPS 애니메이션
- [x] **타임라인 편집** - Razor, Snap, 링크 클립, 키프레임, SMPTE 타임코드
- [x] **썸네일 스트립** - 비동기 생성, 캐싱, LOD 시스템

### 📋 계획
- [ ] 트랜지션 시스템 (Crossfade/Dissolve/Wipe)
- [ ] 키프레임 애니메이션
- [ ] C# 유닛 테스트
- [ ] 플러그인 시스템

## 문서

- [TECHSPEC.md](docs/TECHSPEC.md) - 기술 명세서

## 기여 방법

기여를 환영합니다! 다음 단계를 따라주세요:

1. Fork the repository
2. Create your feature branch (`git checkout -b feature/AmazingFeature`)
3. Commit your changes (`git commit -m 'Add some AmazingFeature'`)
4. Push to the branch (`git push origin feature/AmazingFeature`)
5. Open a Pull Request

## 라이선스

이 프로젝트는 MIT 라이선스 하에 배포됩니다.

## 제작자

- **Claude Opus 4.6** - AI 개발 어시스턴트
- **사용자** - 프로젝트 설계 및 디렉션

## 참고 자료

- [Rust FFI Omnibus](http://jakegoulding.com/rust-ffi-omnibus/)
- [ffmpeg-next GitHub](https://github.com/zmwangx/rust-ffmpeg)
- [Avalonia UI Docs](https://docs.avaloniaui.net/)
- [Microsoft P/Invoke Docs](https://docs.microsoft.com/en-us/dotnet/standard/native-interop/pinvoke)

---

**마지막 업데이트**: 2026-02-15
