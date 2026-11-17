namespace Fa.Core.Hardware;

/// <summary>축 상태 스냅샷. 모션 스레드가 매 스캔 갱신한다. 위치/속도는 사용자 단위(mm, deg).</summary>
public readonly record struct AxisStatus(
    double CommandPosition,
    double ActualPosition,
    double ActualVelocity,
    bool ServoOn,
    bool Busy,
    bool InPosition,
    bool HomeDone,
    bool Homing,
    bool Alarm,
    int AlarmCode,
    bool PositiveLimit,
    bool NegativeLimit);

/// <summary>
/// 벤더 드라이버가 구현하는 축 인터페이스 (WMX3, MMCE, Simulation).
/// 인터록·소프트리밋 같은 공통 로직은 여기 넣지 않는다 → Core 의 <see cref="Motion.Axis"/> 가 담당.
/// </summary>
public interface IAxis
{
    int AxisNo { get; }
    AxisStatus ReadStatus();
    void ServoOn(bool on);
    void MoveAbsolute(double position, double velocity, double acc, double dec);
    void MoveRelative(double distance, double velocity, double acc, double dec);
    /// <summary>velocity 부호로 방향 결정.</summary>
    void Jog(double velocity, double acc);
    void Stop(double dec);
    void EmergencyStop();
    void StartHoming(HomeConfig home);
    void ClearAlarm();
}

/// <summary>벤더 모션 컨트롤러. Open 후 축 설정마다 IAxis 를 만든다.</summary>
public interface IMotionController : IDisposable
{
    string Name { get; }
    bool IsOpen { get; }
    void Open();
    void Close();
    IAxis CreateAxis(AxisConfig config);
    /// <summary>모션 스레드가 매 스캔 호출 (상태 일괄 읽기 등). 시뮬레이터는 여기서 궤적을 계산한다.</summary>
    void Update();
}

/// <summary>
/// 벤더 IO 컨트롤러. EtherCAT 프로세스 이미지(byte 배열)만 주고받는다.
/// DIO/AIO 모두 이미지 안의 위치로 정의하므로 IO 제조사에 의존하지 않는다.
/// </summary>
public interface IIoController : IDisposable
{
    bool IsOpen { get; }
    void Open();
    void Close();
    void ReadInputs(Span<byte> image);
    void WriteOutputs(ReadOnlySpan<byte> image);
}

/// <summary>시뮬레이션 IO: 화면에서 입력을 강제로 바꿀 때 사용.</summary>
public interface ISimulatedIo
{
    void SetInputBit(int byteIndex, int bit, bool value);
    void SetInputBytes(int byteIndex, ReadOnlySpan<byte> data);
}

/// <summary>컨트롤러 종류별로 모션/IO 컨트롤러를 만든다. 벤더 프로젝트마다 하나씩 구현.</summary>
public interface IHardwareFactory
{
    ControllerKind Kind { get; }
    IMotionController CreateMotion(MachineConfig config);
    IIoController CreateIo(MachineConfig config);
}
