namespace Fa.Core.Machine;

/// <summary>
/// 설비별 코드의 진입점. 새 설비를 만들 때 이 인터페이스를 구현하는 프로젝트를 하나 추가한다.
/// Configure 에서 유닛 시퀀스, 인터록, 알람 정의, 레시피/티칭 정의, 파라미터를 등록한다.
/// </summary>
public interface IEquipmentModule
{
    string Name { get; }
    void Configure(MachineController machine);
}
