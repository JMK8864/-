namespace Fa.Core.Data;

/// <summary>
/// 설정/데이터 파일 저장소. 경로는 데이터 폴더 기준 상대 경로이고 확장자(.json)는 붙이지 않는다.
/// 예: "Config/machine", "Recipes/ProductA". 구현: JsonDataStore (Infrastructure).
/// </summary>
public interface IDataStore
{
    T? Load<T>(string path) where T : class;
    void Save<T>(string path, T data) where T : class;
    bool Exists(string path);
    bool Delete(string path);
    /// <summary>폴더 안의 항목 이름 목록 (확장자 제외).</summary>
    IReadOnlyList<string> List(string folder);
}
