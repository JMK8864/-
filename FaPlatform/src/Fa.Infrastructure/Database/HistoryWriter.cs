using System.Threading.Channels;
using Fa.Core.Alarms;
using Fa.Core.Machine;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fa.Infrastructure.Database;

/// <summary>
/// 알람/사이클 이벤트를 큐에 받아 백그라운드에서 저장한다.
/// 시퀀스·감시 스레드는 큐에 넣기만 하므로 DB 지연이나 장애의 영향을 받지 않는다.
/// </summary>
public sealed class HistoryWriter : BackgroundService
{
    private readonly Channel<Func<Task>> _queue = Channel.CreateBounded<Func<Task>>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly IAlarmHistoryRepository _alarms;
    private readonly IProductionRepository _production;
    private readonly ILogger<HistoryWriter> _log;

    public HistoryWriter(AlarmManager alarmManager, MachineController machine,
        IAlarmHistoryRepository alarms, IProductionRepository production, ILogger<HistoryWriter> log)
    {
        _alarms = alarms; _production = production; _log = log;
        alarmManager.Raised += r => _queue.Writer.TryWrite(() => _alarms.InsertAsync(r));
        alarmManager.Cleared += r => _queue.Writer.TryWrite(() => _alarms.UpdateClearedAsync(r));
        machine.CycleCompleted += (unit, tact) =>
        {
            var rec = new CycleRecord(unit.Name, unit.CycleCount, tact, machine.Recipe.Current.Name, DateTime.Now);
            _queue.Writer.TryWrite(() => _production.InsertCycleAsync(rec));
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_alarms is PostgresRepository pg)
        {
            try { await pg.EnsureSchemaAsync(); }
            catch (Exception ex) { _log.LogError(ex, "DB 스키마 확인 실패"); }
        }

        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            // 실패 시 3 회 재시도 후 로그만 남긴다
            for (int attempt = 1; ; attempt++)
            {
                try { await job(); break; }
                catch (Exception ex) when (attempt < 3) { _log.LogWarning(ex, "이력 저장 재시도 {Attempt}", attempt); await Task.Delay(500, stoppingToken); }
                catch (Exception ex) { _log.LogError(ex, "이력 저장 실패"); break; }
            }
        }
    }
}
