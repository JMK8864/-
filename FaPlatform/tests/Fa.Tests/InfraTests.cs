using System.Text;
using Fa.Core.Alarms;
using Fa.Core.Hardware;
using Fa.Core.Io;
using Fa.Infrastructure.Comm;
using Fa.Infrastructure.Database;

namespace Fa.Tests;

public class InfraTests
{
    [Fact]
    public void Framer_handles_split_joined_and_garbage()
    {
        var f = new StxEtxFramer();
        var all = new List<string>();
        void Push(string s) => all.AddRange(f.Push(Encoding.ASCII.GetBytes(s)).Select(b => Encoding.ASCII.GetString(b)));

        Push("xx\u0002STA");
        Push("TUS\u0003\u0002START\u0003\u0002ST");
        Push("OP\u0003junk");
        Assert.Equal(new[] { "STATUS", "START", "STOP" }, all);
        Assert.Equal(new byte[] { 2, (byte)'A', 3 }, StxEtxFramer.Encode("A"u8));
    }

    [Fact]
    public void Analog_scaling_round_trips()
    {
        var c = new AnalogPointConfig { RawMin = 0, RawMax = 32767, EngMin = 0, EngMax = 10 };
        Assert.Equal(5.0, AnalogScale.ToEng(c, AnalogScale.ToRaw(c, 5.0)), 3);
        Assert.Equal(32767, AnalogScale.ToRaw(c, 20)); // 범위 밖은 제한
    }

    [Fact]
    public async Task Csv_alarm_history_merges_set_and_clear()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fa-alarm-" + Guid.NewGuid().ToString("N"));
        var repo = new CsvAlarmHistoryRepository(dir);
        var rec = new AlarmRecord { Code = 1001, Level = AlarmLevel.Heavy, Source = "Loader", Message = "진공, 실패", Detail = "Step 13" };
        await repo.InsertAsync(rec);
        rec.ClearedAt = rec.OccurredAt.AddSeconds(12);
        rec.ClearedBy = "admin";
        await repo.UpdateClearedAsync(rec);

        var list = await repo.QueryAsync(DateTime.Today, DateTime.Today.AddDays(1));
        var r = Assert.Single(list);
        Assert.Equal("진공, 실패", r.Message);
        Assert.Equal(12, r.Duration!.Value.TotalSeconds, 1);
        Assert.Equal("admin", r.ClearedBy);
        Directory.Delete(dir, true);
    }
}
