using Fa.Core.Alarms;
using Fa.Core.Common;
using Fa.Core.Data;
using Fa.Core.Hardware;
using Fa.Core.Io;
using Fa.Core.Motion;
using Fa.Core.Sequence;
using Microsoft.Extensions.Logging;

namespace Fa.Core.Machine;

/// <summary>
/// 설비 전체를 묶는 중심 객체. 상태 모델, 운전 명령, 스레드 구동을 담당한다.
/// 스레드 구성:
///   IO 스레드        : IoManager.Scan          (입력 읽기 / 출력 쓰기)
///   모션 스레드      : MotionManager.Scan      (축 상태 갱신)
///   감시 스레드      : Supervise               (비상정지, 축 알람, 상태 전이, 타워램프)
///   유닛 스레드 × N  : UnitSequence.Scan       (유닛마다 독립 실행)
/// </summary>
public sealed class MachineController : IDisposable
{
    private readonly object _cmdLock = new();
    private readonly List<UnitSequence> _units = new();
    private readonly List<PeriodicWorker> _workers = new();
    private readonly ILoggerFactory _logFactory;
    private readonly ILogger _log;
    private volatile MachineState _state = MachineState.Init;
    private volatile OperationMode _mode = OperationMode.Manual;
    private volatile bool _homing;
    private int _lampBlink;

    public MachineController(MachineConfig config, MotionManager motion, IoManager io, AlarmManager alarms,
        InterlockManager interlocks, RecipeManager recipe, ParameterManager parameters, ILoggerFactory logFactory)
    {
        Config = config; Motion = motion; Io = io; Alarms = alarms; Interlocks = interlocks;
        Recipe = recipe; Parameters = parameters;
        _logFactory = logFactory;
        _log = logFactory.CreateLogger("Machine");
    }

    public MachineConfig Config { get; }
    public MotionManager Motion { get; }
    public IoManager Io { get; }
    public AlarmManager Alarms { get; }
    public InterlockManager Interlocks { get; }
    public RecipeManager Recipe { get; }
    public ParameterManager Parameters { get; }

    public MachineState State => _state;
    public OperationMode Mode => _mode;
    public bool IsHoming => _homing;
    public bool IsSimulation => Config.Controller.Kind == ControllerKind.Simulation;
    public IReadOnlyList<UnitSequence> Units => _units;
    public IReadOnlyList<PeriodicWorker> Workers => _workers;

    /// <summary>수동 조작 가능: 운전/원점 중이 아니고 비상정지가 아닐 때.</summary>
    /// <summary>모든 유닛 원점 완료 (비상정지 후에는 축 HomeDone 과 무관하게 다시 잡아야 함).</summary>
    public bool AllHomed => _units.All(u => u.HomeState == HomeState.Done) && Motion.AllHomeDone;

    public bool CanManualOperate => _state is not (MachineState.Run or MachineState.EStop) && !_homing;

    public event Action<MachineState, MachineState>? StateChanged;
    public event Action<UnitSequence, double>? CycleCompleted;

    /// <summary>설비 모듈이 유닛을 등록. StartRuntime 전에 호출.</summary>
    public void AddUnit(UnitSequence unit)
    {
        unit.Attach(this, _logFactory.CreateLogger($"Seq.{unit.Name}"));
        _units.Add(unit);
    }

    public UnitSequence? FindUnit(string name) => _units.FirstOrDefault(u => u.Name == name);

    // ───────── 구동 ─────────

    public void StartRuntime()
    {
        _log.LogInformation("설비 시작: {Machine} / 컨트롤러 {Kind}", Config.MachineName, Config.Controller.Kind);
        Motion.Controller.Open();
        if (!Io.Controller.IsOpen) Io.Controller.Open();
        Motion.CreateAxes();

        var t = Config.Threads;
        // 1) IO / 모션 스레드 먼저 시작 → 첫 입력 이미지와 축 상태 확보
        var io = new PeriodicWorker("IO", t.IoScanMs, Io.Scan, _log, ThreadPriority.Highest);
        var mo = new PeriodicWorker("Motion", t.MotionScanMs, Motion.Scan, _log, ThreadPriority.Highest);
        _workers.Add(io); _workers.Add(mo);
        io.Start(); mo.Start();
        if (!SpinWait.SpinUntil(() => Io.ScanCount >= 3, 3000)) _log.LogError("IO 스캔이 시작되지 않습니다.");

        // 2) 서보 ON (상태가 실제로 ON 이 될 때까지 대기)
        Motion.ServoAll(true);
        if (!SpinWait.SpinUntil(() => Motion.Axes.Values.All(a => a.Status.ServoOn), 3000))
            _log.LogWarning("서보 ON 확인 실패: {Axes}", string.Join(",", Motion.Axes.Values.Where(a => !a.Status.ServoOn).Select(a => a.Name)));

        // 3) 감시, 유닛 시퀀스 스레드 시작 (입력이 유효해진 뒤에 비상정지 등을 판단하도록)
        var workers = new List<PeriodicWorker> { new("Supervisor", t.SupervisorScanMs, Supervise, _log) };
        foreach (var u in _units)
            workers.Add(new PeriodicWorker($"Seq.{u.Name}", t.SequenceScanMs, u.Scan, _log));
        _workers.AddRange(workers);
        foreach (var w in workers) w.Start();
        SetState(MachineState.Init);
    }

    public void StopRuntime()
    {
        _log.LogInformation("설비 종료");
        Motion.StopAll();
        Thread.Sleep(100);
        Motion.ServoAll(false);
        Io.AllOutputsOff();
        Thread.Sleep(Config.Threads.IoScanMs * 3); // OFF 출력이 실제로 나가도록
        foreach (var w in _workers) w.Stop();
        _workers.Clear();
        Motion.Controller.Close();
        Io.Controller.Close();
    }

    public void Dispose() => StopRuntime();

    // ───────── 운전 명령 (UI, 상위통신) ─────────

    public OpResult SetMode(OperationMode mode)
    {
        lock (_cmdLock)
        {
            if (_state == MachineState.Run) return OpResult.Fail("운전 중에는 모드를 바꿀 수 없습니다.");
            _mode = mode;
            _log.LogInformation("운전 모드 → {Mode}", mode);
            return OpResult.Success;
        }
    }

    public OpResult Home()
    {
        lock (_cmdLock)
        {
            if (_state is MachineState.Run or MachineState.EStop or MachineState.Alarm)
                return OpResult.Fail($"{_state} 상태에서는 원점복귀할 수 없습니다.");
            if (_homing) return OpResult.Fail("원점복귀 중입니다.");
            SequenceReset?.Invoke();
            foreach (var u in _units) u.BeginHoming();
            _homing = true;
            SetState(MachineState.Init);
            return OpResult.Success;
        }
    }

    /// <summary>운전 시작. policy 가 null 이면 파라미터 기본값 사용.</summary>
    public OpResult Start(ResumePolicy? policy = null)
    {
        lock (_cmdLock)
        {
            if (_mode == OperationMode.Manual) return OpResult.Fail("Manual 모드에서는 Start 할 수 없습니다.");
            if (_state is not (MachineState.Ready or MachineState.Stop)) return OpResult.Fail($"{_state} 상태에서는 Start 할 수 없습니다.");
            if (Alarms.HasHeavy) return OpResult.Fail("알람을 먼저 해제하세요.");
            if (!AllHomed) return OpResult.Fail("원점복귀가 필요합니다.");

            var p = policy ?? Parameters.GetEnum<ResumePolicy>(PlatformParameters.ResumePolicy);
            foreach (var u in _units) u.PrepareStart(p);
            if (p == ResumePolicy.Restart) SequenceReset?.Invoke();
            _log.LogInformation("Start ({Mode}, {Policy})", _mode, p);
            SetState(MachineState.Run);
            return OpResult.Success;
        }
    }

    /// <summary>원점복귀 시작 / '처음부터' 시작 시 발생. 설비 모듈이 유닛 간 인터페이스(Handoff) 등을 초기화.</summary>
    public event Action? SequenceReset;

    public OpResult Stop()
    {
        lock (_cmdLock)
        {
            if (_homing)
            {
                _homing = false;
                foreach (var u in _units) u.AbortHoming();
                Motion.StopAll();
                return OpResult.Success;
            }
            if (_state != MachineState.Run) return OpResult.Fail("운전 중이 아닙니다.");
            Motion.StopAll();
            SetState(MachineState.Stop);
            return OpResult.Success;
        }
    }

    public OpResult Reset(string user)
    {
        lock (_cmdLock)
        {
            if (_state == MachineState.EStop && IsEStopActive())
                return OpResult.Fail("비상정지 스위치를 먼저 해제하세요.");

            foreach (var a in Motion.Axes.Values.Where(a => a.Status.Alarm)) a.ClearAlarm();
            Alarms.ResetAll(user);

            if (_state == MachineState.EStop)
            {
                // 비상정지 후에는 원점을 다시 잡는다
                foreach (var u in _units) u.InvalidateHome();
                Motion.ServoAll(true);
                SetState(MachineState.Init);
            }
            else if (_state == MachineState.Alarm)
            {
                SetState(AllHomed ? MachineState.Stop : MachineState.Init);
            }
            return OpResult.Success;
        }
    }

    /// <summary>Step 모드에서 다음 스텝 진행. unitName 이 null 이면 모든 유닛.</summary>
    public void StepNext(string? unitName = null)
    {
        foreach (var u in _units.Where(u => unitName == null || u.Name == unitName)) u.TriggerStep();
    }

    internal void NotifyCycleCompleted(UnitSequence unit, double tactSec) => CycleCompleted?.Invoke(unit, tactSec);

    // ───────── 감시 스레드 ─────────

    private bool IsEStopActive()
        => Config.Safety.EStopInput is { } name && Io.Inputs.TryGetValue(name, out var di) && di.IsOn;

    private void Supervise()
    {
        // 1) 비상정지
        if (IsEStopActive())
        {
            if (_state != MachineState.EStop) EnterEStop();
            Alarms.Raise(PlatformAlarms.EStop, "SAFETY");
        }

        // 2) 도어 (운전 중에만)
        if (_state == MachineState.Run && Parameters.GetBool(PlatformParameters.DoorCheckEnabled))
            foreach (var d in Config.Safety.DoorInputs)
                if (Io.Inputs.TryGetValue(d, out var di) && di.IsOn) Alarms.Raise(PlatformAlarms.DoorOpen, d);

        // 3) 축 알람 / 리밋
        foreach (var a in Motion.Axes.Values)
        {
            var s = a.Status;
            if (s.Alarm) Alarms.Raise(PlatformAlarms.ServoAlarm, a.Name, $"Code {s.AlarmCode}");
            if (s.PositiveLimit) Alarms.Raise(PlatformAlarms.PositiveLimit, a.Name);
            if (s.NegativeLimit) Alarms.Raise(PlatformAlarms.NegativeLimit, a.Name);
        }

        // 4) 중알람 → Alarm 상태
        if (Alarms.HasHeavy && _state is not (MachineState.Alarm or MachineState.EStop))
        {
            lock (_cmdLock)
            {
                Motion.StopAll();
                if (_homing) { _homing = false; foreach (var u in _units) u.AbortHoming(); }
                SetState(MachineState.Alarm);
            }
        }

        // 5) 원점복귀 완료
        if (_homing && _units.All(u => u.HomeState == HomeState.Done))
        {
            lock (_cmdLock)
            {
                _homing = false;
                foreach (var u in _units) u.MarkFresh();
                if (_state == MachineState.Init) SetState(MachineState.Ready);
            }
        }

        UpdateTowerLamp();
    }

    private void EnterEStop()
    {
        lock (_cmdLock)
        {
            _log.LogWarning("비상정지 발생");
            Motion.EmergencyStopAll();
            Motion.ServoAll(false);
            Io.AllOutputsOff();
            _homing = false;
            foreach (var u in _units) u.AbortHoming();
            SetState(MachineState.EStop);
        }
    }

    private void UpdateTowerLamp()
    {
        var s = Config.Safety;
        bool blink = (++_lampBlink / 25) % 2 == 0; // 감시 주기 10ms 기준 약 0.5초 점멸
        SetLamp(s.TowerLampGreen, _state == MachineState.Run);
        SetLamp(s.TowerLampYellow, _state is MachineState.Ready or MachineState.Stop || (_homing && blink));
        SetLamp(s.TowerLampRed, _state == MachineState.EStop || (_state == MachineState.Alarm && blink));
        SetLamp(s.Buzzer, _state == MachineState.Alarm && Alarms.HasHeavy);
    }

    private void SetLamp(string? name, bool on)
    {
        if (name != null && Io.Outputs.TryGetValue(name, out var o) && o.Value != on) o.Set(on);
    }

    private void SetState(MachineState s)
    {
        var old = _state;
        if (old == s) return;
        _state = s;
        _log.LogInformation("상태 {Old} → {New}", old, s);
        StateChanged?.Invoke(old, s);
    }
}
