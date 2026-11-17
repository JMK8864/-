using System.Globalization;

namespace Fa.Core.Data;

/// <summary>파라미터 정의. 값은 문자열로 저장하고 사용할 때 변환한다.</summary>
public sealed record ParameterDef(
    string Key, string Group, string Default, string Unit = "", string Description = "",
    UserLevel EditLevel = UserLevel.Engineer, string[]? Options = null);

/// <summary>설비 파라미터 (Data/Config/parameters.json). 레시피와 달리 제품과 무관한 설비 고유 값.</summary>
public sealed class ParameterManager
{
    private const string Path = "Config/parameters";
    private readonly IDataStore _store;
    private readonly Dictionary<string, ParameterDef> _defs = new();
    private Dictionary<string, string> _values;
    private readonly object _lock = new();

    public ParameterManager(IDataStore store)
    {
        _store = store;
        _values = store.Load<Dictionary<string, string>>(Path) ?? new();
        PlatformParameters.Register(this);
    }

    public event Action<string>? Changed;
    public IEnumerable<ParameterDef> Definitions => _defs.Values;

    public void Define(ParameterDef def) => _defs[def.Key] = def;

    public string Get(string key)
    {
        lock (_lock)
            if (_values.TryGetValue(key, out var v)) return v;
        return _defs.TryGetValue(key, out var d) ? d.Default : "";
    }

    public double GetDouble(string key) => double.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    public int GetInt(string key) => (int)GetDouble(key);
    public bool GetBool(string key) => bool.TryParse(Get(key), out var b) && b;
    public T GetEnum<T>(string key) where T : struct, Enum => Enum.TryParse<T>(Get(key), true, out var e) ? e : default;

    public void Set(string key, string value)
    {
        lock (_lock)
        {
            _values[key] = value;
            _store.Save(Path, _values);
        }
        Changed?.Invoke(key);
    }
}

/// <summary>플랫폼 공통 파라미터 키.</summary>
public static class PlatformParameters
{
    public const string Language = "System.Language";
    public const string ResumePolicy = "Sequence.DefaultResumePolicy";
    public const string StepTimeoutMs = "Sequence.DefaultStepTimeoutMs";
    public const string AutoSpeedPercent = "Motion.AutoSpeedPercent";
    public const string ManualSpeedPercent = "Motion.ManualSpeedPercent";
    public const string DoorCheckEnabled = "Safety.DoorCheckEnabled";

    internal static void Register(ParameterManager p)
    {
        p.Define(new(Language, "System", "ko", Description: "화면/알람 언어", Options: new[] { "ko", "en" }));
        p.Define(new(ResumePolicy, "Sequence", "Continue", Description: "알람 후 기본 재시작 방식", Options: new[] { "Continue", "Restart" }));
        p.Define(new(StepTimeoutMs, "Sequence", "10000", "ms", "시퀀스 대기 기본 타임아웃"));
        p.Define(new(AutoSpeedPercent, "Motion", "100", "%", "자동운전 속도 비율"));
        p.Define(new(ManualSpeedPercent, "Motion", "30", "%", "수동/조그 속도 비율"));
        p.Define(new(DoorCheckEnabled, "Safety", "true", Description: "도어 감시 사용", EditLevel: UserLevel.Admin, Options: new[] { "true", "false" }));
    }
}
