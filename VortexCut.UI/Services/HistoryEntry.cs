namespace VortexCut.UI.Services;

/// <summary>
/// 히스토리 패널 표시용 항목 상태
/// </summary>
public enum HistoryEntryState
{
    Executed,  // 실행된 액션 (Undo 가능)
    Redoable,  // Redo 가능
}

/// <summary>
/// 히스토리 패널 표시용 단일 항목
/// Steps: 이 상태에 도달하기 위한 Undo/Redo 횟수 (0 = 현재 상태)
/// </summary>
public record HistoryEntry(string Description, HistoryEntryState State, int Steps)
{
    public bool IsCurrent => State == HistoryEntryState.Executed && Steps == 0;
    public double DisplayOpacity => State == HistoryEntryState.Redoable ? 0.4 : 1.0;
    public string BulletChar => IsCurrent ? "▶" : "•";
    public string FontWeightStr => IsCurrent ? "SemiBold" : "Normal";
}
