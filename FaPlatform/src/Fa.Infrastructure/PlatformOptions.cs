namespace Fa.Infrastructure;

/// <summary>appsettings.json 의 "Fa" 섹션. 하드웨어 구성은 machine.json, 여기는 실행 환경 설정.</summary>
public sealed class PlatformOptions
{
    /// <summary>데이터 폴더 (Config, Recipes, Lang, Logs, AlarmHistory). 상대 경로면 실행 폴더 기준.</summary>
    public string DataDirectory { get; set; } = "Data";
    public string DefaultRecipe { get; set; } = "Default";
    public FileLogOptions FileLog { get; set; } = new();
    public DatabaseOptions Database { get; set; } = new();
    public HostCommOptions HostComm { get; set; } = new();
}

public sealed class FileLogOptions
{
    public string Directory { get; set; } = "Logs";
    public int RetentionDays { get; set; } = 90;
    /// <summary>카테고리 접두어 → 파일 이름. 맞는 것이 없으면 "System".</summary>
    public Dictionary<string, string> Routes { get; set; } = new()
    {
        ["Seq."] = "Sequence",
        ["Fa.Core.Alarms"] = "Alarm",
        ["Fa.Infrastructure.Comm"] = "Comm",
    };
}

public sealed class DatabaseOptions
{
    /// <summary>false 면 알람 이력을 텍스트(CSV) 파일에 저장.</summary>
    public bool Enabled { get; set; }
    public string ConnectionString { get; set; } = "Host=127.0.0.1;Port=5432;Database=fa;Username=fa;Password=fa";
}

public enum HostCommMode { Server, Client }

public sealed class HostCommOptions
{
    public bool Enabled { get; set; } = true;
    public HostCommMode Mode { get; set; } = HostCommMode.Server;
    /// <summary>Server: 바인드 주소, Client: 접속할 상위 서버 주소.</summary>
    public string Address { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 7000;
    public int ReconnectIntervalMs { get; set; } = 3000;
    public string Encoding { get; set; } = "utf-8";
}
