namespace Fa.Core.Alarms;

public enum AlarmLevel
{
    /// <summary>경알람: 표시만, 설비는 계속 운전.</summary>
    Warning,
    /// <summary>중알람: 설비 정지 (Alarm 상태).</summary>
    Heavy
}

/// <summary>알람 정의 (Data/Config/alarms.json). 언어별 문구와 조치 방법(리커버리 가이드)을 담는다.</summary>
public sealed class AlarmDefinition
{
    public int Code { get; set; }
    public AlarmLevel Level { get; set; } = AlarmLevel.Heavy;
    /// <summary>언어코드 → 알람명. 예: { "ko": "비상정지", "en": "Emergency stop" }</summary>
    public Dictionary<string, string> Text { get; set; } = new();
    /// <summary>언어코드 → 원인/조치 방법.</summary>
    public Dictionary<string, string> Action { get; set; } = new();

    public string GetText(string lang) => Pick(Text, lang) ?? $"Alarm {Code}";
    public string GetAction(string lang) => Pick(Action, lang) ?? "";

    private static string? Pick(Dictionary<string, string> d, string lang)
        => d.TryGetValue(lang, out var s) ? s : d.Values.FirstOrDefault();
}

/// <summary>알람 발생 이력 1건. 발생/해제 시각을 기록해 이력·통계 화면에 사용한다.</summary>
public sealed class AlarmRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public int Code { get; init; }
    public AlarmLevel Level { get; init; }
    /// <summary>발생 위치 (축 이름, 유닛 이름 등).</summary>
    public string Source { get; init; } = "";
    public string Message { get; init; } = "";
    public string? Detail { get; init; }
    public DateTime OccurredAt { get; init; } = DateTime.Now;
    public DateTime? ClearedAt { get; set; }
    public string? ClearedBy { get; set; }
    public string? Recipe { get; init; }
    public TimeSpan? Duration => ClearedAt - OccurredAt;
}

/// <summary>알람 이력 저장소. 구현: PostgreSQL 또는 텍스트(CSV) 파일.</summary>
public interface IAlarmHistoryRepository
{
    Task InsertAsync(AlarmRecord record);
    Task UpdateClearedAsync(AlarmRecord record);
    Task<IReadOnlyList<AlarmRecord>> QueryAsync(DateTime from, DateTime to, int? code = null, int limit = 1000);
}
