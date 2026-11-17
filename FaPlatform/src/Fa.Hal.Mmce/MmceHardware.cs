using Fa.Core.Hardware;

namespace Fa.Hal.Mmce;

/// <summary>
/// RS Automation MMCE 드라이버 (뼈대).
/// 구조는 WMX3 와 같다: 세션 1개를 모션/IO 가 공유하고, IAxis/IIoController 만 구현하면 된다.
/// 함수 이름은 MMCE SDK 매뉴얼을 보고 TODO 위치에 채운다.
/// </summary>
public sealed class MmceHardwareFactory : IHardwareFactory
{
    public ControllerKind Kind => ControllerKind.Mmce;
    private MmceSession? _session;
    private MmceSession Session(MachineConfig c) => _session ??= new MmceSession(c.Controller.Options);
    public IMotionController CreateMotion(MachineConfig config) => new MmceMotionController(Session(config));
    public IIoController CreateIo(MachineConfig config) => new MmceIoController(Session(config));
}

internal sealed class MmceSession
{
    private readonly Dictionary<string, string> _options;
    private int _refCount;
    public MmceSession(Dictionary<string, string> options) { _options = options; }
    public bool IsOpen { get; private set; }

    public void Open()
    {
        if (_refCount++ > 0) return;
#if MMCE_SDK
        // TODO(MMCE): 라이브러리 초기화, 마스터 연결, EtherCAT OP 상태 확인
        IsOpen = true;
#else
        throw new NotSupportedException("MMCE SDK 가 연결되지 않았습니다. Fa.Hal.Mmce.csproj 에서 SDK 참조와 MMCE_SDK 심볼을 추가하세요.");
#endif
    }

    public void Close()
    {
        if (--_refCount > 0 || !IsOpen) return;
        // TODO(MMCE): 연결 종료
        IsOpen = false;
    }
}

internal sealed class MmceMotionController : IMotionController
{
    private readonly MmceSession _s;
    public MmceMotionController(MmceSession s) { _s = s; }
    public string Name => "MMCE";
    public bool IsOpen => _s.IsOpen;
    public void Open() => _s.Open();
    public void Close() => _s.Close();
    public IAxis CreateAxis(AxisConfig config) => new MmceAxis(config);
    public void Update() { } // TODO(MMCE): 전체 축 상태 일괄 읽기
    public void Dispose() => Close();
}

internal sealed class MmceAxis : IAxis
{
    private readonly AxisConfig _cfg;
    public MmceAxis(AxisConfig cfg) { _cfg = cfg; }
    public int AxisNo => _cfg.AxisNo;
    public AxisStatus ReadStatus() => default;  // TODO(MMCE)
    public void ServoOn(bool on) => Todo();
    public void MoveAbsolute(double p, double v, double a, double d) => Todo();
    public void MoveRelative(double p, double v, double a, double d) => Todo();
    public void Jog(double v, double a) => Todo();
    public void Stop(double d) => Todo();
    public void EmergencyStop() => Todo();
    public void StartHoming(HomeConfig h) => Todo();
    public void ClearAlarm() => Todo();
    private static void Todo() => throw new NotImplementedException("MMCE 축 명령 미구현");
}

internal sealed class MmceIoController : IIoController
{
    private readonly MmceSession _s;
    public MmceIoController(MmceSession s) { _s = s; }
    public bool IsOpen => _s.IsOpen;
    public void Open() => _s.Open();
    public void Close() => _s.Close();
    public void ReadInputs(Span<byte> image) { }               // TODO(MMCE): 입력 PDO 이미지 읽기
    public void WriteOutputs(ReadOnlySpan<byte> image) { }     // TODO(MMCE): 출력 PDO 이미지 쓰기
    public void Dispose() => Close();
}
