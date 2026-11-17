using System.Diagnostics;
using Fa.Core.Alarms;
using Fa.Core.Data;
using Fa.Core.Io;
using Fa.Core.Machine;
using Fa.Core.Motion;
using Microsoft.Extensions.Logging;

namespace Fa.Core.Sequence;

public enum HomeState { NotDone, Running, Done, Failed }

/// <summary>
/// 유닛 시퀀스 기본 클래스. 유닛마다 하나씩 상속해서 AutoStep() 을 switch(Step) 로 작성한다.
/// 각 유닛은 자기 전용 스레드에서 주기적으로 실행되며, 한 번의 호출 안에서 대기(Sleep)하지 않는다.
///
///   case 10: if (!Move(ldY, Teach("LD_Y","Pick"))) break; Next(11, "Y 픽업위치 이동 대기"); break;
///   case 11: if (ldY.IsMoveDone(ldY.LastTarget)) Next(20); else CheckTimeout(); break;
///
/// '이어서' 재시작 위치:
///   - 기본은 멈춘 그 스텝.
///   - 단, 그 스텝이 앞 스텝에서 내린 명령(Move/SetOutput)의 완료를 기다리는 중이었다면
///     명령을 내린 스텝으로 돌아가 명령을 다시 내린다 (정지 시 축이 감속 정지했으므로).
/// </summary>
public abstract class UnitSequence
{
    private readonly Stopwatch _stepTimer = new();
    private readonly Stopwatch _cycleTimer = new();
    private int _retryStep;
    private bool _commandInStep; // 현재 스텝에서 Move/SetOutput 을 내렸는지
    private volatile bool _waitTrigger;
    private bool _fresh = true;

    // 원점복귀 진행 정보
    private List<List<Axis>> _homeGroups = new();
    private int _homeGroup;
    private bool _homeIssued;

    protected UnitSequence(string name) { Name = name; }

    public string Name { get; }
    public int Step { get; private set; }
    public string StepName { get; private set; } = "";
    public HomeState HomeState { get; private set; } = HomeState.NotDone;
    public bool WaitingStepTrigger => _waitTrigger;
    public int CycleCount { get; private set; }
    public double LastTactSec { get; private set; }
    public long StepElapsedMs => _stepTimer.ElapsedMilliseconds;

    protected MachineController M { get; private set; } = null!;
    protected ILogger Log { get; private set; } = null!;

    /// <summary>원점복귀할 축 이름. 축의 Home.Order 순서(작은 값 먼저)로 그룹 원점.</summary>
    protected virtual IEnumerable<string> HomeAxes => Array.Empty<string>();

    /// <summary>자동 운전 1 스캔. switch(Step) 으로 작성.</summary>
    protected abstract void AutoStep();
    /// <summary>원점 그룹을 시작해도 되는지 (다른 유닛 축이 먼저 원점을 잡아야 할 때 false).</summary>
    protected virtual bool CanStartHomeGroup(int groupIndex) => true;
    /// <summary>원점 시작 전 안전 동작 (진공 OFF, 실린더 상승 등).</summary>
    protected virtual void OnHomeStart() { }
    /// <summary>'처음부터' 시작 시 초기화 (Step 은 자동으로 0).</summary>
    protected virtual void OnRestart() { }

    // ───────── 플랫폼이 호출 ─────────

    internal void Attach(MachineController machine, ILogger log) { M = machine; Log = log; }

    internal void Scan()
    {
        try
        {
            if (M.IsHoming)
            {
                if (HomeState == HomeState.Running) HomeScan();
                return;
            }
            if (M.State != MachineState.Run) return;
            if (M.Mode == OperationMode.Step && _waitTrigger) return;
            AutoStep();
        }
        catch (Exception ex)
        {
            M.Alarms.Raise(PlatformAlarms.SequenceException, Name, $"Step {Step}: {ex.Message}");
            Log.LogError(ex, "[{Unit}] Step {Step} 예외", Name, Step);
        }
    }

    internal void BeginHoming()
    {
        _homeGroups = HomeAxes.Select(n => M.Motion[n])
            .GroupBy(a => a.Config.Home.Order).OrderBy(g => g.Key)
            .Select(g => g.ToList()).ToList();
        _homeGroup = 0;
        _homeIssued = false;
        _stepTimer.Restart();
        HomeState = HomeState.Running;
        OnHomeStart();
        Log.LogInformation("[{Unit}] 원점복귀 시작 ({Count} 그룹)", Name, _homeGroups.Count);
    }

    internal void AbortHoming()
    {
        if (HomeState == HomeState.Running) HomeState = HomeState.NotDone;
    }

    internal void InvalidateHome() => HomeState = HomeState.NotDone;

    /// <summary>원점 직후 첫 Start 는 항상 처음부터.</summary>
    internal void MarkFresh() => _fresh = true;

    internal void PrepareStart(ResumePolicy policy)
    {
        if (_fresh || policy == ResumePolicy.Restart)
        {
            Step = 0; StepName = ""; _retryStep = 0; _commandInStep = false;
            OnRestart();
            Log.LogInformation("[{Unit}] 처음부터 시작", Name);
        }
        else
        {
            Log.LogInformation("[{Unit}] 이어서 시작: Step {Step} → {Retry}", Name, Step, _retryStep);
            Step = _retryStep;
            _commandInStep = false;
        }
        _fresh = false;
        _waitTrigger = false;
        _stepTimer.Restart();
        if (!_cycleTimer.IsRunning) _cycleTimer.Start();
    }

    internal void TriggerStep() => _waitTrigger = false;

    private void HomeScan()
    {
        if (_homeGroup >= _homeGroups.Count)
        {
            HomeState = HomeState.Done;
            Log.LogInformation("[{Unit}] 원점복귀 완료", Name);
            return;
        }
        var group = _homeGroups[_homeGroup];
        if (!_homeIssued)
        {
            if (!CanStartHomeGroup(_homeGroup)) return;
            // 리셋 직후 서보 ON 이 반영될 때까지 잠시 기다린다
            if (group.Any(a => !a.Status.ServoOn) && _stepTimer.ElapsedMilliseconds < 3000) return;
            foreach (var axis in group)
            {
                var r = axis.StartHoming();
                if (!r.Ok)
                {
                    M.Alarms.Raise(PlatformAlarms.HomeFailed, axis.Name, r.Error);
                    HomeState = HomeState.Failed;
                    return;
                }
            }
            _homeIssued = true;
            _stepTimer.Restart();
            return;
        }
        // 명령 직후 상태가 갱신될 시간을 둔다
        if (_stepTimer.ElapsedMilliseconds < 100) return;
        if (group.All(a => a.Status.HomeDone && !a.Status.Homing))
        {
            _homeGroup++;
            _homeIssued = false;
            return;
        }
        foreach (var a in group.Where(a => !a.Status.HomeDone && _stepTimer.ElapsedMilliseconds > a.Config.Home.TimeoutMs))
        {
            a.Stop();
            M.Alarms.Raise(PlatformAlarms.HomeTimeout, a.Name);
            HomeState = HomeState.Failed;
        }
    }

    // ───────── 시퀀스 작성용 도우미 ─────────

    protected bool IsDryRun => M.Mode == OperationMode.DryRun;
    /// <summary>다른 유닛의 원점복귀 완료 여부 (유닛 간 원점 순서용).</summary>
    protected bool IsUnitHomed(string unitName) => M.FindUnit(unitName)?.HomeState == HomeState.Done;
    protected Axis Axis(string name) => M.Motion[name];
    protected DigitalInput DI(string name) => M.Io.DI(name);
    protected DigitalOutput DO(string name) => M.Io.DO(name);
    protected AnalogInput AI(string name) => M.Io.AI(name);
    protected AnalogOutput AO(string name) => M.Io.AO(name);
    protected double Teach(string axis, string point) => M.Recipe.Teach(axis, point);
    protected double RecipeValue(string key) => M.Recipe.Value(key);
    protected double SpeedPercent => M.Parameters.GetDouble(PlatformParameters.AutoSpeedPercent);

    /// <summary>다음 스텝으로 이동. Step 모드에서는 여기서 멈추고 'Next Step' 을 기다린다.</summary>
    protected void Next(int step, string name = "")
    {
        Log.LogInformation("[{Unit}] {From} → {To} {Name}", Name, Step, step, name);
        // 이 스텝에서 명령을 냈으면 다음 스텝은 그 완료를 기다리므로, 재시작 위치는 이 스텝으로 남긴다
        _retryStep = _commandInStep ? Step : step;
        _commandInStep = false;
        Step = step;
        StepName = name;
        _stepTimer.Restart();
        if (M.Mode == OperationMode.Step) _waitTrigger = true;
    }

    /// <summary>현재 스텝을 '이어서' 재시작 위치로 지정 (Move/SetOutput 은 자동으로 호출).</summary>
    protected void RetryPoint() { _retryStep = Step; _commandInStep = true; }

    protected bool Elapsed(int ms) => _stepTimer.ElapsedMilliseconds >= ms;

    /// <summary>스텝 대기 시간 초과 시 알람. 초과했으면 true.</summary>
    protected bool CheckTimeout(int? ms = null, int alarmCode = PlatformAlarms.StepTimeout)
    {
        int limit = ms ?? M.Parameters.GetInt(PlatformParameters.StepTimeoutMs);
        if (_stepTimer.ElapsedMilliseconds < limit) return false;
        Fail(alarmCode, $"Step {Step} {StepName} 시간 초과 ({limit} ms)");
        return true;
    }

    /// <summary>알람 발생. 중알람이면 설비가 Alarm 상태가 되어 모든 유닛이 멈춘다.</summary>
    protected void Fail(int alarmCode, string? detail = null)
    {
        M.Alarms.Raise(alarmCode, Name, detail ?? $"Step {Step} {StepName}");
        _stepTimer.Restart();
    }

    /// <summary>축 이동 명령. 거부(인터록 등)되면 알람 후 false.</summary>
    protected bool Move(Axis axis, double position, double? speedPercent = null)
    {
        RetryPoint();
        var r = axis.MoveTo(position, speedPercent ?? SpeedPercent);
        if (r.Ok) return true;
        Fail(PlatformAlarms.MoveRejected, r.Error);
        return false;
    }

    /// <summary>이동 완료 대기. 완료 true, 타임아웃이면 알람.</summary>
    protected bool WaitMove(Axis axis)
    {
        if (axis.IsMoveDone(axis.LastTarget)) return true;
        CheckTimeout(axis.Config.MoveTimeoutMs, PlatformAlarms.MoveTimeout);
        return false;
    }

    protected bool SetOutput(DigitalOutput output, bool on)
    {
        RetryPoint();
        var r = output.Set(on);
        if (r.Ok) return true;
        Fail(PlatformAlarms.MoveRejected, r.Error);
        return false;
    }

    /// <summary>센서 확인. 드라이런이면 센서를 보지 않고 dryRunValue 를 돌려준다 (자재/진공 센서용).</summary>
    protected bool Sensor(DigitalInput input, bool dryRunValue = true) => IsDryRun ? dryRunValue : input.Value;

    /// <summary>한 사이클 완료 (택타임 기록, 생산 이력 이벤트).</summary>
    protected void CycleDone()
    {
        CycleCount++;
        LastTactSec = _cycleTimer.Elapsed.TotalSeconds;
        _cycleTimer.Restart();
        Log.LogInformation("[{Unit}] 사이클 완료 #{Count} 택타임 {Tact:F2}s", Name, CycleCount, LastTactSec);
        M.NotifyCycleCompleted(this, LastTactSec);
    }
}
