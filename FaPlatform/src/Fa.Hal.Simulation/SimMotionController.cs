using System.Diagnostics;
using Fa.Core.Hardware;

namespace Fa.Hal.Simulation;

public sealed class SimMotionController : IMotionController
{
    private readonly List<SimAxis> _axes = new();
    private readonly Stopwatch _clock = new();
    private double _last;

    public string Name => "Simulation";
    public bool IsOpen { get; private set; }

    public void Open() { IsOpen = true; _clock.Restart(); _last = 0; }
    public void Close() { IsOpen = false; }

    public IAxis CreateAxis(AxisConfig config)
    {
        var a = new SimAxis(config);
        _axes.Add(a);
        return a;
    }

    public void Update()
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Min(now - _last, 0.05);
        _last = now;
        foreach (var a in _axes) a.Advance(dt);
    }

    /// <summary>시험용: 해당 번호 축에 서보 알람을 만든다.</summary>
    public void InjectAlarm(int axisNo) => _axes.FirstOrDefault(a => a.AxisNo == axisNo)?.InjectAlarm();

    public void Dispose() => Close();
}
