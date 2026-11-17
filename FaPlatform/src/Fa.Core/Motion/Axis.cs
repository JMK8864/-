using Fa.Core.Common;
using Fa.Core.Hardware;
using Fa.Core.Machine;

namespace Fa.Core.Motion;

/// <summary>
/// 시퀀스와 UI 가 사용하는 축 객체. 벤더 드라이버(IAxis) 를 감싸고
/// 공통 검사(서보, 원점, 소프트리밋, 알람, 인터록)를 한 곳에서 처리한다.
/// </summary>
public sealed class Axis
{
    private readonly IAxis _driver;
    private readonly InterlockManager _interlocks;
    private AxisStatus _status;

    public Axis(IAxis driver, AxisConfig config, InterlockManager interlocks)
    {
        _driver = driver; Config = config; _interlocks = interlocks;
    }

    public string Name => Config.Name;
    public AxisConfig Config { get; }
    public AxisStatus Status => _status;
    public double Position => _status.ActualPosition;
    public bool IsHomeDone => _status.HomeDone;
    public bool IsMoving => _status.Busy;
    /// <summary>마지막 이동 명령의 목표 위치.</summary>
    public double LastTarget { get; private set; } = double.NaN;

    /// <summary>모션 스레드가 매 스캔 호출.</summary>
    internal void Refresh() => _status = _driver.ReadStatus();

    /// <summary>절대 이동. speedPercent 는 DefaultVelocity 기준 비율.</summary>
    public OpResult MoveTo(double position, double speedPercent = 100)
    {
        var check = CheckReady(position, requireHome: true);
        if (!check.Ok) return check;
        if (position < Config.SoftLimitMin || position > Config.SoftLimitMax)
            return OpResult.Fail($"{Name}: 소프트리밋 초과 ({position:F3})");

        double vel = Math.Min(Config.DefaultVelocity * speedPercent / 100.0, Config.MaxVelocity);
        LastTarget = position;
        _driver.MoveAbsolute(position, vel, Config.DefaultAcc, Config.DefaultDec);
        return OpResult.Success;
    }

    public OpResult MoveBy(double distance, double speedPercent = 100) => MoveTo(Position + distance, speedPercent);

    /// <summary>이동 완료: 명령 완료 + 목표 위치 도달.</summary>
    public bool IsMoveDone(double target)
        => !_status.Busy && _status.InPosition && Math.Abs(_status.ActualPosition - target) <= Config.InPositionTolerance;

    public bool IsAt(double position) => Math.Abs(_status.ActualPosition - position) <= Config.InPositionTolerance;

    /// <summary>direction: +1 / -1. 원점 전에도 가능 (수동 조작용).</summary>
    public OpResult Jog(int direction, double speedPercent = 100)
    {
        var check = CheckReady(double.NaN, requireHome: false);
        if (!check.Ok) return check;
        _driver.Jog(Math.Sign(direction) * Config.JogVelocity * speedPercent / 100.0, Config.DefaultAcc);
        return OpResult.Success;
    }

    public OpResult StartHoming()
    {
        if (!_status.ServoOn) return OpResult.Fail($"{Name}: 서보 OFF");
        if (_status.Alarm) return OpResult.Fail($"{Name}: 서보 알람 ({_status.AlarmCode})");
        var il = _interlocks.CheckMotion(Name, double.NaN);
        if (!il.Ok) return il;
        _driver.StartHoming(Config.Home);
        return OpResult.Success;
    }

    public void Stop() => _driver.Stop(Config.DefaultDec);
    public void EmergencyStop() => _driver.EmergencyStop();
    public void ServoOn(bool on) => _driver.ServoOn(on);
    public void ClearAlarm() => _driver.ClearAlarm();

    private OpResult CheckReady(double target, bool requireHome)
    {
        if (!_status.ServoOn) return OpResult.Fail($"{Name}: 서보 OFF");
        if (_status.Alarm) return OpResult.Fail($"{Name}: 서보 알람 ({_status.AlarmCode})");
        if (requireHome && !_status.HomeDone) return OpResult.Fail($"{Name}: 원점 미완료");
        return _interlocks.CheckMotion(Name, target);
    }
}
