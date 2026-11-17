using System.Buffers.Binary;
using Fa.Core.Hardware;
using Fa.Core.Machine;

namespace Fa.Core.Io;

/// <summary>
/// IO 프로세스 이미지를 관리한다.
/// IO 스레드: <see cref="Scan"/> 에서 입력 이미지 읽기 → 출력 이미지 쓰기.
/// 다른 스레드(시퀀스, UI)는 이름으로 접점을 찾아 읽고 쓴다.
/// </summary>
public sealed class IoManager
{
    private readonly IIoController _controller;
    private readonly object _lock = new();
    private byte[] _in;   // 마지막으로 읽은 입력 이미지
    private readonly byte[] _out;
    private byte[] _scanBuffer;

    public IoManager(IIoController controller, MachineConfig config, InterlockManager interlocks)
    {
        _controller = controller;
        Interlocks = interlocks;
        _in = new byte[config.IoImage.InputSize];
        _scanBuffer = new byte[config.IoImage.InputSize];
        _out = new byte[config.IoImage.OutputSize];

        Inputs = config.DigitalInputs.ToDictionary(c => c.Name, c => new DigitalInput(this, c));
        Outputs = config.DigitalOutputs.ToDictionary(c => c.Name, c => new DigitalOutput(this, c));
        AnalogInputs = config.AnalogInputs.ToDictionary(c => c.Name, c => new AnalogInput(this, c));
        AnalogOutputs = config.AnalogOutputs.ToDictionary(c => c.Name, c => new AnalogOutput(this, c));
    }

    public InterlockManager Interlocks { get; }
    public IIoController Controller => _controller;
    public IReadOnlyDictionary<string, DigitalInput> Inputs { get; }
    public IReadOnlyDictionary<string, DigitalOutput> Outputs { get; }
    public IReadOnlyDictionary<string, AnalogInput> AnalogInputs { get; }
    public IReadOnlyDictionary<string, AnalogOutput> AnalogOutputs { get; }
    public bool IsSimulation => _controller is ISimulatedIo;
    private long _scanCount;
    /// <summary>완료된 스캔 횟수 (첫 입력 이미지를 읽었는지 확인용).</summary>
    public long ScanCount => Interlocked.Read(ref _scanCount);

    public DigitalInput DI(string name) => Inputs.TryGetValue(name, out var p) ? p : throw new KeyNotFoundException($"DI '{name}' 이(가) machine.json 에 없습니다.");
    public DigitalOutput DO(string name) => Outputs.TryGetValue(name, out var p) ? p : throw new KeyNotFoundException($"DO '{name}' 이(가) machine.json 에 없습니다.");
    public AnalogInput AI(string name) => AnalogInputs.TryGetValue(name, out var p) ? p : throw new KeyNotFoundException($"AI '{name}' 이(가) machine.json 에 없습니다.");
    public AnalogOutput AO(string name) => AnalogOutputs.TryGetValue(name, out var p) ? p : throw new KeyNotFoundException($"AO '{name}' 이(가) machine.json 에 없습니다.");

    /// <summary>IO 스레드에서 주기 호출.</summary>
    public void Scan()
    {
        _controller.ReadInputs(_scanBuffer);
        // 버퍼 교체로 읽는 쪽이 항상 완성된 이미지를 보게 한다
        (_in, _scanBuffer) = (_scanBuffer, _in);
        lock (_lock) _controller.WriteOutputs(_out);
        Interlocked.Increment(ref _scanCount);
    }

    /// <summary>모든 출력 OFF (비상정지 등).</summary>
    public void AllOutputsOff()
    {
        lock (_lock) Array.Clear(_out);
    }

    /// <summary>시뮬레이션 모드에서만 입력을 강제로 바꾼다.</summary>
    public bool SimulateInput(string name, bool value)
    {
        if (_controller is not ISimulatedIo sim || !Inputs.TryGetValue(name, out var di)) return false;
        sim.SetInputBit(di.Config.Byte, di.Config.Bit, value ^ di.Config.Inverted);
        return true;
    }

    public bool SimulateAnalogInput(string name, double engValue)
    {
        if (_controller is not ISimulatedIo sim || !AnalogInputs.TryGetValue(name, out var ai)) return false;
        var buf = new byte[4];
        EncodeRaw(ai.Config.DataType, buf, AnalogScale.ToRaw(ai.Config, engValue));
        sim.SetInputBytes(ai.Config.Byte, buf.AsSpan(0, SizeOf(ai.Config.DataType)));
        return true;
    }

    internal bool GetInputBit(int b, int bit) { var img = _in; return b < img.Length && (img[b] & (1 << bit)) != 0; }

    internal bool GetOutputBit(int b, int bit) { lock (_lock) return b < _out.Length && (_out[b] & (1 << bit)) != 0; }

    internal void SetOutputBit(int b, int bit, bool on)
    {
        lock (_lock)
        {
            if (b >= _out.Length) return;
            if (on) _out[b] |= (byte)(1 << bit); else _out[b] &= (byte)~(1 << bit);
        }
    }

    internal double ReadRaw(AnalogPointConfig c, bool input)
    {
        if (input) return DecodeRaw(c.DataType, _in.AsSpan(c.Byte));
        lock (_lock) return DecodeRaw(c.DataType, _out.AsSpan(c.Byte));
    }

    internal void WriteRaw(AnalogPointConfig c, double raw)
    {
        lock (_lock) EncodeRaw(c.DataType, _out.AsSpan(c.Byte), raw);
    }

    // EtherCAT 은 Little Endian
    private static double DecodeRaw(AnalogDataType t, ReadOnlySpan<byte> s) => t switch
    {
        AnalogDataType.Int16 => BinaryPrimitives.ReadInt16LittleEndian(s),
        AnalogDataType.UInt16 => BinaryPrimitives.ReadUInt16LittleEndian(s),
        AnalogDataType.Int32 => BinaryPrimitives.ReadInt32LittleEndian(s),
        AnalogDataType.Float32 => BinaryPrimitives.ReadSingleLittleEndian(s),
        _ => 0
    };

    private static void EncodeRaw(AnalogDataType t, Span<byte> s, double v)
    {
        switch (t)
        {
            case AnalogDataType.Int16: BinaryPrimitives.WriteInt16LittleEndian(s, (short)Math.Round(v)); break;
            case AnalogDataType.UInt16: BinaryPrimitives.WriteUInt16LittleEndian(s, (ushort)Math.Round(v)); break;
            case AnalogDataType.Int32: BinaryPrimitives.WriteInt32LittleEndian(s, (int)Math.Round(v)); break;
            case AnalogDataType.Float32: BinaryPrimitives.WriteSingleLittleEndian(s, (float)v); break;
        }
    }

    private static int SizeOf(AnalogDataType t) => t is AnalogDataType.Int16 or AnalogDataType.UInt16 ? 2 : 4;
}
