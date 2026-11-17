using Fa.Core.Hardware;

namespace Fa.Hal.Wmx3;

/// <summary>
/// Movensys WMX3 드라이버 (뼈대).
/// WMX3 는 모션과 IO 를 같은 엔진에서 다루므로 하나의 세션(Wmx3Session)을 공유한다.
///
/// 연결 순서 (WMX3 매뉴얼 기준, 설치된 SDK 버전의 API 이름을 반드시 확인):
///   WMX3Api.CreateDevice(설치경로, DeviceType) → StartCommunication(timeout)
///   CoreMotion : 축 서보/이동/원점/상태,  Io : 프로세스 이미지 Get/Set
/// 실제 SDK 코드는 WMX3_SDK 심볼이 정의된 경우에만 컴파일된다 (csproj 참고).
/// </summary>
public sealed class Wmx3HardwareFactory : IHardwareFactory
{
    public ControllerKind Kind => ControllerKind.Wmx3;
    private Wmx3Session? _session;
    private Wmx3Session Session(MachineConfig c) => _session ??= new Wmx3Session(c.Controller.Options);
    public IMotionController CreateMotion(MachineConfig config) => new Wmx3MotionController(Session(config));
    public IIoController CreateIo(MachineConfig config) => new Wmx3IoController(Session(config), config.IoImage);
}

/// <summary>WMX3 엔진 연결 (모션/IO 공용). Options: "InstallPath", "TimeoutMs".</summary>
internal sealed class Wmx3Session
{
    private readonly Dictionary<string, string> _options;
    private int _refCount;
    public Wmx3Session(Dictionary<string, string> options) { _options = options; }
    public bool IsOpen { get; private set; }

    public void Open()
    {
        if (_refCount++ > 0) return;
#if WMX3_SDK
        // TODO(WMX3): 엔진 생성 및 통신 시작
        // var path = _options.GetValueOrDefault("InstallPath", @"C:\Program Files\SoftServo\WMX3\");
        // Api = new WMX3Api(); Api.CreateDevice(path, DeviceType.DeviceTypeNormal, timeout);
        // Api.StartCommunication(timeout);
        // Core = new CoreMotion(Api); Io = new Io(Api);
        IsOpen = true;
#else
        throw new NotSupportedException("WMX3 SDK 가 연결되지 않았습니다. Fa.Hal.Wmx3.csproj 에서 SDK DLL 참조와 WMX3_SDK 심볼을 추가하세요.");
#endif
    }

    public void Close()
    {
        if (--_refCount > 0 || !IsOpen) return;
        // TODO(WMX3): StopCommunication → CloseDevice
        IsOpen = false;
    }
}

internal sealed class Wmx3MotionController : IMotionController
{
    private readonly Wmx3Session _s;
    public Wmx3MotionController(Wmx3Session s) { _s = s; }
    public string Name => "WMX3";
    public bool IsOpen => _s.IsOpen;
    public void Open() => _s.Open();
    public void Close() => _s.Close();
    public IAxis CreateAxis(AxisConfig config) => new Wmx3Axis(config);
    // TODO(WMX3): CoreMotion.GetStatus() 로 전체 축 상태를 한 번에 읽어 캐시 → Wmx3Axis.ReadStatus 에서 사용
    public void Update() { }
    public void Dispose() => Close();
}

/// <summary>
/// 축 1개. 단위 변환: WMX3 는 기어비(Gear Ratio) 설정으로 사용자 단위를 쓸 수 있으므로
/// 엔진에서 기어비를 설정했다면 PulsePerUnit 변환을 하지 않는다. 한 방식만 사용할 것.
/// MR-J5-G-N1 : 서보 알람 코드는 드라이브 알람 번호(예: AL.16)로 AxisStatus.AlarmCode 에 넣는다.
/// </summary>
internal sealed class Wmx3Axis : IAxis
{
    private readonly AxisConfig _cfg;
    public Wmx3Axis(AxisConfig cfg) { _cfg = cfg; }
    public int AxisNo => _cfg.AxisNo;

    // TODO(WMX3): CoreMotionStatus.AxesStatus[AxisNo] → ActualPos, CommandPos, ActualVelocity,
    //             ServoOn, AmpAlarm/AmpAlarmCode, HomeDone, PositiveLS/NegativeLS, OpState(Idle/Pos/Jog/Home)
    public AxisStatus ReadStatus() => default;
    public void ServoOn(bool on) => Todo();               // AxisControl.SetServoOn(axis, on ? 1 : 0)
    public void MoveAbsolute(double p, double v, double a, double d) => Todo(); // Motion.StartPos(PosCommand)
    public void MoveRelative(double p, double v, double a, double d) => Todo(); // Motion.StartMov(PosCommand)
    public void Jog(double v, double a) => Todo();         // Motion.StartJog(JogCommand)
    public void Stop(double d) => Todo();                  // Motion.Stop(axis)
    public void EmergencyStop() => Todo();                 // Motion.ExecQuickStop(axis)
    public void StartHoming(HomeConfig h) => Todo();       // Config.SetHomeParam → Home.StartHome(axis)
    public void ClearAlarm() => Todo();                    // AxisControl.ClearAmpAlarm / ClearAxisAlarm
    private static void Todo() => throw new NotImplementedException("WMX3 축 명령 미구현");
}

internal sealed class Wmx3IoController : IIoController
{
    private readonly Wmx3Session _s;
    private readonly IoImageConfig _img;
    public Wmx3IoController(Wmx3Session s, IoImageConfig img) { _s = s; _img = img; }
    public bool IsOpen => _s.IsOpen;
    public void Open() => _s.Open();
    public void Close() => _s.Close();
    // TODO(WMX3): Io.GetInBytes(0, _img.InputSize, ref buffer)
    public void ReadInputs(Span<byte> image) { }
    // TODO(WMX3): Io.SetOutBytes(0, _img.OutputSize, buffer)
    public void WriteOutputs(ReadOnlySpan<byte> image) { }
    public void Dispose() => Close();
}
