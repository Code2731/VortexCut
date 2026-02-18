using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VortexCut.Core.Services;
using VortexCut.UI.Services;

namespace VortexCut.UI.ViewModels;

/// <summary>
/// Whisper 자동 자막 다이얼로그 ViewModel
/// ExportViewModel의 폴링 패턴을 따름
/// </summary>
public partial class WhisperDialogViewModel : ViewModelBase
{
    private readonly WhisperService _whisperService = new();
    private CancellationTokenSource? _cts;

    /// <summary>인식할 미디어 파일 경로 (열기 전에 설정)</summary>
    public string MediaPath { get; set; } = string.Empty;

    /// <summary>완료 시 SubtitleEntry 리스트 반환 (MainViewModel이 핸들링)</summary>
    public Action<List<SubtitleEntry>>? OnTranscribeComplete { get; set; }

    /// <summary>다이얼로그 닫기 요청 (code-behind에서 핸들링)</summary>
    public Action? RequestClose { get; set; }

    // ─── 모델 선택 ──────────────────────────────────────────────

    /// <summary>사용 가능한 모델 목록 (디렉토리 스캔 결과)</summary>
    public ObservableCollection<WhisperModelItem> AvailableModels { get; } = new();

    [ObservableProperty]
    private WhisperModelItem? _selectedModel;

    [ObservableProperty]
    private string _modelPath = string.Empty;

    [ObservableProperty]
    private int _selectedLanguageIndex = 1; // 기본값: 한국어 (ko)

    // ─── 상태 프로퍼티 ──────────────────────────────────────────────

    [ObservableProperty]
    private int _progress = 0;

    [ObservableProperty]
    private bool _isRunning = false;

    [ObservableProperty]
    private string _statusText = "준비";

    [ObservableProperty]
    private string? _errorMessage;

    // ─── 언어 목록 ──────────────────────────────────────────────────

    /// <summary>UI 표시용 언어 목록 (ComboBox 바인딩)</summary>
    public static readonly string[] LanguageDisplayNames =
    {
        "자동 감지",
        "한국어 (ko)",
        "영어 (en)",
        "일본어 (ja)",
        "중국어 (zh)",
        "스페인어 (es)",
        "프랑스어 (fr)",
        "독일어 (de)",
    };

    /// <summary>언어 코드 목록 (LanguageDisplayNames와 인덱스 대응)</summary>
    private static readonly string[] LanguageCodes =
    {
        "",    // 자동 감지
        "ko",  // 한국어
        "en",  // 영어
        "ja",  // 일본어
        "zh",  // 중국어
        "es",  // 스페인어
        "fr",  // 프랑스어
        "de",  // 독일어
    };

    /// <summary>선택된 언어 코드</summary>
    private string GetSelectedLanguageCode()
    {
        int idx = SelectedLanguageIndex;
        return idx >= 0 && idx < LanguageCodes.Length ? LanguageCodes[idx] : "";
    }

    // ─── 초기화 ─────────────────────────────────────────────────────

    public WhisperDialogViewModel()
    {
        ScanModels();
    }

    /// <summary>models/whisper 디렉토리에서 ggml 모델 스캔</summary>
    private void ScanModels()
    {
        var modelDir = Path.Combine(AppContext.BaseDirectory, "models", "whisper");
        if (!Directory.Exists(modelDir)) return;

        // 정의된 모델 순서 (큰 모델 우선)
        var knownModels = new (string file, string label, string desc)[]
        {
            ("ggml-large-v3.bin",  "Large V3",  "~3.1GB — 최고 정확도"),
            ("ggml-large.bin",     "Large",     "~3.1GB — 최고 정확도"),
            ("ggml-medium.bin",    "Medium",    "~1.5GB — 높은 정확도 (추천)"),
            ("ggml-small.bin",     "Small",     "~466MB — 보통 정확도"),
            ("ggml-base.bin",      "Base",      "~142MB — 낮은 정확도 (영어 위주)"),
            ("ggml-tiny.bin",      "Tiny",      "~75MB — 테스트용"),
        };

        WhisperModelItem? bestModel = null;

        foreach (var (file, label, desc) in knownModels)
        {
            var fullPath = Path.Combine(modelDir, file);
            if (!File.Exists(fullPath)) continue;

            var fi = new FileInfo(fullPath);
            var sizeMb = fi.Length / (1024.0 * 1024.0);
            var item = new WhisperModelItem(label, $"{label} ({sizeMb:F0}MB)", fullPath);
            AvailableModels.Add(item);

            bestModel ??= item; // 첫 번째 (가장 큰) 모델
        }

        // 찾아보기 옵션 항상 마지막에 추가
        AvailableModels.Add(new WhisperModelItem("찾아보기...", "직접 모델 파일 선택", ""));

        if (bestModel != null)
        {
            SelectedModel = bestModel;
            ModelPath = bestModel.FilePath;
        }
    }

    partial void OnSelectedModelChanged(WhisperModelItem? value)
    {
        if (value == null) return;

        if (string.IsNullOrEmpty(value.FilePath))
        {
            // "찾아보기..." 선택 → code-behind에서 파일 다이얼로그 열기
            RequestBrowseModel?.Invoke();
            return;
        }

        ModelPath = value.FilePath;
    }

    /// <summary>찾아보기 요청 (code-behind에서 핸들링)</summary>
    public Action? RequestBrowseModel { get; set; }

    /// <summary>외부에서 모델 경로 직접 설정 (찾아보기 결과)</summary>
    public void SetCustomModelPath(string path)
    {
        // 기존 AvailableModels에 없는 경로면 "Custom" 항목 추가
        var existing = AvailableModels.FirstOrDefault(m => m.FilePath == path);
        if (existing != null)
        {
            SelectedModel = existing;
        }
        else
        {
            var fi = new FileInfo(path);
            var sizeMb = fi.Length / (1024.0 * 1024.0);
            var name = Path.GetFileNameWithoutExtension(fi.Name);
            var item = new WhisperModelItem(name, $"{name} ({sizeMb:F0}MB)", path);
            // "찾아보기..." 앞에 삽입
            AvailableModels.Insert(AvailableModels.Count - 1, item);
            SelectedModel = item;
        }
        ModelPath = path;
    }

    // ─── 커맨드 ─────────────────────────────────────────────────────

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning) return;

        if (string.IsNullOrWhiteSpace(ModelPath) || !File.Exists(ModelPath))
        {
            ErrorMessage = $"모델 파일을 찾을 수 없습니다:\n{ModelPath}";
            return;
        }

        if (string.IsNullOrWhiteSpace(MediaPath))
        {
            ErrorMessage = "미디어 파일 경로가 필요합니다.";
            return;
        }

        IsRunning = true;
        ErrorMessage = null;
        Progress = 0;
        StatusText = "음성 인식 중...";

        _cts = new CancellationTokenSource();
        var progressReporter = new Progress<int>(p =>
        {
            Progress = p;
            StatusText = p switch
            {
                < 10 => $"오디오 추출 중... {p}%",
                < 20 => $"Whisper 모델 로드 중... {p}%",
                100  => "완료",
                _    => $"음성 인식 중... {p}%",
            };
        });

        try
        {
            var segments = await _whisperService.TranscribeAsync(
                MediaPath,
                ModelPath,
                GetSelectedLanguageCode(),
                progressReporter,
                _cts.Token);

            Progress = 100;

            if (segments.Count == 0)
            {
                StatusText = "완료 — 인식된 음성 없음";
                ErrorMessage =
                    "음성 세그먼트가 생성되지 않았습니다.\n\n" +
                    "해결 방법:\n" +
                    "1. 언어를 '자동 감지' 대신 '한국어 (ko)'로 직접 선택\n" +
                    "2. 더 큰 모델 사용 (Medium 이상 추천)\n" +
                    "3. 클립에 실제 음성(대사)이 포함되어 있는지 확인";
                IsRunning = false;
                return;
            }

            StatusText = $"완료! {segments.Count}개 자막 생성";
            OnTranscribeComplete?.Invoke(segments);

            // 완료 후 자동 닫기
            await Task.Delay(600);
            RequestClose?.Invoke();
        }
        catch (OperationCanceledException)
        {
            StatusText = "취소됨";
            Progress = 0;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusText = "오류 발생";
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }
}

/// <summary>
/// Whisper 모델 항목 (ComboBox 표시용)
/// </summary>
public record WhisperModelItem(string Name, string DisplayName, string FilePath)
{
    public override string ToString() => DisplayName;
}
