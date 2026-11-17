using Microsoft.Extensions.Logging;

namespace Fa.Core.Alarms;

/// <summary>
/// 현재 발생 중인 알람 관리. 어느 스레드에서 호출해도 안전하다.
/// 같은 (Code, Source) 는 해제 전까지 한 번만 등록되므로, 감시 코드는 조건이 유지되는 동안 매 스캔 Raise 해도 된다.
/// 이력 저장(DB/파일)은 이벤트를 받은 쪽(AlarmHistoryWriter)이 별도 스레드에서 처리한다.
/// </summary>
public sealed class AlarmManager
{
    private readonly Dictionary<int, AlarmDefinition> _defs;
    private readonly List<AlarmRecord> _active = new();
    private readonly object _lock = new();
    private readonly ILogger _log;

    public AlarmManager(IEnumerable<AlarmDefinition> definitions, ILogger<AlarmManager> log)
    {
        _defs = definitions.GroupBy(d => d.Code).ToDictionary(g => g.Key, g => g.Last());
        _log = log;
    }

    /// <summary>알람 문구 언어 (다국어 설정과 연동).</summary>
    public string Language { get; set; } = "ko";
    public Func<string?>? CurrentRecipeProvider { get; set; }

    public event Action<AlarmRecord>? Raised;
    public event Action<AlarmRecord>? Cleared;

    public IReadOnlyList<AlarmRecord> Active { get { lock (_lock) return _active.ToList(); } }
    public bool HasHeavy { get { lock (_lock) return _active.Any(a => a.Level == AlarmLevel.Heavy); } }
    public bool HasAny { get { lock (_lock) return _active.Count > 0; } }

    public AlarmDefinition GetDefinition(int code)
        => _defs.TryGetValue(code, out var d) ? d : new AlarmDefinition { Code = code };

    public void Register(AlarmDefinition def) => _defs[def.Code] = def;
    public IReadOnlyCollection<AlarmDefinition> Definitions => _defs.Values;

    /// <summary>알람 발생. 이미 같은 알람이 활성이면 무시하고 false.</summary>
    public bool Raise(int code, string source, string? detail = null)
    {
        AlarmRecord rec;
        lock (_lock)
        {
            if (_active.Any(a => a.Code == code && a.Source == source)) return false;
            var def = GetDefinition(code);
            rec = new AlarmRecord
            {
                Code = code, Level = def.Level, Source = source, Detail = detail,
                Message = def.GetText(Language), Recipe = CurrentRecipeProvider?.Invoke()
            };
            _active.Add(rec);
        }
        _log.LogWarning("ALARM SET {Code} [{Source}] {Message} {Detail}", code, source, rec.Message, detail);
        Raised?.Invoke(rec);
        return true;
    }

    /// <summary>특정 알람 해제 (경알람 자동 해제 등).</summary>
    public void Clear(int code, string source, string by = "AUTO")
    {
        AlarmRecord? rec;
        lock (_lock)
        {
            rec = _active.FirstOrDefault(a => a.Code == code && a.Source == source);
            if (rec == null) return;
            _active.Remove(rec);
        }
        Complete(rec, by);
    }

    /// <summary>전체 리셋 (화면 Reset 버튼). 원인이 남아 있으면 감시 코드가 다음 스캔에 다시 발생시킨다.</summary>
    public void ResetAll(string by)
    {
        List<AlarmRecord> list;
        lock (_lock) { list = _active.ToList(); _active.Clear(); }
        foreach (var r in list) Complete(r, by);
    }

    private void Complete(AlarmRecord rec, string by)
    {
        rec.ClearedAt = DateTime.Now;
        rec.ClearedBy = by;
        _log.LogInformation("ALARM CLEAR {Code} [{Source}] by {By} ({Duration:F1}s)", rec.Code, rec.Source, by, rec.Duration?.TotalSeconds);
        Cleared?.Invoke(rec);
    }
}
