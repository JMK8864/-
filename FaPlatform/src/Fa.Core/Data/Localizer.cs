namespace Fa.Core.Data;

/// <summary>
/// 다국어 문구 (Data/Lang/{언어}.json : { "키": "문구" }).
/// 키가 없으면 키 자체를 보여주므로 누락된 번역을 화면에서 바로 알 수 있다.
/// </summary>
public sealed class Localizer
{
    private readonly IDataStore _store;
    private Dictionary<string, string> _texts = new();

    public Localizer(IDataStore store, string language = "ko")
    {
        _store = store;
        SetLanguage(language);
    }

    public string Language { get; private set; } = "ko";
    public event Action? Changed;

    public string this[string key] => _texts.TryGetValue(key, out var s) ? s : key;

    public IReadOnlyList<string> Available => _store.List("Lang");

    public void SetLanguage(string language)
    {
        _texts = _store.Load<Dictionary<string, string>>($"Lang/{language}") ?? new();
        Language = language;
        Changed?.Invoke();
    }
}
