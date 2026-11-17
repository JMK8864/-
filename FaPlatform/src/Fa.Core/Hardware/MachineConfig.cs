namespace Fa.Core.Hardware;

/// <summary>
/// 설비 하드웨어 구성 (Data/Config/machine.json).
/// 축 수, IO 접점은 설비마다 다르므로 모두 이 파일에서 정의한다. 코드는 이름(Name)으로만 참조한다.
/// </summary>
public sealed class MachineConfig
{
    public string MachineName { get; set; } = "Machine";
    public ControllerConfig Controller { get; set; } = new();
    public ThreadConfig Threads { get; set; } = new();
    public SafetyConfig Safety { get; set; } = new();
    public List<AxisConfig> Axes { get; set; } = new();
    public IoImageConfig IoImage { get; set; } = new();
    public List<DigitalPointConfig> DigitalInputs { get; set; } = new();
    public List<DigitalPointConfig> DigitalOutputs { get; set; } = new();
    public List<AnalogPointConfig> AnalogInputs { get; set; } = new();
    public List<AnalogPointConfig> AnalogOutputs { get; set; } = new();
    public SimulationConfig Simulation { get; set; } = new();
}

public enum ControllerKind { Simulation, Wmx3, Mmce }

public sealed class ControllerConfig
{
    public ControllerKind Kind { get; set; } = ControllerKind.Simulation;
    /// <summary>벤더별 설정 (설치 경로, 통신 타임아웃 등). 키/값은 각 드라이버가 해석한다.</summary>
    public Dictionary<string, string> Options { get; set; } = new();
}

/// <summary>스레드 주기(ms). 모션 컨트롤러 자체 스캔 주기는 컨트롤러 기본값을 사용한다.</summary>
public sealed class ThreadConfig
{
    public int IoScanMs { get; set; } = 2;
    public int MotionScanMs { get; set; } = 2;
    public int SequenceScanMs { get; set; } = 5;
    public int SupervisorScanMs { get; set; } = 10;
}

/// <summary>안전 관련 입력 이름. 비워두면 해당 감시를 하지 않는다.</summary>
public sealed class SafetyConfig
{
    public string? EStopInput { get; set; }          // ON = 비상정지 상태 (B접점이면 Inverted 사용)
    public List<string> DoorInputs { get; set; } = new(); // ON = 도어 열림
    public string? TowerLampRed { get; set; }
    public string? TowerLampYellow { get; set; }
    public string? TowerLampGreen { get; set; }
    public string? Buzzer { get; set; }
}

public sealed class AxisConfig
{
    public string Name { get; set; } = "";
    public int AxisNo { get; set; }
    public string Unit { get; set; } = "mm";
    /// <summary>1 단위(mm/deg)당 펄스 수. 드라이버가 단위 변환에 사용.</summary>
    public double PulsePerUnit { get; set; } = 1000;
    public double MaxVelocity { get; set; } = 500;
    public double DefaultVelocity { get; set; } = 100;
    public double DefaultAcc { get; set; } = 1000;
    public double DefaultDec { get; set; } = 1000;
    public double JogVelocity { get; set; } = 20;
    public double SoftLimitMin { get; set; } = double.MinValue;
    public double SoftLimitMax { get; set; } = double.MaxValue;
    public double InPositionTolerance { get; set; } = 0.01;
    public int MoveTimeoutMs { get; set; } = 10000;
    public HomeConfig Home { get; set; } = new();
}

public sealed class HomeConfig
{
    /// <summary>드라이버별 원점 방식 이름 (예: "HomeSensor", "LimitSwitch", "ZPulse").</summary>
    public string Method { get; set; } = "HomeSensor";
    public double Velocity { get; set; } = 20;
    public double Acc { get; set; } = 200;
    public bool NegativeDirection { get; set; } = true;
    public double Offset { get; set; }
    public int TimeoutMs { get; set; } = 30000;
    /// <summary>유닛 안에서 원점 순서 (작은 값 먼저). 예: Z=0, Y=1.</summary>
    public int Order { get; set; }
}

/// <summary>EtherCAT 프로세스 이미지 크기 (byte).</summary>
public sealed class IoImageConfig
{
    public int InputSize { get; set; } = 256;
    public int OutputSize { get; set; } = 256;
}

/// <summary>디지털 접점: 프로세스 이미지의 Byte.Bit 위치.</summary>
public sealed class DigitalPointConfig
{
    public string Name { get; set; } = "";
    public int Byte { get; set; }
    public int Bit { get; set; }
    public bool Inverted { get; set; }
    public string Description { get; set; } = "";
}

public enum AnalogDataType { Int16, UInt16, Int32, Float32 }

/// <summary>아날로그 채널: 이미지의 Byte 오프셋 + 스케일링(Raw → 공학 단위).</summary>
public sealed class AnalogPointConfig
{
    public string Name { get; set; } = "";
    public int Byte { get; set; }
    public AnalogDataType DataType { get; set; } = AnalogDataType.Int16;
    public double RawMin { get; set; } = 0;
    public double RawMax { get; set; } = 32767;
    public double EngMin { get; set; } = 0;
    public double EngMax { get; set; } = 10;
    public string Unit { get; set; } = "V";
    public string Description { get; set; } = "";
}

public sealed class SimulationConfig
{
    /// <summary>출력 → 입력 자동 연결 (예: 진공 ON → 진공 OK 센서 150ms 후 ON).</summary>
    public List<SimLink> Links { get; set; } = new();
    /// <summary>시작 시 입력 초기값 (예: 비상정지 B접점 정상 상태).</summary>
    public Dictionary<string, bool> InitialInputs { get; set; } = new();
    public Dictionary<string, double> InitialAnalogInputs { get; set; } = new();
}

public sealed class SimLink
{
    public string Output { get; set; } = "";
    public string Input { get; set; } = "";
    public bool Invert { get; set; }
    public int DelayMs { get; set; } = 100;
}
