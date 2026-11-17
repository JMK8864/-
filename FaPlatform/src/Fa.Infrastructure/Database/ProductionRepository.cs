using System.Text;

namespace Fa.Infrastructure.Database;

public sealed record CycleRecord(string Unit, int CycleNo, double TactSec, string? Recipe, DateTime CompletedAt);

/// <summary>생산(사이클) 이력 저장소.</summary>
public interface IProductionRepository
{
    Task InsertCycleAsync(CycleRecord cycle);
}

/// <summary>DB 미사용 시: Production/yyyy-MM-dd.csv</summary>
public sealed class CsvProductionRepository : IProductionRepository
{
    private readonly string _dir;
    private readonly SemaphoreSlim _lock = new(1, 1);
    public CsvProductionRepository(string dir) { _dir = dir; Directory.CreateDirectory(dir); }

    public async Task InsertCycleAsync(CycleRecord c)
    {
        var line = $"{c.CompletedAt:yyyy-MM-dd HH:mm:ss.fff},{c.Unit},{c.CycleNo},{c.TactSec:F3},{c.Recipe}{Environment.NewLine}";
        await _lock.WaitAsync();
        try { await File.AppendAllTextAsync(Path.Combine(_dir, $"{c.CompletedAt:yyyy-MM-dd}.csv"), line, Encoding.UTF8); }
        finally { _lock.Release(); }
    }
}
