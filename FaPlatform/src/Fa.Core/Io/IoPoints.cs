using Fa.Core.Common;
using Fa.Core.Hardware;

namespace Fa.Core.Io;

public sealed class DigitalInput
{
    private readonly IoManager _io;
    internal DigitalInput(IoManager io, DigitalPointConfig cfg) { _io = io; Config = cfg; }
    public DigitalPointConfig Config { get; }
    public string Name => Config.Name;
    /// <summary>Inverted 적용 후 논리값.</summary>
    public bool Value => _io.GetInputBit(Config.Byte, Config.Bit) ^ Config.Inverted;
    public bool IsOn => Value;
    public bool IsOff => !Value;
}

public sealed class DigitalOutput
{
    private readonly IoManager _io;
    internal DigitalOutput(IoManager io, DigitalPointConfig cfg) { _io = io; Config = cfg; }
    public DigitalPointConfig Config { get; }
    public string Name => Config.Name;
    public bool Value => _io.GetOutputBit(Config.Byte, Config.Bit) ^ Config.Inverted;

    /// <summary>인터록 확인 후 출력. 실패 시 출력하지 않고 사유를 반환한다.</summary>
    public OpResult Set(bool on)
    {
        var check = _io.Interlocks.CheckOutput(Name, on);
        if (!check.Ok) return check;
        _io.SetOutputBit(Config.Byte, Config.Bit, on ^ Config.Inverted);
        return OpResult.Success;
    }
    public OpResult On() => Set(true);
    public OpResult Off() => Set(false);

    /// <summary>인터록 무시 (비상정지 시 안전상태 출력 등 플랫폼 내부용).</summary>
    internal void ForceSet(bool on) => _io.SetOutputBit(Config.Byte, Config.Bit, on ^ Config.Inverted);
}

public sealed class AnalogInput
{
    private readonly IoManager _io;
    internal AnalogInput(IoManager io, AnalogPointConfig cfg) { _io = io; Config = cfg; }
    public AnalogPointConfig Config { get; }
    public string Name => Config.Name;
    public double Raw => _io.ReadRaw(Config, input: true);
    public double Value => AnalogScale.ToEng(Config, Raw);
}

public sealed class AnalogOutput
{
    private readonly IoManager _io;
    internal AnalogOutput(IoManager io, AnalogPointConfig cfg) { _io = io; Config = cfg; }
    public AnalogPointConfig Config { get; }
    public string Name => Config.Name;
    public double Value => AnalogScale.ToEng(Config, _io.ReadRaw(Config, input: false));
    public void Set(double engValue) => _io.WriteRaw(Config, AnalogScale.ToRaw(Config, engValue));
}

public static class AnalogScale
{
    public static double ToEng(AnalogPointConfig c, double raw)
    {
        double span = c.RawMax - c.RawMin;
        return span == 0 ? c.EngMin : c.EngMin + (raw - c.RawMin) * (c.EngMax - c.EngMin) / span;
    }

    public static double ToRaw(AnalogPointConfig c, double eng)
    {
        double span = c.EngMax - c.EngMin;
        double raw = span == 0 ? c.RawMin : c.RawMin + (eng - c.EngMin) * (c.RawMax - c.RawMin) / span;
        double lo = Math.Min(c.RawMin, c.RawMax), hi = Math.Max(c.RawMin, c.RawMax);
        return Math.Clamp(raw, lo, hi);
    }
}
