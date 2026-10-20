using EventDataStore;
using ftms.app.Data;
using ftms.app.Services;
using ftms.shared.Model;
using ftms.shared.Model.Messages;
using ftms.shared.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading.Channels;


namespace ftms.app.Managers;

public class LogisticsSystemStateManager : BackgroundService
{
    private /*readonly*/ object _payloadProcessingResultLock { get; set; } = new();
    private /*readonly*/ PayloadProcessingResult _payloadProcessingResultAccum { get; set; } = new();
    private /*readonly*/ Dictionary<DateTimeOffset, PayloadProcessingResult> _payloadProcessingResults { get; set; } = new();

    private readonly ChannelReader<AssetItem> _channelReader;

    private readonly IEventDataStoreService<string> _eventDataStoreService;

    private readonly ProcessCountConfig _processCountConfig;

    //private readonly DeviceStatuses _processCountConfig;
    private readonly DeviceStatusManager _deviceStatusManager;

    protected readonly ILogger? _logger;
    private readonly IDbContextFactory<DataDbContext> _dbFactory;

    private readonly IMemoryCache _cache; // 1. IMemoryCache 필드 추가

    // 기존 _lockerProcessCount를 SemaphoreSlim으로 교체합니다.
    //private readonly SemaphoreSlim _processCountSemaphore = new SemaphoreSlim(1, 1);


    public LogisticsSystemStateManager(
        IDbContextFactory<DataDbContext> dbFactory,
        IEventDataStoreService<string> eventDataStoreService,
        DeviceStatusManager deviceStatusManager,
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        IMemoryCache memoryCache,
        AssetItemUpdateChannel channelWrapper)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));

        _logger = loggerFactory.CreateLogger(GetType());

        //_channelBus = bus.GetChannel(string.IsNullOrWhiteSpace(channelName) ? GetType().Name : channelName/*channelName*/);        //channelName = string.IsNullOrWhiteSpace(channelName) ? GetType().Name : channelName;
        _channelReader = channelWrapper.Instance.Reader;

        _deviceStatusManager = deviceStatusManager ?? throw new ArgumentNullException(nameof(deviceStatusManager));

        _processCountConfig = configuration.GetSection(Constants.ProcessCountConfig).Get<ProcessCountConfig>() ?? new ProcessCountConfig();

#if DEBUG
        _processCountConfig.AccumulationMinutes = 1;
#endif

        _eventDataStoreService = eventDataStoreService ?? throw new ArgumentNullException(nameof(eventDataStoreService));

        _cache = memoryCache;
    }


    public override async Task StartAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("Starting LogisticsSystemStateManager...");

        try
        {
            _logger?.LogInformation("Starting LogisticsSystemStateManager...");
            await _eventDataStoreService.EnsureTablesCreatedAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Fatal: Failed to ensure event store tables are created. Stopping service.");
            throw;
        }

        try
        {
            var restoreTasks = new[]
            {
                // 아래 2개는, 다음 블럭에서 settings 테이블에서 복원
                _eventDataStoreService.RestoreAsync<DockSortingParameterSet>(stoppingToken),
                _eventDataStoreService.RestoreAsync<DockSortingActiveParameter>(stoppingToken),
                // 위 2개는, 다음 블럭에서 settings 테이블에서 복원, 다음 블럭에서 settings 테이블에서 복원

                _eventDataStoreService.RestoreAsync<HeartBeatReportMessage>(stoppingToken),
                _eventDataStoreService.RestoreAsync<ProcessCountL>(stoppingToken),
                _eventDataStoreService.RestoreAsync<List<ProcessCountL>>(stoppingToken),
            };
            await Task.WhenAll(restoreTasks);
            _logger?.LogInformation("Restore completed for all known types.");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Restore failed for one or more types. State may be incomplete.");
        }

        try
        {
            var paramSets = await GetDockSortingCurrParam<DockSortingParameterSet>(category: Constants.DockSortParamSetKey, stoppingToken: stoppingToken);
            if (paramSets.Any())
            {
                foreach (var param in paramSets)
                {
                    await _eventDataStoreService.UpdateAsync<DockSortingParameterSet>(
                        key: param.ParamName,   //param.DockId
                        value: param,
                        version: param.UpdatedAt.ToString("o"),
                        saveToDb: false,
                        stoppingToken);
                }
                _logger?.LogInformation("Loaded {Count} dock sorting parameters set from database", paramSets.Count);
            }
            else { _logger?.LogWarning("No dock sorting parameters set found in database"); }
        }
        catch (Exception ex) { _logger?.LogError(ex, "Failed to load dock sorting parameters set from database"); }

        try
        {
            var sortingParams = await GetDockSortingCurrParam<DockSortingActiveParameter>(category: Constants.DockSortParamKey1, key: Constants.DockSortParamKey2, stoppingToken: stoppingToken);
            if (sortingParams.Any())
            {
                foreach (var param in sortingParams)
                {
                    await _eventDataStoreService.UpdateAsync<DockSortingActiveParameter>(
                        key: param.DockId,  //param.SetId,
                        value: param,
                        version: param.UpdatedAt.ToString("o"),
                        saveToDb: false,
                        stoppingToken);
                }
                _logger?.LogInformation("Loaded {Count} dock sorting parameters from database", sortingParams.Count);
            }
            else { _logger?.LogWarning("No dock sorting parameters found in database"); }
        }
        catch (Exception ex) { _logger?.LogError(ex, "Failed to load dock sorting parameters from database"); }

        await base.StartAsync(stoppingToken);
        _logger?.LogInformation("LogisticsSystemStateManager started successfully");
    }


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("LogisticsSystemStateManager background processing started");

        await foreach (var msg in _channelReader.ReadAllAsync(stoppingToken))
        //await foreach (var msg in _channelBus.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                // 메시지 타입에 따른 처리 로직
                await DispatchSettingChannelMessageAsync(msg, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger?.LogInformation("Message processing cancelled");
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error processing message of type {MessageType}", msg?.GetType().Name ?? "Unknown");
            }
        }

        _logger?.LogInformation("LogisticsSystemStateManager background processing stopped");
    }

    private async Task DispatchSettingChannelMessageAsync(object message, CancellationToken stoppingToken)
    {
        // 메시지 타입별 처리 로직을 여기에 구현
        switch (message)
        {
            case Setting setting:
            {
                if (string.Equals(setting.Category, Constants.DockSortParamSetKey, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(setting.JsonValue))
                        {
                            var item = Json.Deserialize<DockSortingParameterSet>(setting.JsonValue);
                            if (item != null)
                            {
                                await _eventDataStoreService.UpdateAsync<DockSortingParameterSet>(
                                    key: setting.Key,
                                    value: item,
                                    version: setting.UpdatedAt.ToString("o"),
                                    saveToDb: false,
                                    stoppingToken: stoppingToken);
                            }
                        }
                    }
                    catch (Exception)
                    {
                    }
                }

                if (string.Equals(setting.Category, Constants.DockSortParamKey1, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(setting.Key, Constants.DockSortParamKey2, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(setting.JsonValue))
                            {
                                var item = Json.Deserialize<DockSortingActiveParameter>(setting.JsonValue);
                                if (item != null)
                                {
                                    await  _eventDataStoreService.UpdateAsync<DockSortingActiveParameter>(
                                        key: setting.MachineId,
                                        value: item,
                                        version: setting.UpdatedAt.ToString("o"),
                                        saveToDb: false,
                                        stoppingToken: stoppingToken);
                                }
                            }
                        }
                        catch (Exception)
                        {
                        }

                    }
                }

                //_logger?.LogDebug("Received Models.Setting from {ClientId}", setting.JsonValue);
                _logger?.LogInformation("Received Models.Setting from {MessageType}", message?.GetType().Name ?? "null");
            }
            break;

            default:
            {
                _logger?.LogWarning("Unknown message type: {MessageType}", message?.GetType().Name ?? "null");
            }
            break;
        }

        //return Task.CompletedTask;
    }

    private async Task<List<T>> GetDockSortingCurrParam<T>(
        string category,
        string? key = null,
        string? machineId = null,
        CancellationToken stoppingToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(stoppingToken);

        var query = db.Set<Setting>()
            .Where(s => s.Category == category
                     && s.JsonValue != null);

        if (!string.IsNullOrEmpty(key)) query = query.Where(s => s.Key == key);
        if (!string.IsNullOrEmpty(machineId)) query = query.Where(s => s.MachineId == machineId);

        var jsonValues = await query
            .Select(s => s.JsonValue!)
            .ToListAsync(stoppingToken);

        var results = new List<T>();
        foreach (var json in jsonValues)
        {
            try
            {
                var obj = Json.Deserialize<T>(json);
                if (obj != null)
                {
                    results.Add(obj);
                }
            }
            catch (JsonException ex)
            {
                _logger?.LogWarning(ex, "Failed to deserialize JSON for category: {Category}, key: {Key}", category, key);
            }
        }

        return results;  // Task.FromResult 제거
    }

#if false
    public async Task UpdateDockSettingFromHostAsync(DockSortingCurrParam currParam, CancellationToken stoppingToken = default)
    {
        if (currParam == null)
        {
            _logger?.LogWarning("Attempted to update null DockSetting");
            return;
        }

        var dockId = currParam.DockId;
        if (string.IsNullOrWhiteSpace(dockId))
        {
            _logger?.LogWarning("DockSetting with null or empty DockId received");
            return;
        }

        try
        {
            await _eventDataStoreService.EnqueueAsync<DockSortingCurrParam>(
                dockId,
                currParam,
                currParam.UpdatedAt.ToString("o"),
                stoppingToken);

            _logger?.LogDebug("DockSetting updated for DockId: {DockId}", dockId);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to update DockSetting for DockId: {DockId}", dockId);
            throw;
        }
    }
#endif

    public async Task AccumProcessCountFromClientAsync(ProcessCountL processCount, CancellationToken stoppingToken = default)
    {
        if (processCount == null)
        {
            _logger?.LogWarning("Attempted to accum null ProcessCountL");
            return;
        }

        if (string.IsNullOrWhiteSpace(processCount.DockId))
        {
            _logger?.LogWarning("ProcessCountL with null or empty DockId received");
            return;
        }

        // SemaphoreSlim을 사용하여 비동기적으로 임계 영역에 진입합니다.
        //await _processCountSemaphore.WaitAsync(stoppingToken);
        
        try
        {
            // 현재 시간, Key: DockId
            var utcNow = DateTimeOffset.UtcNow;
            var dockId = processCount.DockId;

            // 값을 저장하고 있는 리스트와 Accum 을 찾아내고
            var dictlistProcCounts = await _eventDataStoreService.GetAsync<List<ProcessCountL>>(stoppingToken);
            var dictAccumProcCount = await _eventDataStoreService.GetAsync<ProcessCountL>(stoppingToken);

            var listProcCounts = (dictlistProcCounts?.TryGetValue(dockId, out var obj1) ?? false) ? obj1 as List<ProcessCountL> : null;
            var accumProcCount = (dictAccumProcCount?.TryGetValue(dockId, out var obj2) ?? false) ? obj2 as ProcessCountL : null;

            if (listProcCounts == null)
            {
                listProcCounts = new List<ProcessCountL>();
                //listProcCounts.Insert(0, processCount);
                //await _eventDataStoreService.UpdateAsync<List<ProcessCountL>>(dockId, listProcCounts, version: utcNow.ToString("o"), false, stoppingToken);
            }

            if (accumProcCount == null)
            {
                accumProcCount = new ProcessCountL();
                //await _eventDataStoreService.UpdateAsync<ProcessCountL>(dockId, accumProcCount, version: utcNow.ToString("o"), false, stoppingToken);
            }

            if (listProcCounts != null && accumProcCount != null)
            {
                // 누적한다.
                listProcCounts.Insert(0, processCount);

                var counts = listProcCounts?.Count ?? 0;
                for (int i = counts - 1; i >= 0; i--)
                {
                    var elapseMinutes = (utcNow - listProcCounts?[i].CreatedAt)?.TotalMinutes ?? 0;

                    if (elapseMinutes <= _processCountConfig.AccumulationMinutes)
                        break;

                    accumProcCount -= listProcCounts![i];
                    listProcCounts?.RemoveAt(i);
                }

                accumProcCount += processCount;
                accumProcCount.DockId = processCount.DockId;
                accumProcCount.VehicleNumber = processCount.VehicleNumber;
                accumProcCount.Route = "";
                accumProcCount.Pid = "";
                accumProcCount.CreatedAt = listProcCounts?.LastOrDefault()?.CreatedAt ?? DateTimeOffset.UtcNow;

                await _eventDataStoreService.UpdateAsync<ProcessCountL>(
                    dockId,
                    accumProcCount,
                    DateTimeOffset.UtcNow.ToString("o"),
                    saveToDb: false,
                    stoppingToken);

                await _eventDataStoreService.UpdateAsync<List<ProcessCountL>>(
                    dockId,
                    listProcCounts!,
                    version: utcNow.ToString("o"),
                    saveToDb: false,
                    stoppingToken);
            }
            _logger?.LogDebug("ProcessCount accum for DockId: {DockId}", processCount.DockId);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to accum ProcessCount for DockId: {DockId}", processCount.DockId);
            throw;
        }
        finally
        {
            // 임계 영역을 빠져나갈 때 세마포어를 해제합니다.
            //_processCountSemaphore.Release();
        }
    }


    /// <summary>
    /// 구성된 축적 시간(_processCountConfig.AccumulationMinutes)이 지난 프로세스 카운트 데이터를 정리합니다.
    /// 이 메서드는 주기적으로 호출되어 메모리에 저장된 오래된 데이터를 제거하고 누적 값을 업데이트합니다.
    /// </summary>
    /// <param name="stoppingToken">작업 취소 토큰</param>
    public async Task RemoveTimeoutProcessCountsAsync(CancellationToken stoppingToken = default)
    {
        var exceptionMessage = string.Empty;
        // SemaphoreSlim을 사용하여 비동기적으로 임계 영역에 진입합니다.
        //await _processCountSemaphore.WaitAsync(stoppingToken);
        try
        {
            var utcNow = DateTimeOffset.UtcNow;

            // 데이터 저장소에서 현재 모든 도크(Dock)의 '프로세스 카운트 목록'과 '누적 카운트'를 가져옵니다.
            var dictlistProcCounts = await _eventDataStoreService.GetAsync<List<ProcessCountL>>(stoppingToken);
            var dictAccumProcCounts = await _eventDataStoreService.GetAsync<ProcessCountL>(stoppingToken);

            // 정리할 데이터가 없으면 작업을 종료합니다.
            if (dictlistProcCounts == null || !dictlistProcCounts.Any())
            {
                _logger?.LogTrace("정리할 프로세스 카운트 목록이 없습니다.");
                return;
            }

            // 각 도크 ID에 대해 반복하며 만료된 데이터를 정리합니다.
            foreach (var kvp in dictlistProcCounts)
            {
                var dockId = kvp.Key;
                var listProcCounts = kvp.Value as List<ProcessCountL>;

                var accumProcCount = (dictAccumProcCounts?.TryGetValue(dockId, out var obj) ?? false) ? obj as ProcessCountL : null;
                if (listProcCounts == null || !listProcCounts.Any())
                {
                    if (accumProcCount != null && accumProcCount.Total != 0)
                    {
                        exceptionMessage = $"DockId: {dockId}의 프로세스 카운트 목록이 비어 있지만 누적 카운트가 존재합니다. 누적 카운트를 초기화합니다.";

                        await _eventDataStoreService.UpdateAsync<ProcessCountL>(
                            dockId,
                            accumProcCount,
                            utcNow.ToString("o"),
                            saveToDb: false,
                            stoppingToken);

                        exceptionMessage = $"DockId: {dockId}의 누적 프로세스 카운트를 초기화했습니다.";
                    }
                    continue;
                }

                bool listModified = false;
                // 리스트의 뒤에서부터 순회하며 만료된 항목을 안전하게 제거합니다.
                for (int i = listProcCounts.Count - 1; i >= 0; i--)
                {
                    exceptionMessage = $"DockId: {dockId}의 프로세스 카운트 목록을 검사하는 중입니다. 인덱스: {i}";

                    var item = listProcCounts[i];
                    var elapseMinutes = (utcNow - item.CreatedAt).TotalMinutes;
                    if (elapseMinutes > _processCountConfig.AccumulationMinutes)
                    {
                        if (accumProcCount != null)
                        {
                            accumProcCount -= item;
                        }

                        exceptionMessage = $"DockId: {dockId}의 프로세스 카운트 목록에서 인덱스 {i}의 항목이 만료되어 제거됩니다.";

                        listProcCounts.RemoveAt(i);
                        listModified = true;

                        exceptionMessage = $"DockId: {dockId}의 프로세스 카운트 목록에서 인덱스 {i}의 항목을 제거했습니다.";
                    }
                }

                if (listModified)
                {
                    if (accumProcCount != null)
                    {
                        exceptionMessage = $"DockId: {dockId}의 누적 프로세스 카운트를 업데이트합니다.";

                        accumProcCount.CreatedAt = listProcCounts.LastOrDefault()?.CreatedAt ?? utcNow;

                        exceptionMessage = $"DockId: {dockId}의 누적 프로세스 카운트를 업데이트했습니다.";

                        await _eventDataStoreService.UpdateAsync(
                            dockId,
                            accumProcCount,
                            utcNow.ToString("o"),
                            saveToDb: false,
                            stoppingToken);

                        exceptionMessage = $"DockId: {dockId}의 누적 프로세스 카운트를 저장했습니다.";
                    }

                    exceptionMessage = $"DockId: {dockId}의 프로세스 카운트 목록을 업데이트합니다.";

                    await _eventDataStoreService.UpdateAsync(
                        dockId,
                        listProcCounts,
                        version: utcNow.ToString("o"),
                        saveToDb: false,
                        stoppingToken);

                    exceptionMessage = $"DockId: {dockId}의 프로세스 카운트 목록을 저장했습니다.";

                    // log 정보 기록
                    {
                        // 1. 캐시를 이용한 조건부 로깅 로직 시작
                        var logCounterKey = $"log-counter:{dockId}";
                        var lastLogTimeKey = $"last-log-time:{dockId}";

                        // 카운터 값을 가져오거나 0으로 초기화
                        var counter = _cache.GetOrCreate(logCounterKey, entry => 0);
                        counter++;

                        // 마지막 로그 시간을 가져옴
                        _cache.TryGetValue(lastLogTimeKey, out DateTimeOffset? lastLogTime);

                        // 로그 출력 조건 확인: (카운터가 100 이상) 또는 (마지막 로그 후 1분 경과)
                        if (counter >= 100 || !lastLogTime.HasValue || (utcNow - lastLogTime.Value).TotalMinutes >= 1)
                        {
                            _logger?.LogInformation("DockId: {DockId}의 만료된 프로세스 카운트를 정리했습니다. (지난 기간 동안 {Count}회 발생)", dockId, counter);
                            //_logger?.LogInformation("DockId: {DockId}의 만료된 프로세스 카운트를 정리했습니다. (지난 1분간 {Count}회 발생)", dockId, counter);
                            //_logger?.LogInformation("DockId: {DockId}의 만료된 프로세스 카운트를 정리했습니다.", dockId);

                            // 로그를 출력했으므로 카운터와 타임스탬프를 리셋
                            _cache.Set(logCounterKey, 0);
                            _cache.Set(lastLogTimeKey, utcNow, TimeSpan.FromHours(1)); // 타임스탬프는 1시간 동안 유지
                        }
                        else
                        {
                            // 로그를 출력하지 않으면 카운터만 업데이트
                            _cache.Set(logCounterKey, counter);
                        }
                        // 조건부 로깅 로직 끝
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "만료된 프로세스 카운트를 정리하는 중 오류가 발생: {ExceptionMessage}", exceptionMessage);
        }
        finally
        {
            // 임계 영역을 빠져나갈 때 세마포어를 해제합니다.
            //_processCountSemaphore.Release();
        }
    }


    public async Task UpdateProcessCountFromClientAsync(ProcessCountL processCount, CancellationToken stoppingToken = default)
    {
        if (processCount == null)
        {
            _logger?.LogWarning("Attempted to update null ProcessCountL");
            return;
        }

        if (string.IsNullOrWhiteSpace(processCount.DockId))
        {
            _logger?.LogWarning("ProcessCountL with null or empty DockId received");
            return;
        }

        try
        {
            await _eventDataStoreService.UpdateAsync<ProcessCountL>(
                processCount.DockId,
                processCount,
                DateTimeOffset.UtcNow.ToString("o"),
                saveToDb: true,
                stoppingToken);

            _logger?.LogDebug("ProcessCount updated for DockId: {DockId}", processCount.DockId);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to update ProcessCount for DockId: {DockId}", processCount.DockId);
            throw;
        }
    }

    public async Task<IEnumerable<ProcessCountL>> GetProcessCountsAsync(IEnumerable<string>? dockIds = null, CancellationToken stoppingToken = default)
    {
        var procCounts = await _eventDataStoreService.GetAsync<ProcessCountL>(stoppingToken);

        if (procCounts == null || procCounts.Count == 0)
        {
            _logger?.LogDebug("No ProcessCounts found in data store");
            return Enumerable.Empty<ProcessCountL>();
        }

        // dockIds가 없으면 전체 반환
        if (dockIds == null || !dockIds.Any())
        {
            return procCounts.Values.OfType<ProcessCountL>();
        }

        // Contains 성능을 올리기 위해 HashSet 사용
        var dockIdSet = new HashSet<string>(dockIds);

        // 도크ID가 입력 배열에 포함된 것만 반환
        return procCounts
            .Where(kv => dockIdSet.Contains(kv.Key))
            .Select(kv => kv.Value)
            .OfType<ProcessCountL>()
            .OrderBy(v => v.DockId);
    }

    /// <summary>
    /// [도크 끊김 판정 - 원인 2] TCP 연결이 끊긴 도크의 마지막 heartbeat 에 "끊김" 표시를 합니다.
    /// 상태 값은 지우지 않고 남겨 두어(마지막 상태 확인용), 화면에서 끊김으로 표시하게 합니다.
    /// </summary>
    public async Task MarkDockDisconnectedAsync(string dockId, CancellationToken stoppingToken = default)
    {
        if (string.IsNullOrWhiteSpace(dockId)) return;

        try
        {
            var heartbeats = await _eventDataStoreService.GetAsync<HeartBeatReportMessage>(stoppingToken);
            if (heartbeats != null && heartbeats.TryGetValue(dockId, out var obj) && obj is HeartBeatReportMessage hb)
            {
                hb.Connected = false;
                hb.DisconnectedAt = DateTimeOffset.UtcNow;
                _logger?.LogInformation("Dock {DockId} marked as disconnected", dockId);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to mark dock {DockId} as disconnected", dockId);
        }
    }

    public async Task<IEnumerable<HeartBeatReportMessage>> GetAllStatusAsync(IEnumerable<string>? dockIds = null, CancellationToken stoppingToken = default)
    {
        var heartbeats = await _eventDataStoreService.GetAsync<HeartBeatReportMessage>(stoppingToken);

        if (heartbeats == null || heartbeats.Count == 0)
        {
            _logger?.LogDebug("No HeartBeatMessages found in data store");
            return Enumerable.Empty<HeartBeatReportMessage>();
        }

        // dockIds가 없으면 전체 반환
        if (dockIds == null || !dockIds.Any())
        {
            return heartbeats.Values.OfType<HeartBeatReportMessage>();
        }

        // Contains 성능을 올리기 위해 HashSet 사용
        var dockIdSet = new HashSet<string>(dockIds);

        // 도크ID가 입력 배열에 포함된 것만 반환
        return heartbeats
            .Where(kv => dockIdSet.Contains(kv.Key))
            .Select(kv => kv.Value)
            .OfType<HeartBeatReportMessage>();
    }


    // 기존에 CS\u0001\u0000\u0004\u0000... 형태로 들어오던 것을 16진수 쉼표 구분 문자열로 변경 CS0001,0040,... 헥사 코드로 처리합니다.
    // 입력 문자열이 Conveyor 장비에서 온 경우, "CS"로 시작하면 처음 2바이트를 건너뛰고 2바이트씩 읽어서 숫자로 변환합니다. (예: \x01\xA0 -> 0xA001 -> 0001)
    // 그 외의 경우, 입력 문자열을 2글자씩 잘라서 Hex 숫자로 변환합니다. (예: "02ff03" -> 0002,00ff,0003)
    // ----------------------------------------------------------------
    // 입력 타입           								예시 출력
    // ----------------------------------------------------------------
    // Conveyor + CS	CS\x01\xA0\x02\x03\x04\x20		0001,0002,0004
    // Conveyor         \x01\xA0\x02\x03				0001,0002
    // Hex 문자열       02ff03							0002,00ff,0003
    // 잘못된 Hex       02zz03							0002,0003
    // 홀수 길이        02ff0    	                	0002,00ff
    // ----------------------------------------------------------------
    private (bool, string) SanitizeStatusString_v2(string? deviceType, string? status)
    {
        // status가 null 또는 빈 문자열이면 처리 실패로 간주
        if (string.IsNullOrEmpty(status))
        {
            return (false, string.Empty);
        }

        try
        {
            // 결과로 반환할 숫자 리스트 (각 항목은 0~255 범위의 값)
            var numbers = new List<int>();

            // deviceType이 Conveyor 인 경우 (Raw Binary 문자열 처리 모드)
            if (string.Equals(deviceType, DeviceTypeEnum.Conveyor.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                // 기본 시작 인덱스는 0
                int startIndex = 0;

                // status가 "CS"로 시작하면 헤더로 간주하고 2바이트 건너뜀
                if (status.StartsWith("CS"))
                {
                    startIndex = 2;
                }

                // 2바이트씩 순차적으로 처리
                for (int i = startIndex; i + 1 < status.Length; i += 2)
                {
                    // 첫 번째 바이트 (하위 바이트 역할)
                    char c1 = status[i];

                    // 두 번째 바이트 (상위 바이트 역할)
                    char c2 = status[i + 1];

                    // 2바이트를 조합하여 16비트 정수 생성 (c2가 상위 바이트)
                    int number = (c2 << 8) | c1;

                    // 현재는 하위 1바이트만 사용. 향후 16비트 확장 시 아래 주석 해제
                    //numbers.Add(number);

                    // 하위 1바이트만 추출하여 저장
                    numbers.Add(number & 0x00ff);
                }
            }
            else
            {
                // Conveyor가 아닌 경우: status는 Hex 문자열로 간주

                // 2글자씩 잘라서 1바이트 단위로 처리
                for (int i = 0; i + 1 < status.Length; i += 2)
                {
                    // Hex 2자리 추출, 문자열을 정수로 변환 시도
                    var hexPair = status.Substring(i, 2);
                    if (int.TryParse(hexPair, System.Globalization.NumberStyles.HexNumber, null, out int number))
                    {
                        // 하위 1바이트만 저장 (안전 마스킹)
                        numbers.Add(number & 0x00ff);
                    }
                    // 변환 실패 시 해당 바이트는 무시 (의도된 skip 처리)
                }
            }

            // 모든 숫자를 4자리 소문자 hex 문자열로 변환 후 콤마(,)로 연결하여 반환
            return (true, string.Join(",", numbers.Select(n => n.ToString("x4"))));
        }
        catch (Exception ex)
        {
            // 예외 발생 시 로그 기록
            _logger?.LogError(ex, "Error sanitizing status string: {Status}", status);

            // 실패로 반환
            return (false, string.Empty);
        }
    }



    private static (bool, string) SanitizeStatusString_v1(string? status)
    {
        if (string.IsNullOrEmpty(status))
        {
            return (false, string.Empty);
        }

        bool flag = false;
        var sb = new System.Text.StringBuilder(status.Length);
        foreach (char c in status)
        {
            int charValue = c;
            // 문자의 정수 값이 0x0에서 0xF 사이인 경우 (즉, \0 부터 \f 까지)
            if (charValue >= 0 && charValue <= 15)
            {
                // 정수 값을 16진수 소문자 문자열로 변환하여 추가 (예: 10 -> "a")
                sb.Append(charValue.ToString("x"));
                flag = true;
            }
            else
            {
                // 그 외의 문자는 그대로 추가
                sb.Append(c);
            }
        }
        return (flag, sb.ToString());
    }

    /// <summary>
    /// 하트비트 메시지에 대한 응답을 생성합니다.
    /// </summary>
    /// <param name="message">하트비트 메시지</param>
    /// <param name="stoppingToken">작업 취소 토큰</param>
    /// <returns>클라이언트 닉네임(dockId, nickname, clientId), 응답 메시지</returns>
    public async Task<(string, string)> GetResponseHeartbeatMessageAsync(string message, CancellationToken stoppingToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            _logger?.LogWarning("Empty or null heartbeat message received");
            return (string.Empty, string.Empty);
        }

        try
        {
            // 수신한 하트 비트 데이터를 변수에 저장해 놓습니다.
            var hbm = Json.Deserialize<HeartBeatReportMessage>(message);
            if (hbm == null || string.IsNullOrWhiteSpace(hbm.ClientId))
            {
                _logger?.LogWarning("Invalid heartbeat message: unable to deserialize or missing ClientId");
                return (string.Empty, string.Empty);
            }

            var clientId = hbm.ClientId;
            var messageId = hbm.MessageId;

            // [도크 끊김 판정 - 원인 1] 서버 기준 수신 시각과 연결 상태를 기록
            //   도크 PC 가 보낸 LastSeenAt 은 그 PC 의 시계라 서버와 어긋날 수 있으므로 별도로 남깁니다.
            hbm.ReceivedAt = DateTimeOffset.UtcNow;
            hbm.Connected = true;
            hbm.DisconnectedAt = null;
            
            foreach (var status in hbm?.DeviceStatuses ?? Enumerable.Empty<HeartBeatReportMessage.DeviceStatus>())
            {
                try
                {
                    // 수정된 함수가 여기서 호출됩니다.
#if false
                    var (isSanitized, sanitizedStatus) = SanitizeStatusString_v1(status.Status);
#else
                    // status.Id는 장비 유형을 나타내며, status.Status는 원래의 상태 문자열입니다.
                    // 리턴된 문자열은 4자리 hex로 구성된 콤마 구분 문자열입니다. 예: "0001,00a0,0002,0000,0008,..."
                    var (isSanitized, sanitizedStatus) = SanitizeStatusString_v2(deviceType: status?.Id, status?.Status);
#endif
                    // isSanitized가 true인 경우에만 status.Status를 sanitizedStatus로 업데이트합니다.
                    if (isSanitized)
                    {
                        status!.Status = sanitizedStatus;
                        _logger?.LogTrace/*.LogWarning*/("Sanitized DeviceStatus - Id: {Id}, Original Status: {OriginalStatus}, Sanitized Status: {SanitizedStatus}, LastSeenAt: {LastSeenAt}",
                            status.Id, status.Status, sanitizedStatus, status.LastSeenAt);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error sanitizing DeviceStatus - Id: {Id}, Status: {Status}, LastSeenAt: {LastSeenAt}",
                        status.Id, status.Status, status.LastSeenAt);  
                }
                //else
                //{
                //    _logger?.LogDebug("HeartBeat DeviceStatus - Id: {Id}, Status: {Status}, LastSeenAt: {LastSeenAt}",
                //       status.Id, status.Status, status.LastSeenAt);
                //}   
            }

            // 하트비트에 포함된 상태 데이터를 업데이트 합니다.
            try
            {
                await _eventDataStoreService.UpdateAsync<HeartBeatReportMessage>(
                    key: clientId,
                    value: hbm!,
                    version: DateTimeOffset.UtcNow.ToString("o"),
                    saveToDb: true,
                    stoppingToken);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to update HeartBeatReportMessage for ClientId: {ClientId}", clientId);
            }

            // 캐시 변수에 저장되어 있는 최신 데이터를 갖고 옵니다.
            var currParams = await _eventDataStoreService.GetAsync<DockSortingActiveParameter>();

            // 여기서 설정된 파라메터이름을 찾아서 해당 데이터를 리턴 합니다.
            var currParam = currParams.TryGetValue(clientId, out var cp1) ? (cp1 as DockSortingActiveParameter) : null;
            if (currParam == null)
            {
                currParam = currParams.TryGetValue("Default", out cp1) ? (cp1 as DockSortingActiveParameter) : null;
                if (currParam != null)
                    currParam.DockId = clientId;  
            }

            var response = new HeartBeatResponse
            {
                MessageId = messageId,
                Type = TcpMessage.Type.Heartbeat,
                Timestamp = DateTimeOffset.UtcNow,

                // 현재 설정된 파라메터를 그대로 전달합니다. (clientId에 해당하는 것이 없으면 Default로 전달)
                SetParameter = currParam,
            };

            _logger?.LogDebug("HeartBeat response generated for ClientId: {ClientId}", clientId);

            var nickname/*dockId*/ = string.IsNullOrEmpty(clientId) ? string.Empty : (clientId ?? "null");
            var responseMessage = Json.Serialize(response);

            return (nickname/*dockId*/, responseMessage);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to process heartbeat message");
            return (string.Empty, string.Empty);
        }
    }

    public async Task<string> GetResponseDockSortingParamMessageAsync(string message, CancellationToken stoppingToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            _logger?.LogWarning("Empty or null heartbeat message received");
            return string.Empty;
        }

        try
        {
            // 수신한 하트 비트 데이터를 변수에 저장해 놓습니다.
            var request = Json.Deserialize<DockSortingParamRequestMessage>(message);
            if (request == null || string.IsNullOrWhiteSpace(request.ClientId))
            {
                _logger?.LogWarning("Invalid DockSortingParamRequestMessage message: unable to deserialize or missing ClientId");
                return string.Empty;
            }

            //await _eventDataStoreService.EnqueueAsync<HeartBeatReportMessage>(hbm.ClientId, hbm, null, stoppingToken);

            // 변수에 저장되어 있는 최신 데이터를 갖고 옵니다.
            var currParams = await _eventDataStoreService.GetAsync<DockSortingActiveParameter>();
            var currParam = currParams.TryGetValue(request.ClientId ?? string.Empty, out var cp) ? cp as DockSortingActiveParameter : null;
            var paramName = currParam?.ParamName ?? "Default"/*string.Empty*/;

            // 위에서 찾아낸 paramName 를 기준으로 해당 값을 찾아서 전달합니다.
            var paramSets = await _eventDataStoreService.GetAsync<DockSortingParameterSet>();
            var response = new DockSortingParamResponseMessage
            {
                MessageId = request.MessageId,
                Type = TcpMessage.Type.DockSortingParam1,
                Timestamp = DateTimeOffset.UtcNow,
                //...
                ParameterSet = paramSets.TryGetValue(paramName ?? string.Empty, out var ps) ? ps as DockSortingParameterSet : null
            };
            var responseMessage = Json.Serialize(response);

            _logger?.LogDebug("DockSortingParamRequestMessage response generated for ClientId: {ClientId}", request.ClientId);

            return responseMessage;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to process DockSortingParamRequestMessage message");
            return string.Empty;
        }
    }

    public override async Task StopAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("Stopping LogisticsSystemStateManager...");
        await base.StopAsync(stoppingToken);
        _logger?.LogInformation("LogisticsSystemStateManager stopped");
    }

    public void AddCollectedPayloadProcessingResult(PayloadProcessingResult payloadProcessingResult, double discardMinutes = 60)
    {
        if (payloadProcessingResult == null)
            return;

        lock (_payloadProcessingResultLock)
        {

            try
            {
                var utcNow = DateTimeOffset.UtcNow;

                _payloadProcessingResultAccum += payloadProcessingResult;
                _payloadProcessingResults[utcNow] = payloadProcessingResult;
            }
            catch (Exception ex)
            {
                _payloadProcessingResultAccum = new PayloadProcessingResult();
                _payloadProcessingResults = new Dictionary<DateTimeOffset, PayloadProcessingResult>();

                _logger?.LogError(ex, "Error in AddCollectedPayloadProcessingResult");
            }
        }

        ClearCollectedPayloadProcessingResult(discardMinutes);
    }

    public bool ClearCollectedPayloadProcessingResult(double discardMinutes = 60)
    {
        bool result = false;
        lock (_payloadProcessingResultLock)
        {
            try
            {
                var utcNow = DateTimeOffset.UtcNow;

                var expiredKeys = _payloadProcessingResults
                    .Where(x => (utcNow - x.Key).TotalMinutes > discardMinutes)
                    .Select(x => x.Key)
                    .ToList();   // 스냅샷

                foreach (var key in expiredKeys)
                {
                    _payloadProcessingResultAccum -= _payloadProcessingResults[key];
                    _payloadProcessingResults.Remove(key);
                }

                result = expiredKeys.Count > 0;
            }
            catch (Exception ex)
            {
                _payloadProcessingResultAccum = new PayloadProcessingResult();
                _payloadProcessingResults = new Dictionary<DateTimeOffset, PayloadProcessingResult>();

                _logger?.LogError(ex, "Error in AddCollectedPayloadProcessingResult");
            }
        }
        return result;
    }

    public PayloadProcessingResultDto GetCollectedPayloadProcessingResultAccum()
    {
        var item = _payloadProcessingResultAccum ?? new PayloadProcessingResult();

        return new PayloadProcessingResultDto
        {
            LoadedCount = item.LoadedCount,
            DedupedCount = item.DedupedCount,
            InsertedCount = item.InsertedCount,
            UpdatedCount = item.UpdatedCount,

            //RawdataLoadedCount = item.RawdataLoadedCount,
            //RawdataInsertedCount = item.RawdataInsertedCount,
            //RawdataUpdatedCount = item.RawdataUpdatedCount,

            ErrorLineCount = item.ErrorLineCount,

            Last_N_Minutes = 60,
        };
    }

    //internal void IncrementProcessedPayloadsCount(PayloadProcessingResult payloadProcessingResult)
    //{
    //    if (payloadProcessingResult?.Success == true)
    //        _payloadProcessingResult += payloadProcessingResult;
    //}
    //public string PayloadProcessingAccum => _payloadProcessingResult?.ToSimpleInfomation ?? "No Data";
}
