using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VortexCut.Core.Models;

namespace VortexCut.UI.ViewModels;

/// <summary>
/// Project Bin (미디어 라이브러리) ViewModel
/// </summary>
public partial class ProjectBinViewModel : ViewModelBase
{
    [ObservableProperty]
    private ObservableCollection<MediaItem> _mediaItems = new();

    [ObservableProperty]
    private MediaItem? _selectedItem;

    [ObservableProperty]
    private bool _isLoading = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredMediaItems))]
    [NotifyPropertyChangedFor(nameof(HasSearchText))]
    private string _searchText = string.Empty;

    /// <summary>
    /// 검색 필터 적용된 미디어 목록
    /// </summary>
    public ObservableCollection<MediaItem> FilteredMediaItems
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SearchText))
                return MediaItems;

            var filtered = MediaItems
                .Where(m => m.Name.Contains(SearchText, System.StringComparison.OrdinalIgnoreCase))
                .ToList();
            return new ObservableCollection<MediaItem>(filtered);
        }
    }

    /// <summary>
    /// 검색 텍스트 존재 여부 (X 버튼 표시용)
    /// </summary>
    public bool HasSearchText => !string.IsNullOrEmpty(SearchText);

    /// <summary>
    /// 검색 초기화
    /// </summary>
    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = string.Empty;
    }

    /// <summary>
    /// 빈 상태 여부 (Empty State 표시용)
    /// </summary>
    public bool IsEmpty => MediaItems.Count == 0 && !IsLoading;

    /// <summary>
    /// 전체 오버레이 형태의 로딩 UI를 보여줄지 여부.
    /// - true  : 아직 미디어가 하나도 없고, 첫 임포트가 진행 중일 때만
    /// - false : 이미 미디어가 하나 이상 있으면, 임포트 중이어도 기존 썸네일은 그대로 노출
    /// </summary>
    public bool ShowLoadingOverlay => MediaItems.Count == 0 && IsLoading;

    /// <summary>
    /// 미디어 아이템 추가
    /// </summary>
    public void AddMediaItem(MediaItem item)
    {
        MediaItems.Add(item);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowLoadingOverlay));
        OnPropertyChanged(nameof(FilteredMediaItems));
    }

    /// <summary>
    /// 미디어 아이템 제거
    /// </summary>
    [RelayCommand]
    private void RemoveSelectedItem()
    {
        if (SelectedItem != null)
        {
            MediaItems.Remove(SelectedItem);
            SelectedItem = null;
            OnPropertyChanged(nameof(FilteredMediaItems));
        }
    }

    /// <summary>
    /// 모든 아이템 제거
    /// </summary>
    public void Clear()
    {
        MediaItems.Clear();
        SelectedItem = null;
        SearchText = string.Empty;
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowLoadingOverlay));
        OnPropertyChanged(nameof(FilteredMediaItems));
    }

    /// <summary>
    /// 로딩 상태 설정 (미디어 임포트 시작/종료)
    /// </summary>
    public void SetLoading(bool loading)
    {
        IsLoading = loading;
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowLoadingOverlay));
    }
}
