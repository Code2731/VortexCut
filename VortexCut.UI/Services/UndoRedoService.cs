using System.Linq;
using VortexCut.Core.Interfaces;

namespace VortexCut.UI.Services;

/// <summary>
/// Undo/Redo 히스토리 관리 서비스
/// Command 패턴 기반, 최대 100 단계
/// </summary>
public class UndoRedoService
{
    private const int MaxHistorySize = 100;

    private readonly Stack<IUndoableAction> _undoStack = new();
    private readonly Stack<IUndoableAction> _redoStack = new();

    /// <summary>
    /// 히스토리 변경 시 호출 (렌더 캐시 클리어 등)
    /// </summary>
    public Action? OnHistoryChanged { get; set; }

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public string? UndoDescription => _undoStack.Count > 0 ? _undoStack.Peek().Description : null;
    public string? RedoDescription => _redoStack.Count > 0 ? _redoStack.Peek().Description : null;

    /// <summary>
    /// Undo 스택 목록 반환 (Stack.ToArray = [최신, ..., 오래된] 순)
    /// </summary>
    public IReadOnlyList<string> GetUndoItems() =>
        _undoStack.Select(a => a.Description).ToList();

    /// <summary>
    /// Redo 스택 목록 반환 (Stack.ToArray = [다음Redo, ..., 마지막Redo] 순)
    /// </summary>
    public IReadOnlyList<string> GetRedoItems() =>
        _redoStack.Select(a => a.Description).ToList();

    /// <summary>
    /// 액션 실행 + 히스토리 기록
    /// </summary>
    public void ExecuteAction(IUndoableAction action)
    {
        action.Execute();
        PushUndo(action);
        _redoStack.Clear();
        OnHistoryChanged?.Invoke();
    }

    /// <summary>
    /// 이미 실행된 액션을 사후 기록 (드래그/트림 완료 후)
    /// </summary>
    public void RecordAction(IUndoableAction action)
    {
        PushUndo(action);
        _redoStack.Clear();
        OnHistoryChanged?.Invoke();
    }

    /// <summary>
    /// Undo 실행
    /// </summary>
    public void Undo()
    {
        if (_undoStack.Count == 0) return;

        var action = _undoStack.Pop();
        action.Undo();
        _redoStack.Push(action);
        OnHistoryChanged?.Invoke();
    }

    /// <summary>
    /// Redo 실행
    /// </summary>
    public void Redo()
    {
        if (_redoStack.Count == 0) return;

        var action = _redoStack.Pop();
        action.Execute();
        _undoStack.Push(action);
        OnHistoryChanged?.Invoke();
    }

    /// <summary>
    /// 히스토리 초기화 (프로젝트 로드/새 프로젝트 시)
    /// </summary>
    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        OnHistoryChanged?.Invoke();
    }

    private void PushUndo(IUndoableAction action)
    {
        _undoStack.Push(action);

        // 최대 히스토리 크기 제한
        if (_undoStack.Count > MaxHistorySize)
        {
            // Stack을 Array로 변환하여 오래된 항목 제거
            var items = _undoStack.ToArray();
            _undoStack.Clear();
            // ToArray()는 역순(최신→오래된)이므로 앞에서 MaxHistorySize개만 유지
            for (int i = MaxHistorySize - 1; i >= 0; i--)
            {
                _undoStack.Push(items[i]);
            }
        }
    }
}
