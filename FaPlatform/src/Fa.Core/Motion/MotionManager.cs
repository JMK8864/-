using Fa.Core.Hardware;
using Fa.Core.Machine;

namespace Fa.Core.Motion;

/// <summary>machine.json 의 축 목록으로 Axis 객체를 만들고, 모션 스레드에서 상태를 갱신한다.</summary>
public sealed class MotionManager
{
    private readonly IMotionController _controller;

    public MotionManager(IMotionController controller, MachineConfig config, InterlockManager interlocks)
    {
        _controller = controller;
        _configs = config.Axes;
        _interlocks = interlocks;
    }

    private readonly List<AxisConfig> _configs;
    private readonly InterlockManager _interlocks;
    private Dictionary<string, Axis> _axes = new();

    public IMotionController Controller => _controller;
    public IReadOnlyDictionary<string, Axis> Axes => _axes;

    public Axis this[string name] => _axes.TryGetValue(name, out var a) ? a : throw new KeyNotFoundException($"축 '{name}' 이(가) machine.json 에 없습니다.");

    /// <summary>컨트롤러 Open 후 호출.</summary>
    public void CreateAxes()
    {
        _axes = _configs.ToDictionary(c => c.Name, c => new Axis(_controller.CreateAxis(c), c, _interlocks));
    }

    /// <summary>모션 스레드에서 주기 호출.</summary>
    public void Scan()
    {
        _controller.Update();
        foreach (var a in _axes.Values) a.Refresh();
    }

    public void StopAll() { foreach (var a in _axes.Values) a.Stop(); }
    public void EmergencyStopAll() { foreach (var a in _axes.Values) a.EmergencyStop(); }
    public void ServoAll(bool on) { foreach (var a in _axes.Values) a.ServoOn(on); }
    public bool AllHomeDone => _axes.Values.All(a => a.IsHomeDone);
}
