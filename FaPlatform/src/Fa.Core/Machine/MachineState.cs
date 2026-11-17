namespace Fa.Core.Machine;

/// <summary>
/// 설비 상태.
/// Init → (원점복귀) → Ready → (Start) → Run ⇄ Stop
/// 중알람 → Alarm → (Reset) → Stop(원점 유지) 또는 Init(원점 상실)
/// 비상정지 → EStop → (해제 + Reset) → Init
/// </summary>
public enum MachineState { Init, Ready, Run, Stop, Alarm, EStop }

/// <summary>운전 모드. 원점복귀는 모드가 아니라 Init/Ready/Stop 상태에서 내리는 명령(IsHoming)이다.</summary>
public enum OperationMode
{
    /// <summary>수동: Start 불가, 화면에서 축/IO 직접 조작.</summary>
    Manual,
    /// <summary>자동 연속 운전.</summary>
    Auto,
    /// <summary>스텝 운전: 시퀀스 스텝이 바뀔 때마다 멈추고 'Next Step' 을 기다린다.</summary>
    Step,
    /// <summary>드라이런: 자재 없이 동작 (자재/진공 센서 확인 생략).</summary>
    DryRun
}

/// <summary>알람/정지 후 재시작 방식.</summary>
public enum ResumePolicy
{
    /// <summary>이어서: 멈춘 스텝(마지막 명령 스텝)부터 다시 실행.</summary>
    Continue,
    /// <summary>처음부터: 모든 유닛 Step 0, 유닛 간 인터페이스 초기화.</summary>
    Restart
}
