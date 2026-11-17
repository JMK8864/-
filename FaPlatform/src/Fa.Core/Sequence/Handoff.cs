namespace Fa.Core.Sequence;

public enum HandoffState { Idle, ReceiverReady, Transferring, SenderDone }

/// <summary>
/// 유닛 간 자재 주고받기 인터페이스.
///   Station(자재를 놓을/집을 자리를 가진 유닛, 예: 반송 스테이지)
///   Robot  (그 자리로 들어와서 놓거나 집는 유닛, 예: 로더 / 언로더 피커)
///
///   Station: SetReady()       → ReceiverReady  (자리 준비 완료, 이후 Station 은 움직이면 안 됨)
///   Robot  : BeginTransfer()  → Transferring   (Robot 이 들어가는 중)
///   Robot  : Complete()       → SenderDone     (놓기/집기 끝, Robot 이 빠져나옴)
///   Station: Acknowledge()    → Idle           (Station 다시 움직일 수 있음)
/// Idle 이 아니면 Station 축 이동을 막는 인터록을 거는 것이 기본 사용법이다.
/// </summary>
public sealed class Handoff
{
    private volatile HandoffState _state = HandoffState.Idle;

    public Handoff(string name) { Name = name; }
    public string Name { get; }
    public HandoffState State => _state;

    public bool IsReceiverReady => _state == HandoffState.ReceiverReady;
    public bool IsTransferring => _state == HandoffState.Transferring;
    public bool IsSenderDone => _state == HandoffState.SenderDone;

    public void SetReady() { if (_state == HandoffState.Idle) _state = HandoffState.ReceiverReady; }
    public void BeginTransfer() { if (_state == HandoffState.ReceiverReady) _state = HandoffState.Transferring; }
    public void Complete() { if (_state == HandoffState.Transferring) _state = HandoffState.SenderDone; }
    public void Acknowledge() { if (_state == HandoffState.SenderDone) _state = HandoffState.Idle; }
    /// <summary>'처음부터' 재시작 시 초기화.</summary>
    public void Reset() => _state = HandoffState.Idle;
}
