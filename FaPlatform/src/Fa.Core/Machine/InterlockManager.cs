using Fa.Core.Common;

namespace Fa.Core.Machine;

/// <summary>
/// 인터록 규칙 모음. 축 이동/출력 변경 전에 등록된 규칙을 모두 검사한다.
/// 규칙은 null(허용) 또는 차단 사유 문자열을 반환한다.
/// 예: 반송 X 축은 로더 Z 가 Up 위치일 때만 이동 가능.
/// </summary>
public sealed class InterlockManager
{
    private readonly Dictionary<string, List<Func<double, string?>>> _motion = new();
    private readonly Dictionary<string, List<Func<bool, string?>>> _output = new();
    private readonly object _lock = new();

    /// <summary>축 이동 인터록. 인자는 목표 위치 (Jog 는 NaN).</summary>
    public void AddMotion(string axisName, Func<double, string?> rule)
    {
        lock (_lock)
        {
            if (!_motion.TryGetValue(axisName, out var list)) _motion[axisName] = list = new();
            list.Add(rule);
        }
    }

    /// <summary>출력 인터록. 인자는 바꾸려는 값.</summary>
    public void AddOutput(string outputName, Func<bool, string?> rule)
    {
        lock (_lock)
        {
            if (!_output.TryGetValue(outputName, out var list)) _output[outputName] = list = new();
            list.Add(rule);
        }
    }

    public OpResult CheckMotion(string axisName, double target)
    {
        List<Func<double, string?>>? rules;
        lock (_lock) _motion.TryGetValue(axisName, out rules);
        if (rules == null) return OpResult.Success;
        foreach (var r in rules)
            if (r(target) is { } reason) return OpResult.Fail($"[인터록] {axisName}: {reason}");
        return OpResult.Success;
    }

    public OpResult CheckOutput(string outputName, bool value)
    {
        List<Func<bool, string?>>? rules;
        lock (_lock) _output.TryGetValue(outputName, out rules);
        if (rules == null) return OpResult.Success;
        foreach (var r in rules)
            if (r(value) is { } reason) return OpResult.Fail($"[인터록] {outputName}: {reason}");
        return OpResult.Success;
    }
}
