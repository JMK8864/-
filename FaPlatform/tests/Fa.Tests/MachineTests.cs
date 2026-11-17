using Fa.Core.Alarms;
using Fa.Core.Machine;

namespace Fa.Tests;

public class MachineTests
{
    [Fact]
    public void Start_is_rejected_before_homing()
    {
        using var t = new TestMachine();
        Assert.Equal(MachineState.Init, t.M.State);
        t.M.SetMode(OperationMode.Auto);
        Assert.False(t.M.Start().Ok);
    }

    [Fact]
    public void Auto_run_completes_cycles_on_all_units()
    {
        using var t = new TestMachine();
        t.HomeAndStart();
        Assert.True(TestMachine.WaitUntil(() => t.M.FindUnit("Unloader")!.CycleCount >= 2, 20000), t.AlarmText);
        Assert.Equal(MachineState.Run, t.M.State);
        Assert.False(t.M.Alarms.HasAny, t.AlarmText);
    }

    [Fact]
    public void Stop_and_continue_repeatedly_does_not_raise_alarms()
    {
        using var t = new TestMachine();
        t.HomeAndStart();
        var rnd = new Random(1);
        for (int i = 0; i < 12; i++)
        {
            Thread.Sleep(rnd.Next(100, 700));
            var stop = t.M.Stop();
            Assert.True(stop.Ok, $"#{i} Stop: {stop.Error} / {t.AlarmText}");
            Thread.Sleep(150);
            var start = t.M.Start(ResumePolicy.Continue);
            Assert.True(start.Ok, $"#{i} Start: {start.Error} / {t.AlarmText}");
        }
        int before = t.M.FindUnit("Unloader")!.CycleCount;
        Assert.True(TestMachine.WaitUntil(() => t.M.FindUnit("Unloader")!.CycleCount >= before + 2, 20000), t.AlarmText);
        Assert.False(t.M.Alarms.HasAny, t.AlarmText);
    }

    [Fact]
    public void Restart_policy_resets_steps()
    {
        using var t = new TestMachine();
        t.HomeAndStart();
        Thread.Sleep(800);
        t.M.Stop();
        Assert.True(TestMachine.WaitUntil(() => !t.M.Motion.Axes.Values.Any(a => a.IsMoving), 3000));
        Assert.True(t.M.Start(ResumePolicy.Restart).Ok);
        Assert.True(TestMachine.WaitUntil(() => t.M.FindUnit("Unloader")!.CycleCount >= 1, 20000), t.AlarmText);
        Assert.False(t.M.Alarms.HasAny, t.AlarmText);
    }

    [Fact]
    public void EStop_stops_machine_and_requires_rehoming()
    {
        using var t = new TestMachine();
        t.HomeAndStart();
        Thread.Sleep(300);

        t.M.Io.SimulateInput("EMO", true);
        Assert.True(TestMachine.WaitUntil(() => t.M.State == MachineState.EStop, 2000));
        Assert.Contains(t.M.Alarms.Active, a => a.Code == PlatformAlarms.EStop);
        Assert.False(t.M.Reset("test").Ok); // 스위치가 눌린 동안 리셋 불가

        t.M.Io.SimulateInput("EMO", false);
        Thread.Sleep(50);
        Assert.True(t.M.Reset("test").Ok);
        Assert.Equal(MachineState.Init, t.M.State);
        Assert.False(t.M.Start().Ok); // 다시 원점 필요
        t.HomeAndStart();
        Assert.True(TestMachine.WaitUntil(() => t.M.FindUnit("Unloader")!.CycleCount >= 1, 20000), t.AlarmText);
    }

    [Fact]
    public void Clamp_sensor_failure_raises_alarm_and_recovers_with_continue()
    {
        using var t = new TestMachine();
        // 클램프 센서 대기 시간을 센서 응답(150ms)보다 짧게 → 클램프 시 알람
        t.M.Recipe.SetValue("Transfer.ClampTimeoutMs", 20);
        t.M.Io.SimulateInput("TR_CLAMP_OFF", true); // 시작 시 언클램프 센서는 이미 ON
        t.HomeAndStart();

        Assert.True(TestMachine.WaitUntil(() => t.M.State == MachineState.Alarm, 8000));
        Assert.Contains(t.M.Alarms.Active, a => a.Code == Fa.Machine.Sample.SampleMachineModule.TransferClampFail);

        t.M.Recipe.SetValue("Transfer.ClampTimeoutMs", 3000); // 원인 제거
        Assert.True(t.M.Reset("test").Ok);
        Assert.Equal(MachineState.Stop, t.M.State); // 원점 유지 → Stop
        Assert.True(t.M.Start(ResumePolicy.Continue).Ok);
        int before = t.M.FindUnit("Unloader")!.CycleCount;
        Assert.True(TestMachine.WaitUntil(() => t.M.FindUnit("Unloader")!.CycleCount > before, 20000), t.AlarmText);
    }

    [Fact]
    public void Interlock_blocks_transfer_when_loader_z_is_down()
    {
        using var t = new TestMachine();
        Assert.True(t.M.Home().Ok);
        Assert.True(TestMachine.WaitUntil(() => t.M.State == MachineState.Ready));
        var y = t.M.Motion["LD_Y"];
        Assert.True(y.MoveTo(300).Ok); // 스테이지 위 (Place)
        Assert.True(TestMachine.WaitUntil(() => y.IsMoveDone(300)));
        var z = t.M.Motion["LD_Z"];
        Assert.True(z.MoveTo(50).Ok);
        Assert.True(TestMachine.WaitUntil(() => z.IsMoveDone(50)));
        var r = t.M.Motion["TR_X"].MoveTo(100);
        Assert.False(r.Ok);
        Assert.Contains("LD_Z", r.Error);
    }

    [Fact]
    public void Step_mode_waits_for_trigger()
    {
        using var t = new TestMachine();
        t.HomeAndStart(OperationMode.Step);
        var loader = t.M.FindUnit("Loader")!;
        Assert.True(TestMachine.WaitUntil(() => loader.WaitingStepTrigger, 3000));
        int step = loader.Step;
        Thread.Sleep(300);
        Assert.Equal(step, loader.Step); // 트리거 전에는 진행하지 않음
        t.M.StepNext("Loader");
        Assert.True(TestMachine.WaitUntil(() => loader.Step != step, 3000));
    }
}
