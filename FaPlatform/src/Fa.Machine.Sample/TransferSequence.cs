using Fa.Core.Sequence;

namespace Fa.Machine.Sample;

/// <summary>반송: 로딩 위치에서 자재를 받아 클램프 → 언로딩 위치로 이동 → 언클램프 후 언로더에 넘긴다.</summary>
public sealed class TransferSequence : UnitSequence
{
    private readonly Handoff _fromLoader;
    private readonly Handoff _toUnloader;

    public TransferSequence(Handoff fromLoader, Handoff toUnloader) : base("Transfer")
    {
        _fromLoader = fromLoader; _toUnloader = toUnloader;
    }

    protected override IEnumerable<string> HomeAxes => new[] { "TR_X" };

    // 로더/언로더 원점(Z 상승)이 끝나야 X 원점 시작 (충돌 방지)
    protected override bool CanStartHomeGroup(int groupIndex)
        => IsUnitHomed("Loader") && IsUnitHomed("Unloader");

    protected override void OnHomeStart() => DO("TR_CLAMP").Off();

    protected override void AutoStep()
    {
        var x = Axis("TR_X");
        var clamp = DO("TR_CLAMP");
        int clampTimeout = (int)RecipeValue("Transfer.ClampTimeoutMs");

        switch (Step)
        {
            case 0: // 언클램프 후 로딩 위치로
                if (SetOutput(clamp, false)) Next(1, "언클램프 확인");
                break;
            case 1:
                if (DI("TR_CLAMP_OFF").IsOn)
                {
                    if (Move(x, Teach("TR_X", "Load"))) Next(2, "X Load 이동");
                }
                else CheckTimeout(clampTimeout, SampleMachineModule.TransferClampFail);
                break;
            case 2:
                if (WaitMove(x)) { _fromLoader.SetReady(); Next(10, "로더 놓기 대기"); }
                break;

            // ── 로더에게서 받기 ──
            case 10:
                if (!_fromLoader.IsSenderDone) break;
                _fromLoader.Acknowledge(); // 명령과 분리: 재시작 시 이 스텝으로 돌아오지 않도록
                Next(11, "클램프");
                break;
            case 11:
                if (SetOutput(clamp, true)) Next(12, "클램프 확인");
                break;
            case 12:
                if (DI("TR_CLAMP_ON").IsOn)
                {
                    if (Move(x, Teach("TR_X", "Unload"))) Next(13, "X Unload 이동");
                }
                else CheckTimeout(clampTimeout, SampleMachineModule.TransferClampFail);
                break;
            case 13:
                if (WaitMove(x) && SetOutput(clamp, false)) Next(14, "언클램프 확인");
                break;
            case 14:
                if (DI("TR_CLAMP_OFF").IsOn) { _toUnloader.SetReady(); Next(20, "언로더 집기 대기"); }
                else CheckTimeout(clampTimeout, SampleMachineModule.TransferClampFail);
                break;

            // ── 언로더에게 넘기기 ──
            case 20:
                if (!_toUnloader.IsSenderDone) break;
                _toUnloader.Acknowledge();
                CycleDone();
                Next(0, "로딩 위치 복귀");
                break;
        }
    }
}
