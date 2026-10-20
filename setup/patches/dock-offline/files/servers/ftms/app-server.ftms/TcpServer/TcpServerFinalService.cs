using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using SocketConnectionManager;
using TcpServer;
using ftms.app.Managers;

namespace ftms.app.TcpMessageHandlers;

public class TcpServerConfig : BaseConfig
{
    public string IpAddress { get; set; } = "0.0.0.0";
    public List<int> Ports { get; set; } = new List<int>();
    public int TimeoutSeconds { get; set; } = 30;
}

public class TcpServerFinalService : TcpServerServiceBase<TcpServerConfig>
{
    private const string LogHeader = "[TCP-SRV]"; // 고정 구분자 추가

    private readonly ITcpServerConnectionManager _connectionManager;
    private readonly ConcurrentDictionary<string, Task> _clientTasks = new ConcurrentDictionary<string, Task>();
    private readonly ConcurrentDictionary<TcpClient, DateTimeOffset> _lastReceiveTimes = new ConcurrentDictionary<TcpClient, DateTimeOffset>();
    private readonly TimeSpan _clientTimeout;

    // 정렬된 핸들러 목록을 저장할 필드
    private readonly IReadOnlyList<ITcpMessageHandler> _sortedCommandHandlers;

    private readonly ChannelWriter<string> _channelWriter;

    // [도크 끊김 판정 - 원인 2] 연결 종료 시 도크 상태에 "끊김" 표시
    private readonly LogisticsSystemStateManager _stateManager;

    public TcpServerFinalService(
        ILogger<TcpServerFinalService> logger,
        IConfiguration configuration,
        ITcpServerConnectionManager connectionManager,
        IEnumerable<ITcpMessageHandler> commandHandlers,
        DataCollectorChannel channelWrapper,
        LogisticsSystemStateManager stateManager)
        : base(
            logger,
            configuration.GetSection(Constants.TcpServerConfig).Get<TcpServerConfig>() ?? new TcpServerConfig())
    {
        _connectionManager = connectionManager;
        _stateManager = stateManager;
        _clientTimeout = TimeSpan.FromSeconds(Config.TimeoutSeconds);

        _channelWriter = channelWrapper.Instance.Writer;

        // ===== 핵심: 주입받은 핸들러를 Priority 순으로 정렬 =====
        // OrderBy를 사용해 우선순위(낮은 숫자 우선)로 정렬하고,
        // ToList()를 호출하여 결과를 리스트로 저장합니다.
        // 이렇게 하면 매번 메시지를 처리할 때마다 정렬할 필요가 없습니다.
        _sortedCommandHandlers = commandHandlers.OrderBy(h => h.Priority).ToList();

#if DEBUG
        // 정렬 결과를 로그로 남겨서 의도대로 되었는지 확인할 수 있습니다.
        var handlerOrder = string.Join(" -> ", _sortedCommandHandlers.Select(h => h.GetType().Name));
        // ✅ [수정] 생성자 로그 헤더 추가
        Logger.LogInformation("{Header} Command handler priority order: {Order}", LogHeader, handlerOrder);
#endif
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await base.ExecuteAsync(stoppingToken);

        if (!IPAddress.TryParse(Config.IpAddress, out var ipAddress) || !Config.Ports.Any())
        {
            // ✅ [수정] 설정 오류 로그 헤더 추가
            Logger.LogCritical("{Header} TCP Server configuration is invalid. Server will not start.", LogHeader);
            return;
        }

        var runningTasks = Config.Ports.Select(port => ListenOnPortAsync(ipAddress, port, stoppingToken)).ToList();
        if (Config.TimeoutSeconds > 0)
        {
            // ✅ [수정] 타임아웃 활성화 로그 헤더 추가
            Logger.LogInformation("{Header} Client timeout is enabled ({Timeout}s).", LogHeader, Config.TimeoutSeconds);
            runningTasks.Add(CheckClientTimeoutsAsync(stoppingToken));
        }

        await Task.WhenAll(runningTasks);
        await Task.WhenAll(_clientTasks.Values);
    }

    private async Task ListenOnPortAsync(IPAddress ipAddress, int port, CancellationToken stoppingToken)
    {
        var listener = new TcpListener(ipAddress, port);

        // _listener.LocalEndpoint는 "0.0.0.0:8080"과 같은 문자열을 반환합니다.
        var listenerID = listener?.LocalEndpoint?.ToString() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(listenerID))
        {
            _connectionManager.AddListener(listenerID);
        }

        try
        {
            listener?.Start();

            Logger.LogInformation("({Protocol}) Listening on {Endpoint}", FramingMode, listener?.LocalEndpoint);

            while (!stoppingToken.IsCancellationRequested && listener != null)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                var clientId = client?.Client?.RemoteEndPoint?.ToString() ?? string.Empty;

                if (client != null && !string.IsNullOrWhiteSpace(clientId))
                {
                    _connectionManager.AddClient(clientId, client);

                    if (Config.TimeoutSeconds > 0)
                        _lastReceiveTimes[client] = DateTimeOffset.UtcNow;

                    var clientTask = HandleClientSessionAsync(clientId, client, stoppingToken);
                    _clientTasks.TryAdd(clientId, clientTask);

                    _ = clientTask.ContinueWith(t =>
                    {
                        // [도크 끊김 판정 - 원인 2] 접속 목록에서 지우기 전에 이 연결의 DockId(닉네임)를 확보
                        var dockId = GetDockIdOfClient(clientId);

                        _clientTasks.TryRemove(clientId, out _);
                        if (Config.TimeoutSeconds > 0) _lastReceiveTimes.TryRemove(client, out _);
                        _connectionManager.RemoveClient(clientId);

                        // 같은 도크가 다른 연결로 이미 다시 접속해 있으면 끊김으로 표시하지 않음
                        if (!string.IsNullOrWhiteSpace(dockId) && !IsDockConnected(dockId))
                        {
                            Logger.LogWarning("{Header} Dock {DockId} disconnected ({ClientId})", LogHeader, dockId, clientId);
                            _ = _stateManager.MarkDockDisconnectedAsync(dockId, clientId, CancellationToken.None);
                        }

                        // ✅ [수정] 태스크 완료 로그 헤더 추가
                        Logger.LogInformation("{Header} Client task completed for {ClientId}", LogHeader, clientId);
                    }, TaskScheduler.Default);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // ✅ [수정] 리스너 취소 로그 헤더 추가
            Logger.LogInformation("{Header} Listener on port {Port} is stopping due to cancellation.", LogHeader, port);
        }
        catch (Exception ex)
        {
            // ✅ [수정] 리스너 에러 로그 헤더 추가
            Logger.LogError(ex, "{Header} Listener error on port {Port}", LogHeader, port);
        }
        finally
        {
            listener?.Stop();
            // ✅ [수정] 리스너 정지 로그 헤더 추가
            Logger.LogInformation("{Header} Stopped listening on port {Port}", LogHeader, port);
        }
    }

    // [도크 끊김 판정 - 원인 2] 연결 ID(IP:Port) → DockId (heartbeat 처리 시 JsonMessagedHandler 가 닉네임으로 등록)
    private string? GetDockIdOfClient(string clientId)
    {
        try
        {
            return _connectionManager.GetConnectedClientEndpoints()
                .Where(kv => kv.Key == clientId)
                .Select(kv => kv.Value.nickname)
                .FirstOrDefault();
        }
        catch (Exception) { return null; }
    }

    // [도크 끊김 판정 - 원인 2] 해당 DockId 로 접속 중인 다른 연결이 있는지
    private bool IsDockConnected(string dockId)
    {
        try
        {
            return _connectionManager.GetConnectedClientEndpoints()
                .Any(kv => string.Equals(kv.Value.nickname, dockId, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception) { return false; }
    }

    private async Task CheckClientTimeoutsAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(5000, stoppingToken); } catch (OperationCanceledException) { break; }

            try
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var (client, lastReceiveTime) in _lastReceiveTimes) // ToList()를 사용하지 않고 직접 순회해도 ConcurrentDictionary는 안전
                {
                    try
                    {
                        if (!client.Connected) { _lastReceiveTimes.TryRemove(client, out _); continue; }

                        if (now - lastReceiveTime > _clientTimeout)
                        {
                            Logger.LogWarning("{Header} Client {ClientId} heartbeat timed out.", LogHeader, client.Client.RemoteEndPoint);
                            client.Dispose(); // Dispose는 연결된 스트림에 예외를 발생시켜 ProcessClientAsync 루프를 중단시킵니다.
                        }
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }
    }

    // 기존: ProcessClientAsync
    private async Task HandleClientSessionAsync(string clientId, TcpClient client, CancellationToken stoppingToken)
    {
        var remoteEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "Unknown";

        // --- 로그 개선 1: 세션 시작 ---
        Logger.LogInformation("{Header} Session started. Endpoint: {RemoteEndpoint}, ID: {ClientId}", LogHeader, remoteEndpoint, clientId);

        //var clientId = client.Client.RemoteEndPoint!.ToString();
        //Logger.LogInformation("Processing client {ClientId}", clientId);

        using (client)
        {
            try
            {
                // 데이터 수신되면 타임아웃 변수 리셋해주는 델리게이트
                Action? onResetTimeout = (Config.TimeoutSeconds <= 0) ? null : () => _lastReceiveTimes[client] = DateTimeOffset.UtcNow;

                var pipe = new Pipe();
                var writing = FillPipeAsync(clientId, client.GetStream(), pipe.Writer, onResetTimeout, stoppingToken);
                var reading = ReadPipeAsync(clientId, pipe.Reader, (message) => ProcessMessageAsync(clientId, message, client, stoppingToken), stoppingToken);

                await Task.WhenAll(reading, writing);
            }
            catch (Exception ex)
            {
                // ✅ [수정] 세션 에러 로그 헤더 추가
                Logger.LogError(ex, "{Header} Error processing client {ClientId}", LogHeader, clientId);
            }
            finally
            {
                // ✅ [수정] 세션 종료 로그 헤더 추가
                Logger.LogInformation("{Header} Client session ended. Endpoint: {RemoteEndpoint}, ID: {ClientId}", LogHeader, remoteEndpoint, clientId);
            }
        }
    }

    private async Task ProcessMessageAsync(string clientId, ReadOnlySequence<byte> message, TcpClient client, CancellationToken stoppingToken)
    {
        //var clientId = client?.Client?.RemoteEndPoint?.ToString() ?? "(Unknown)";

        // (이전 제안대로) 메모리 효율적인 문자열 변환 로직 적용
        var receivedMessage = Encoding.UTF8.GetString(message);

        if (_connectionManager?.GetLogLevel(clientId) <= LogLevel.Information)
        {
            Logger.LogInformation("{Header} Received from {ClientId} ({Length} bytes): {Message}", LogHeader, clientId, message.Length, receivedMessage);
        }

        // 수정된 ProcessTcpCommand 호출
        var responseMessage = await ProcessTcpCommandAsync(clientId, receivedMessage, stoppingToken);
        if (!string.IsNullOrEmpty(responseMessage))
        {
            try
            {
                if (_connectionManager != null) await _connectionManager.SendToClientAsync(clientId, responseMessage);

                if (_connectionManager?.GetLogLevel(clientId) <= LogLevel.Information)
                {
                    // ✅ [수정] 전송 로그 헤더 추가
                    Logger.LogInformation("{Header} Sent to {ClientId} ({Length} bytes): {Message}", LogHeader, clientId, responseMessage.Length, responseMessage);
                }
            }
            catch (Exception ex)
            {
                // ✅ [수정] 전송 에러 로그 헤더 추가
                Logger.LogError(ex, "{Header} Error sending response to {ClientId}", LogHeader, clientId);
            }
        }
    }


    private async Task<string?> ProcessTcpCommandAsync(string clientId, string receivedMessage, CancellationToken stoppingToken = default)
    {
        var command = string.Empty;
        var responseMessage = string.Empty;

        // 이제 정렬된 리스트를 순회합니다.
        var handler = _sortedCommandHandlers?.FirstOrDefault(h => h.CanHandle(receivedMessage, out command));
        if (handler != null)
            responseMessage = await handler.HandleAsync(clientId, command, receivedMessage, _channelWriter, stoppingToken);

        if (!string.IsNullOrWhiteSpace(responseMessage))
            return responseMessage;

        return $"Unknown command: {receivedMessage.Trim()}";
    }
}
