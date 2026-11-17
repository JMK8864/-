namespace Fa.Core.Alarms;

/// <summary>플랫폼 공통 알람 코드 (1~999). 설비별 알람은 1000 이상 사용.</summary>
public static class PlatformAlarms
{
    public const int EStop = 1;
    public const int DoorOpen = 2;
    public const int ServoAlarm = 100;
    public const int PositiveLimit = 101;
    public const int NegativeLimit = 102;
    public const int HomeFailed = 103;
    public const int HomeTimeout = 104;
    public const int MoveTimeout = 105;
    public const int MoveRejected = 106;
    public const int SequenceException = 200;
    public const int StepTimeout = 201;
    public const int HostDisconnected = 300;
}
