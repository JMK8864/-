using System.Globalization;
using System.Text;
using Fa.Core.Alarms;

namespace Fa.Infrastructure.Database;

/// <summary>
/// DB 를 쓰지 않을 때의 알람 이력 저장소. 하루 한 파일 (AlarmHistory/yyyy-MM-dd.csv).
/// 발생 시 SET 줄, 해제 시 CLEAR 줄을 추가하고 조회할 때 Id 로 합친다.
/// </summary>
public sealed class CsvAlarmHistoryRepository : IAlarmHistoryRepository
{
    private readonly string _dir;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public CsvAlarmHistoryRepository(string directory)
    {
        _dir = directory;
        Directory.CreateDirectory(_dir);
    }

    private string FileOf(DateTime day) => Path.Combine(_dir, $"{day:yyyy-MM-dd}.csv");

    public Task InsertAsync(AlarmRecord r) => Append(r.OccurredAt,
        $"SET,{r.Id},{r.OccurredAt:O},{r.Code},{r.Level},{Esc(r.Source)},{Esc(r.Message)},{Esc(r.Detail)},{Esc(r.Recipe)}");

    // 해제 줄은 발생 일자 파일에 기록 (자정을 넘겨도 한 파일에서 찾도록)
    public Task UpdateClearedAsync(AlarmRecord r) => Append(r.OccurredAt,
        $"CLEAR,{r.Id},{r.ClearedAt:O},{Esc(r.ClearedBy)}");

    private async Task Append(DateTime day, string line)
    {
        await _lock.WaitAsync();
        try { await File.AppendAllTextAsync(FileOf(day), line + Environment.NewLine, Encoding.UTF8); }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<AlarmRecord>> QueryAsync(DateTime from, DateTime to, int? code = null, int limit = 1000)
    {
        var map = new Dictionary<Guid, AlarmRecord>();
        await _lock.WaitAsync();
        try
        {
            for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
            {
                var f = FileOf(d);
                if (!File.Exists(f)) continue;
                foreach (var line in await File.ReadAllLinesAsync(f))
                {
                    var c = Split(line);
                    if (c.Count < 4 || !Guid.TryParse(c[1], out var id)) continue;
                    if (c[0] == "SET" && c.Count >= 9)
                        map[id] = new AlarmRecord
                        {
                            Id = id, OccurredAt = DateTime.Parse(c[2], null, DateTimeStyles.RoundtripKind),
                            Code = int.Parse(c[3]), Level = Enum.Parse<AlarmLevel>(c[4]),
                            Source = c[5], Message = c[6], Detail = Null(c[7]), Recipe = Null(c[8])
                        };
                    else if (c[0] == "CLEAR" && map.TryGetValue(id, out var rec))
                    {
                        rec.ClearedAt = DateTime.Parse(c[2], null, DateTimeStyles.RoundtripKind);
                        rec.ClearedBy = Null(c[3]);
                    }
                }
            }
        }
        finally { _lock.Release(); }

        return map.Values
            .Where(r => r.OccurredAt >= from && r.OccurredAt <= to && (code == null || r.Code == code))
            .OrderByDescending(r => r.OccurredAt).Take(limit).ToList();
    }

    private static string? Null(string s) => s.Length == 0 ? null : s;
    private static string Esc(string? s) => s == null ? "" : s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

    private static List<string> Split(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (q)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') q = false;
                else sb.Append(ch);
            }
            else if (ch == '"') q = true;
            else if (ch == ',') { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        result.Add(sb.ToString());
        return result;
    }
}
