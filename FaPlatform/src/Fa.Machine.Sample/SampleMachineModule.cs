using Fa.Core.Alarms;
using Fa.Core.Data;
using Fa.Core.Machine;
using Fa.Core.Sequence;

namespace Fa.Machine.Sample;

/// <summary>
/// 예제 설비: 로더 → 반송 → 언로더 (독립 시퀀스 3개).
///   Loader   : 카세트에서 자재를 집어 반송 스테이지에 놓는다   (LD_Y, LD_Z, 진공)
///   Transfer : 스테이지를 로딩 위치 → 언로딩 위치로 옮긴다       (TR_X, 클램프)
///   Unloader : 스테이지에서 자재를 집어 배출 위치에 놓는다       (UL_Y, UL_Z, 진공)
/// 유닛 간 인터록: Handoff 2개 (LD↔TR, TR↔UL) + 축 인터록 규칙.
/// </summary>
public sealed class SampleMachineModule : IEquipmentModule
{
    public string Name => "Sample Loader/Unloader";

    /// <summary>로더가 반송 스테이지에 놓기</summary>
    public Handoff LoadHandoff { get; } = new("LD→TR");
    /// <summary>언로더가 반송 스테이지에서 집기</summary>
    public Handoff UnloadHandoff { get; } = new("TR→UL");

    public void Configure(MachineController m)
    {
        RegisterAlarms(m.Alarms);
        RegisterRecipe(m.Recipe);

        m.AddUnit(new LoaderSequence(LoadHandoff));
        m.AddUnit(new TransferSequence(LoadHandoff, UnloadHandoff));
        m.AddUnit(new UnloaderSequence(UnloadHandoff));

        // 원점복귀 / 처음부터 재시작 시 유닛 간 인터페이스 초기화
        m.SequenceReset += () => { LoadHandoff.Reset(); UnloadHandoff.Reset(); };

        RegisterInterlocks(m);
    }

    private void RegisterInterlocks(MachineController m)
    {
        var recipe = m.Recipe;
        bool ZUp(string z) => m.Motion[z].Position <= recipe.Teach(z, "Up") + 1.0;
        // Y 가 반송 스테이지 위(영역 ±50mm)에 있는지
        bool OverStage(string y, string point) => Math.Abs(m.Motion[y].Position - recipe.Teach(y, point)) < 50;

        // 반송 X: 스테이지 위에 있는 로더/언로더 Z 가 Up 이고, 주고받는 중이 아닐 때만 이동
        m.Interlocks.AddMotion("TR_X", _ =>
        {
            if (OverStage("LD_Y", "Place") && !ZUp("LD_Z")) return "LD_Z 가 스테이지 위에서 Up 위치가 아닙니다";
            if (OverStage("UL_Y", "Pick") && !ZUp("UL_Z")) return "UL_Z 가 스테이지 위에서 Up 위치가 아닙니다";
            if (LoadHandoff.State != HandoffState.Idle) return $"{LoadHandoff.Name} 진행 중";
            if (UnloadHandoff.State != HandoffState.Idle) return $"{UnloadHandoff.Name} 진행 중";
            return null;
        });

        // Z 하강은 같은 유닛 Y 가 멈춰 있을 때만
        foreach (var (z, y) in new[] { ("LD_Z", "LD_Y"), ("UL_Z", "UL_Y") })
            m.Interlocks.AddMotion(z, target =>
                !double.IsNaN(target) && target > recipe.Teach(z, "Up") + 1.0 && m.Motion[y].IsMoving
                    ? $"{y} 이동 중에는 하강할 수 없습니다" : null);

        // Y 이동은 Z 가 Up 일 때만
        foreach (var (y, z) in new[] { ("LD_Y", "LD_Z"), ("UL_Y", "UL_Z") })
            m.Interlocks.AddMotion(y, _ => ZUp(z) ? null : $"{z} 가 Up 위치가 아닙니다");
    }

    private static void RegisterRecipe(RecipeManager r)
    {
        r.ValueDefs.AddRange(new RecipeValueDef[]
        {
            new("Loader.VacuumDelayMs", 150, 0, 5000, "ms", "로더 진공 ON 후 대기"),
            new("Unloader.VacuumDelayMs", 150, 0, 5000, "ms", "언로더 진공 ON 후 대기"),
            new("Transfer.ClampTimeoutMs", 3000, 100, 10000, "ms", "클램프 센서 대기 시간"),
        });
        r.TeachingDefs.AddRange(new TeachingPointDef[]
        {
            new("LD_Y", "Pick", 0, "카세트 픽업"),
            new("LD_Y", "Place", 300, "스테이지 놓기"),
            new("LD_Z", "Up", 0, "상승"),
            new("LD_Z", "Down", 50, "하강"),
            new("TR_X", "Load", 0, "로딩 위치"),
            new("TR_X", "Unload", 600, "언로딩 위치"),
            new("UL_Y", "Pick", 0, "스테이지 픽업"),
            new("UL_Y", "Place", 300, "배출 놓기"),
            new("UL_Z", "Up", 0, "상승"),
            new("UL_Z", "Down", 50, "하강"),
        });
    }

    public const int LoaderVacuumFail = 1001;
    public const int LoaderNoMaterial = 1002;
    public const int TransferClampFail = 1101;
    public const int UnloaderVacuumFail = 1201;

    private static void RegisterAlarms(AlarmManager a)
    {
        static AlarmDefinition D(int code, AlarmLevel lv, string ko, string en, string actKo, string actEn) => new()
        {
            Code = code, Level = lv,
            Text = new() { ["ko"] = ko, ["en"] = en },
            Action = new() { ["ko"] = actKo, ["en"] = actEn }
        };
        a.Register(D(LoaderVacuumFail, AlarmLevel.Heavy, "로더 진공 흡착 실패", "Loader vacuum failed",
            "자재 상태와 진공 패드, 진공 센서를 확인하세요.", "Check material, vacuum pad and sensor."));
        a.Register(D(LoaderNoMaterial, AlarmLevel.Warning, "로더 자재 없음", "Loader: no material",
            "카세트에 자재를 공급하세요.", "Supply material to the cassette."));
        a.Register(D(TransferClampFail, AlarmLevel.Heavy, "반송 클램프 동작 실패", "Transfer clamp failed",
            "클램프 실린더와 센서, 에어 압력을 확인하세요.", "Check clamp cylinder, sensors and air pressure."));
        a.Register(D(UnloaderVacuumFail, AlarmLevel.Heavy, "언로더 진공 흡착 실패", "Unloader vacuum failed",
            "자재 상태와 진공 패드, 진공 센서를 확인하세요.", "Check material, vacuum pad and sensor."));
    }
}
