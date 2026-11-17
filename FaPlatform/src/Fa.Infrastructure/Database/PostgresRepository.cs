using Fa.Core.Alarms;
using Npgsql;

namespace Fa.Infrastructure.Database;

/// <summary>
/// PostgreSQL 저장소: 알람 이력 + 생산(사이클) 이력.
/// 테이블이 없으면 시작 시 만든다 (db/schema.sql 과 같은 내용).
/// </summary>
public sealed class PostgresRepository : IAlarmHistoryRepository, IProductionRepository
{
    private readonly NpgsqlDataSource _ds;

    public PostgresRepository(string connectionString)
    {
        _ds = NpgsqlDataSource.Create(connectionString);
    }

    public const string Schema = """
        CREATE TABLE IF NOT EXISTS fa_alarm_history (
            id           uuid PRIMARY KEY,
            code         int          NOT NULL,
            level        varchar(16)  NOT NULL,
            source       varchar(64)  NOT NULL,
            message      text         NOT NULL,
            detail       text,
            recipe       varchar(64),
            occurred_at  timestamp    NOT NULL,
            cleared_at   timestamp,
            cleared_by   varchar(64)
        );
        CREATE INDEX IF NOT EXISTS ix_fa_alarm_history_occurred ON fa_alarm_history (occurred_at DESC);

        CREATE TABLE IF NOT EXISTS fa_cycle_history (
            id           bigserial PRIMARY KEY,
            unit         varchar(64)  NOT NULL,
            cycle_no     int          NOT NULL,
            tact_sec     double precision NOT NULL,
            recipe       varchar(64),
            completed_at timestamp    NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_fa_cycle_history_completed ON fa_cycle_history (completed_at DESC);
        """;

    public async Task EnsureSchemaAsync()
    {
        await using var cmd = _ds.CreateCommand(Schema);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task InsertAsync(AlarmRecord r)
    {
        await using var cmd = _ds.CreateCommand("""
            INSERT INTO fa_alarm_history (id, code, level, source, message, detail, recipe, occurred_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8) ON CONFLICT (id) DO NOTHING
            """);
        cmd.Parameters.AddWithValue(r.Id);
        cmd.Parameters.AddWithValue(r.Code);
        cmd.Parameters.AddWithValue(r.Level.ToString());
        cmd.Parameters.AddWithValue(r.Source);
        cmd.Parameters.AddWithValue(r.Message);
        cmd.Parameters.AddWithValue((object?)r.Detail ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)r.Recipe ?? DBNull.Value);
        cmd.Parameters.AddWithValue(r.OccurredAt);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateClearedAsync(AlarmRecord r)
    {
        await using var cmd = _ds.CreateCommand("UPDATE fa_alarm_history SET cleared_at = $2, cleared_by = $3 WHERE id = $1");
        cmd.Parameters.AddWithValue(r.Id);
        cmd.Parameters.AddWithValue((object?)r.ClearedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)r.ClearedBy ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<AlarmRecord>> QueryAsync(DateTime from, DateTime to, int? code = null, int limit = 1000)
    {
        await using var cmd = _ds.CreateCommand("""
            SELECT id, code, level, source, message, detail, recipe, occurred_at, cleared_at, cleared_by
            FROM fa_alarm_history
            WHERE occurred_at BETWEEN $1 AND $2 AND ($3::int IS NULL OR code = $3)
            ORDER BY occurred_at DESC LIMIT $4
            """);
        cmd.Parameters.AddWithValue(from);
        cmd.Parameters.AddWithValue(to);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)code ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer });
        cmd.Parameters.AddWithValue(limit);

        var list = new List<AlarmRecord>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            list.Add(new AlarmRecord
            {
                Id = rd.GetGuid(0), Code = rd.GetInt32(1), Level = Enum.Parse<AlarmLevel>(rd.GetString(2)),
                Source = rd.GetString(3), Message = rd.GetString(4),
                Detail = rd.IsDBNull(5) ? null : rd.GetString(5), Recipe = rd.IsDBNull(6) ? null : rd.GetString(6),
                OccurredAt = rd.GetDateTime(7),
                ClearedAt = rd.IsDBNull(8) ? null : rd.GetDateTime(8),
                ClearedBy = rd.IsDBNull(9) ? null : rd.GetString(9)
            });
        }
        return list;
    }

    public async Task InsertCycleAsync(CycleRecord c)
    {
        await using var cmd = _ds.CreateCommand(
            "INSERT INTO fa_cycle_history (unit, cycle_no, tact_sec, recipe, completed_at) VALUES ($1, $2, $3, $4, $5)");
        cmd.Parameters.AddWithValue(c.Unit);
        cmd.Parameters.AddWithValue(c.CycleNo);
        cmd.Parameters.AddWithValue(c.TactSec);
        cmd.Parameters.AddWithValue((object?)c.Recipe ?? DBNull.Value);
        cmd.Parameters.AddWithValue(c.CompletedAt);
        await cmd.ExecuteNonQueryAsync();
    }
}
