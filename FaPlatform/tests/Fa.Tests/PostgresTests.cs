using Fa.Core.Alarms;
using Fa.Infrastructure.Database;

namespace Fa.Tests;

/// <summary>
/// PostgreSQL 저장소 시험. 환경변수 FA_TEST_PG 에 연결 문자열이 있을 때만 실제로 DB 를 사용한다.
/// 예) FA_TEST_PG="Host=127.0.0.1;Database=fa;Username=fa;Password=fa" dotnet test
/// </summary>
public class PostgresTests
{
    [Fact]
    public async Task Alarm_and_cycle_history_round_trip_when_db_configured()
    {
        var cs = Environment.GetEnvironmentVariable("FA_TEST_PG");
        if (string.IsNullOrEmpty(cs)) return; // DB 없는 환경에서는 건너뜀

        var repo = new PostgresRepository(cs);
        await repo.EnsureSchemaAsync();

        var rec = new AlarmRecord { Code = 9999, Level = AlarmLevel.Warning, Source = "TEST", Message = "시험 알람", Recipe = "R1" };
        await repo.InsertAsync(rec);
        rec.ClearedAt = rec.OccurredAt.AddSeconds(5);
        rec.ClearedBy = "tester";
        await repo.UpdateClearedAsync(rec);

        var list = await repo.QueryAsync(DateTime.Now.AddMinutes(-1), DateTime.Now.AddMinutes(1), 9999);
        var r = Assert.Single(list, x => x.Id == rec.Id);
        Assert.Equal("tester", r.ClearedBy);
        Assert.Equal(5, r.Duration!.Value.TotalSeconds, 1);
        Assert.NotEmpty(await repo.QueryAsync(DateTime.Now.AddMinutes(-1), DateTime.Now.AddMinutes(1))); // code 필터 없이

        await repo.InsertCycleAsync(new CycleRecord("TEST", 1, 2.5, "R1", DateTime.Now));
    }
}
