using Fa.Core.Hardware;

namespace Fa.Hal.Simulation;

/// <summary>
/// 가상 축. 사다리꼴 속도 프로파일을 단순 적분해서 위치를 계산한다.
/// 원점복귀는 현재 위치에서 0 으로 이동하는 것으로 흉내 낸다.
/// </summary>
internal sealed class SimAxis : IAxis
{
    private readonly object _lock = new();
    private readonly AxisConfig _cfg;
    private double _pos, _vel, _target, _maxVel, _acc, _dec;
    private bool _servo, _busy, _homeDone, _homing, _jog, _alarm;

    public SimAxis(AxisConfig cfg) { _cfg = cfg; }

    public int AxisNo => _cfg.AxisNo;

    public AxisStatus ReadStatus()
    {
        lock (_lock)
            return new AxisStatus(_target, _pos, _vel, _servo, _busy,
                !_busy && Math.Abs(_pos - _target) <= _cfg.InPositionTolerance,
                _homeDone, _homing, _alarm, _alarm ? 0x16 : 0, false, false);
    }

    public void ServoOn(bool on) { lock (_lock) { _servo = on && !_alarm; if (!on) Halt(); } }

    public void MoveAbsolute(double position, double velocity, double acc, double dec)
    {
        lock (_lock)
        {
            if (!_servo) return;
            _target = position; _maxVel = Math.Abs(velocity); _acc = acc; _dec = dec;
            _jog = false; _busy = true;
        }
    }

    public void MoveRelative(double distance, double velocity, double acc, double dec)
    {
        double t; lock (_lock) t = _pos + distance;
        MoveAbsolute(t, velocity, acc, dec);
    }

    public void Jog(double velocity, double acc)
    {
        lock (_lock)
        {
            if (!_servo) return;
            _target = velocity >= 0 ? double.MaxValue : double.MinValue;
            _maxVel = Math.Abs(velocity); _acc = acc; _dec = acc; _jog = true; _busy = true;
        }
    }

    public void Stop(double dec)
    {
        lock (_lock)
        {
            if (!_busy) return;
            // 현재 속도에서 감속 정지할 위치를 새 목표로
            double dist = _vel * _vel / (2 * Math.Max(dec, 1));
            _target = _pos + Math.Sign(_vel) * dist;
            _dec = dec; _jog = false; _homing = false;
            if (_vel == 0) { _target = _pos; _busy = false; }
        }
    }

    public void EmergencyStop() { lock (_lock) { Halt(); _homing = false; } }

    public void StartHoming(HomeConfig home)
    {
        lock (_lock)
        {
            if (!_servo) return;
            _homeDone = false; _homing = true;
            _target = home.Offset; _maxVel = Math.Abs(home.Velocity) * 5; _acc = home.Acc * 5; _dec = home.Acc * 5;
            _jog = false; _busy = true;
        }
    }

    public void ClearAlarm() { lock (_lock) _alarm = false; }

    /// <summary>시험용: 서보 알람 발생.</summary>
    public void InjectAlarm() { lock (_lock) { _alarm = true; _servo = false; Halt(); } }

    private void Halt() { _vel = 0; _target = _pos; _busy = false; _jog = false; }

    /// <summary>dt(초) 만큼 진행.</summary>
    public void Advance(double dt)
    {
        lock (_lock)
        {
            if (!_busy) { _vel = 0; return; }
            double remain = _target - _pos;
            double dir = Math.Sign(remain);
            double stopDist = _vel * _vel / (2 * Math.Max(_dec, 1));
            double speed = Math.Abs(_vel);

            if (!_jog && Math.Abs(remain) <= stopDist) speed = Math.Max(speed - _dec * dt, 0);
            else speed = Math.Min(speed + _acc * dt, _maxVel);

            double step = speed * dt;
            if (!_jog && (step >= Math.Abs(remain) || speed == 0 && Math.Abs(remain) < 1e-3))
            {
                _pos = _target; _vel = 0; _busy = false;
                if (_homing) { _homing = false; _homeDone = true; _pos = 0; _target = 0; }
                return;
            }
            _pos += dir * step;
            _vel = dir * speed;
            if (speed == 0) // 감속 완료했지만 목표 미도달 → 다시 가속
                _vel = dir * Math.Min(_acc * dt, _maxVel);
        }
    }
}
