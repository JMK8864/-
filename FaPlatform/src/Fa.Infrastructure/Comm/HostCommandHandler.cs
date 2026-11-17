using Fa.Core.Common;
using Fa.Core.Machine;

namespace Fa.Infrastructure.Comm;

/// <summary>
/// 상위 명령 처리기. 기본 명령을 제공하고, 설비 모듈이 Register 로 명령을 추가할 수 있다.
/// 응답 형식: "명령,OK[,값...]" 또는 "명령,NG,사유"
/// </summary>
public sealed class HostCommandHandler
{
    private readonly Dictionary<string, Func<string[], string>> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly MachineController _m;

    public HostCommandHandler(MachineController machine)
    {
        _m = machine;
        Register("STATUS", _ => $"STATUS,OK,{_m.State},{_m.Mode},{_m.Recipe.Current.Name}");
        Register("START", a => Reply("START", _m.Start(a.Length > 0 && Enum.TryParse<ResumePolicy>(a[0], true, out var p) ? p : null)));
        Register("STOP", _ => Reply("STOP", _m.Stop()));
        Register("RESET", _ => Reply("RESET", _m.Reset("HOST")));
        Register("HOME", _ => Reply("HOME", _m.Home()));
        Register("MODE", a => a.Length > 0 && Enum.TryParse<OperationMode>(a[0], true, out var md)
            ? Reply("MODE", _m.SetMode(md)) : "MODE,NG,인자 오류");
        Register("RECIPE", a =>
        {
            if (a.Length == 0) return $"RECIPE,OK,{_m.Recipe.Current.Name}";
            if (_m.State == MachineState.Run) return "RECIPE,NG,운전 중";
            _m.Recipe.Select(a[0]);
            return $"RECIPE,OK,{a[0]}";
        });
        Register("ALARMS", _ => "ALARMS,OK," + string.Join(';', _m.Alarms.Active.Select(x => $"{x.Code}:{x.Source}")));

        _m.StateChanged += (_, s) => EventReport?.Invoke($"EVENT,STATE,{s}");
        _m.CycleCompleted += (u, t) => EventReport?.Invoke($"EVENT,CYCLE,{u.Name},{u.CycleCount},{t:F2}");
    }

    /// <summary>설비 → 상위 이벤트 보고.</summary>
    public event Action<string>? EventReport;

    public void Register(string command, Func<string[], string> handler) => _commands[command] = handler;

    public string? Handle(string message)
    {
        var parts = message.Trim().Split(',');
        if (parts.Length == 0 || parts[0].Length == 0) return null;
        if (!_commands.TryGetValue(parts[0], out var h)) return $"{parts[0]},NG,알 수 없는 명령";
        try { return h(parts[1..]); }
        catch (Exception ex) { return $"{parts[0]},NG,{ex.Message}"; }
    }

    private static string Reply(string cmd, OpResult r) => r.Ok ? $"{cmd},OK" : $"{cmd},NG,{r.Error}";
}
