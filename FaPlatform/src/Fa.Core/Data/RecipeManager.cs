namespace Fa.Core.Data;

/// <summary>레시피 = 공정 값 + 티칭 위치. 제품마다 하나의 파일 (Data/Recipes/{Name}.json).</summary>
public sealed class Recipe
{
    public string Name { get; set; } = "Default";
    public DateTime Modified { get; set; } = DateTime.Now;
    public string ModifiedBy { get; set; } = "";
    /// <summary>공정 값. 예: "Loader.VacuumDelayMs" = 200</summary>
    public Dictionary<string, double> Values { get; set; } = new();
    /// <summary>티칭 위치. 키 "축이름.포인트". 예: "LD_Y.Pick" = 10.5</summary>
    public Dictionary<string, double> Teaching { get; set; } = new();
}

/// <summary>설비 모듈이 등록하는 레시피 값 정의 (화면 표시·기본값).</summary>
public sealed record RecipeValueDef(string Key, double Default, double Min, double Max, string Unit = "", string Description = "");

/// <summary>설비 모듈이 등록하는 티칭 포인트 정의.</summary>
public sealed record TeachingPointDef(string Axis, string Point, double Default, string Description = "")
{
    public string Key => $"{Axis}.{Point}";
}

public sealed class RecipeManager
{
    private const string Folder = "Recipes";
    private readonly IDataStore _store;
    private readonly object _lock = new();
    private Recipe _current = new();

    public RecipeManager(IDataStore store) { _store = store; }

    public List<RecipeValueDef> ValueDefs { get; } = new();
    public List<TeachingPointDef> TeachingDefs { get; } = new();
    public Recipe Current { get { lock (_lock) return _current; } }
    public event Action<Recipe>? Changed;

    public IReadOnlyList<string> List() => _store.List(Folder);

    /// <summary>레시피 선택. 없으면 기본값으로 새로 만든다.</summary>
    public void Select(string name)
    {
        var r = _store.Load<Recipe>($"{Folder}/{name}") ?? new Recipe { Name = name };
        r.Name = name;
        FillDefaults(r);
        lock (_lock) _current = r;
        Changed?.Invoke(r);
    }

    public void Save(Recipe recipe, string user)
    {
        recipe.Modified = DateTime.Now;
        recipe.ModifiedBy = user;
        _store.Save($"{Folder}/{recipe.Name}", recipe);
        if (recipe.Name == Current.Name) { lock (_lock) _current = recipe; Changed?.Invoke(recipe); }
    }

    public void SaveCurrent(string user) => Save(Current, user);

    public void Copy(string from, string to, string user)
    {
        var src = _store.Load<Recipe>($"{Folder}/{from}") ?? throw new InvalidOperationException($"레시피 없음: {from}");
        src.Name = to;
        Save(src, user);
    }

    public bool Delete(string name) => name != Current.Name && _store.Delete($"{Folder}/{name}");

    public double Value(string key) =>
        Current.Values.TryGetValue(key, out var v) ? v : ValueDefs.FirstOrDefault(d => d.Key == key)?.Default ?? 0;

    /// <summary>티칭 위치 읽기. 시퀀스에서 Teach("LD_Y", "Pick") 형태로 사용.</summary>
    public double Teach(string axis, string point)
    {
        var key = $"{axis}.{point}";
        if (Current.Teaching.TryGetValue(key, out var v)) return v;
        return TeachingDefs.FirstOrDefault(d => d.Key == key)?.Default
            ?? throw new KeyNotFoundException($"티칭 포인트 없음: {key}");
    }

    // 시퀀스 스레드가 읽는 중일 수 있으므로 사전을 직접 고치지 않고 사본을 만들어 교체한다 (copy-on-write)
    public void SetTeach(string axis, string point, double position)
    {
        lock (_lock) _current.Teaching = new Dictionary<string, double>(_current.Teaching) { [$"{axis}.{point}"] = position };
    }

    public void SetValue(string key, double value)
    {
        lock (_lock) _current.Values = new Dictionary<string, double>(_current.Values) { [key] = value };
    }

    private void FillDefaults(Recipe r)
    {
        foreach (var d in ValueDefs) r.Values.TryAdd(d.Key, d.Default);
        foreach (var d in TeachingDefs) r.Teaching.TryAdd(d.Key, d.Default);
    }
}
