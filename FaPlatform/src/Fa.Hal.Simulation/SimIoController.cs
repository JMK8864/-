using System.Diagnostics;
using Fa.Core.Hardware;

namespace Fa.Hal.Simulation;

/// <summary>
/// 가상 IO. 출력 이미지를 받아 SimLink 설정대로 지연 후 입력 비트에 반영한다.
/// 화면에서 입력을 강제로 바꿀 수 있다(ISimulatedIo).
/// </summary>
public sealed class SimIoController : IIoController, ISimulatedIo
{
    private readonly object _lock = new();
    private readonly byte[] _in;
    private readonly byte[] _out;
    private readonly List<(DigitalPointConfig Out, DigitalPointConfig In, SimLink Link)> _links = new();
    private readonly Dictionary<SimLink, (bool Value, long Since)> _linkState = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public SimIoController(MachineConfig config)
    {
        _in = new byte[config.IoImage.InputSize];
        _out = new byte[config.IoImage.OutputSize];
        var dis = config.DigitalInputs.ToDictionary(p => p.Name);
        var dos = config.DigitalOutputs.ToDictionary(p => p.Name);
        foreach (var l in config.Simulation.Links)
            if (dos.TryGetValue(l.Output, out var o) && dis.TryGetValue(l.Input, out var i))
                _links.Add((o, i, l));

        foreach (var (name, value) in config.Simulation.InitialInputs)
            if (dis.TryGetValue(name, out var p)) SetInputBit(p.Byte, p.Bit, value ^ p.Inverted);

        foreach (var (name, value) in config.Simulation.InitialAnalogInputs)
        {
            var ai = config.AnalogInputs.FirstOrDefault(a => a.Name == name);
            if (ai == null) continue;
            short raw = (short)Math.Round(Fa.Core.Io.AnalogScale.ToRaw(ai, value));
            SetInputBytes(ai.Byte, BitConverter.GetBytes(raw));
        }
    }

    public bool IsOpen { get; private set; }
    public void Open() => IsOpen = true;
    public void Close() => IsOpen = false;

    public void ReadInputs(Span<byte> image)
    {
        lock (_lock)
        {
            ApplyLinks();
            _in.AsSpan(0, Math.Min(_in.Length, image.Length)).CopyTo(image);
        }
    }

    public void WriteOutputs(ReadOnlySpan<byte> image)
    {
        lock (_lock) image[..Math.Min(image.Length, _out.Length)].CopyTo(_out);
    }

    public void SetInputBit(int byteIndex, int bit, bool value)
    {
        lock (_lock)
        {
            if (byteIndex >= _in.Length) return;
            if (value) _in[byteIndex] |= (byte)(1 << bit); else _in[byteIndex] &= (byte)~(1 << bit);
        }
    }

    public void SetInputBytes(int byteIndex, ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            if (byteIndex + data.Length <= _in.Length) data.CopyTo(_in.AsSpan(byteIndex));
        }
    }

    private void ApplyLinks()
    {
        long now = _clock.ElapsedMilliseconds;
        foreach (var (o, i, l) in _links)
        {
            bool outVal = ((_out[o.Byte] >> o.Bit) & 1) != 0;
            bool target = outVal ^ l.Invert;
            if (!_linkState.TryGetValue(l, out var st) || st.Value != target)
                _linkState[l] = st = (target, now);
            if (now - st.Since >= l.DelayMs)
            {
                if (target) _in[i.Byte] |= (byte)(1 << i.Bit); else _in[i.Byte] &= (byte)~(1 << i.Bit);
            }
        }
    }

    public void Dispose() => Close();
}

public sealed class SimulationHardwareFactory : IHardwareFactory
{
    public ControllerKind Kind => ControllerKind.Simulation;
    public IMotionController CreateMotion(MachineConfig config) => new SimMotionController();
    public IIoController CreateIo(MachineConfig config) => new SimIoController(config);
}
