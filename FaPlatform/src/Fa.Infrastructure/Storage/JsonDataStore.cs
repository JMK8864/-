using System.Text.Json;
using System.Text.Json.Serialization;
using Fa.Core.Data;

namespace Fa.Infrastructure.Storage;

/// <summary>
/// JSON 파일 저장소. 저장 시 임시 파일에 먼저 쓰고 교체해서, 저장 중 전원이 꺼져도 파일이 깨지지 않게 한다.
/// 이전 파일은 .bak 으로 남긴다.
/// </summary>
public sealed class JsonDataStore : IDataStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 한글 그대로 저장
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _lock = new();

    public JsonDataStore(string rootDirectory)
    {
        Root = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    private string FullPath(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar) + ".json");

    public T? Load<T>(string path) where T : class
    {
        var file = FullPath(path);
        lock (_lock)
        {
            if (!File.Exists(file)) return null;
            using var fs = File.OpenRead(file);
            return JsonSerializer.Deserialize<T>(fs, Options);
        }
    }

    public void Save<T>(string path, T data) where T : class
    {
        var file = FullPath(path);
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, Options));
            if (File.Exists(file)) File.Replace(tmp, file, file + ".bak");
            else File.Move(tmp, file);
        }
    }

    public bool Exists(string path) => File.Exists(FullPath(path));

    public bool Delete(string path)
    {
        var file = FullPath(path);
        lock (_lock)
        {
            if (!File.Exists(file)) return false;
            File.Delete(file);
            return true;
        }
    }

    public IReadOnlyList<string> List(string folder)
    {
        var dir = Path.Combine(Root, folder);
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.GetFiles(dir, "*.json").Select(Path.GetFileNameWithoutExtension).OrderBy(n => n).ToList()!;
    }
}
