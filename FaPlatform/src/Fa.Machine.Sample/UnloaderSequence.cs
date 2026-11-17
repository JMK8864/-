using Fa.Core.Sequence;

namespace Fa.Machine.Sample;

/// <summary>언로더: 반송 스테이지(Pick) 에서 자재를 집어 배출 위치(Place) 에 놓는다.</summary>
public sealed class UnloaderSequence : UnitSequence
{
    private readonly Handoff _fromTransfer;
    public UnloaderSequence(Handoff fromTransfer) : base("Unloader") { _fromTransfer = fromTransfer; }

    protected override IEnumerable<string> HomeAxes => new[] { "UL_Z", "UL_Y" };

    protected override void OnHomeStart() => DO("UL_VAC_ON").Off();

    protected override void AutoStep()
    {
        var y = Axis("UL_Y");
        var z = Axis("UL_Z");
        var vac = DO("UL_VAC_ON");

        switch (Step)
        {
            case 0:
                if (Move(z, Teach("UL_Z", "Up"))) Next(1, "Z Up 대기");
                break;
            case 1:
                if (WaitMove(z)) Next(10, "반송 준비 대기");
                break;

            // ── 반송 스테이지에서 집기 ──
            case 10:
                if (_fromTransfer.IsReceiverReady) _fromTransfer.BeginTransfer();
                if (!_fromTransfer.IsTransferring) break;
                if (Move(y, Teach("UL_Y", "Pick"))) Next(11, "Y Pick 이동");
                break;
            case 11:
                if (WaitMove(y) && Move(z, Teach("UL_Z", "Down"))) Next(12, "Z Down (Pick)");
                break;
            case 12:
                if (WaitMove(z) && SetOutput(vac, true)) Next(13, "진공 확인");
                break;
            case 13:
                if (Sensor(DI("UL_VAC_OK")) && Elapsed((int)RecipeValue("Unloader.VacuumDelayMs")))
                {
                    if (Move(z, Teach("UL_Z", "Up"))) Next(14, "Z Up (Pick)");
                }
                else CheckTimeout(2000, SampleMachineModule.UnloaderVacuumFail);
                break;
            case 14:
                if (!WaitMove(z)) break;
                _fromTransfer.Complete();
                if (Move(y, Teach("UL_Y", "Place"))) Next(20, "Y Place 이동");
                break;

            // ── 배출 위치에 놓기 ──
            case 20:
                if (WaitMove(y) && Move(z, Teach("UL_Z", "Down"))) Next(21, "Z Down (Place)");
                break;
            case 21:
                if (WaitMove(z) && SetOutput(vac, false)) Next(22, "진공 해제");
                break;
            case 22:
                if (Elapsed((int)RecipeValue("Unloader.VacuumDelayMs")) && Move(z, Teach("UL_Z", "Up"))) Next(23, "Z Up (Place)");
                break;
            case 23:
                if (!WaitMove(z)) break;
                CycleDone();
                Next(10, "반송 준비 대기");
                break;
        }
    }
}
