using Fa.Core.Sequence;

namespace Fa.Machine.Sample;

/// <summary>로더: 카세트(Pick) 에서 자재를 집어 반송 스테이지(Place) 에 놓는다.</summary>
public sealed class LoaderSequence : UnitSequence
{
    private readonly Handoff _toTransfer;
    public LoaderSequence(Handoff toTransfer) : base("Loader") { _toTransfer = toTransfer; }

    protected override IEnumerable<string> HomeAxes => new[] { "LD_Z", "LD_Y" }; // Home.Order: Z=0, Y=1

    protected override void OnHomeStart() => DO("LD_VAC_ON").Off();

    protected override void AutoStep()
    {
        var y = Axis("LD_Y");
        var z = Axis("LD_Z");
        var vac = DO("LD_VAC_ON");

        switch (Step)
        {
            case 0: // 초기: Z 상승
                if (Move(z, Teach("LD_Z", "Up"))) Next(1, "Z Up 대기");
                break;
            case 1:
                if (WaitMove(z)) Next(10, "자재 대기");
                break;

            // ── 픽업 ──
            case 10:
                if (!Sensor(DI("LD_MATERIAL"))) break; // 자재가 올 때까지 대기
                if (Move(y, Teach("LD_Y", "Pick"))) Next(11, "Y Pick 이동");
                break;
            case 11:
                if (WaitMove(y) && Move(z, Teach("LD_Z", "Down"))) Next(12, "Z Down (Pick)");
                break;
            case 12:
                if (WaitMove(z) && SetOutput(vac, true)) Next(13, "진공 확인");
                break;
            case 13:
                if (Sensor(DI("LD_VAC_OK")) && Elapsed((int)RecipeValue("Loader.VacuumDelayMs")))
                {
                    if (Move(z, Teach("LD_Z", "Up"))) Next(14, "Z Up (Pick)");
                }
                else CheckTimeout(2000, SampleMachineModule.LoaderVacuumFail);
                break;
            case 14:
                if (WaitMove(z) && Move(y, Teach("LD_Y", "Place"))) Next(20, "Y Place 이동");
                break;

            // ── 반송 스테이지에 놓기 ──
            case 20:
                if (WaitMove(y)) Next(21, "반송 준비 대기");
                break;
            case 21:
                // '이어서' 재시작으로 다시 들어온 경우 이미 Transferring 일 수 있다
                if (_toTransfer.IsReceiverReady) _toTransfer.BeginTransfer();
                if (!_toTransfer.IsTransferring) break;
                if (Move(z, Teach("LD_Z", "Down"))) Next(22, "Z Down (Place)");
                break;
            case 22:
                if (WaitMove(z) && SetOutput(vac, false)) Next(23, "진공 해제");
                break;
            case 23:
                if (Elapsed((int)RecipeValue("Loader.VacuumDelayMs")) && Move(z, Teach("LD_Z", "Up"))) Next(24, "Z Up (Place)");
                break;
            case 24:
                if (!WaitMove(z)) break;
                _toTransfer.Complete();
                CycleDone();
                Next(10, "자재 대기");
                break;
        }
    }
}
