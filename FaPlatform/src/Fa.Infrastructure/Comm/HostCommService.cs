using System.Net;
using System.Net.Sockets;
using System.Text;
using Fa.Core.Alarms;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fa.Infrastructure.Comm;

/// <summary>
/// 상위 통신 (TCP, STX/ETX). Server 모드는 상위가 접속해오고, Client 모드는 설비가 상위로 접속(끊기면 재접속).
/// 받은 메시지는 <see cref="HostCommandHandler"/> 가 처리하고, 응답이 있으면 돌려준다.
/// 메시지 본문 형식(기본): "명령,인자1,인자2"  예) STATUS  /  START,CONTINUE  /  RECIPE,ProductA
/// </summary>
public sealed class HostCommService : BackgroundService
{
    private readonly HostCommOptions _opt;
    private readonly HostCommandHandler _handler;
    private readonly ILogger<HostCommService> _log;
    private readonly Encoding _enc;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private NetworkStream? _stream;

    public HostCommService(PlatformOptions options, HostCommandHandler handler, AlarmManager alarms, ILogger<HostCommService> log)
    {
        _opt = options.HostComm; _handler = handler; _log = log;
        _enc = Encoding.GetEncoding(_opt.Encoding);
        // 이벤트 보고
        alarms.Raised += a => _ = SendAsync($"ALARM,SET,{a.Code},{a.Source}");
        alarms.Cleared += a => _ = SendAsync($"ALARM,CLEAR,{a.Code},{a.Source}");
        handler.EventReport += msg => _ = SendAsync(msg);
    }

    public bool IsConnected => _stream != null;
    public string? RemoteEndPoint { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opt.Enabled) return;
        if (_opt.Mode == HostCommMode.Server) await RunServer(ct);
        else await RunClient(ct);
    }

    private async Task RunServer(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Parse(_opt.Address), _opt.Port);
        listener.Start();
        _log.LogInformation("상위통신 서버 대기 {Addr}:{Port}", _opt.Address, _opt.Port);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                // 상위는 하나만: 새 접속이 오면 이전 접속은 끊는다
                _stream?.Close();
                _ = Session(client, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally { listener.Stop(); }
    }

    private async Task RunClient(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = new TcpClient();
                await client.ConnectAsync(_opt.Address, _opt.Port, ct);
                await Session(client, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogWarning("상위 접속 실패: {Msg}", ex.Message); }
            await Task.Delay(_opt.ReconnectIntervalMs, ct).ContinueWith(_ => { });
        }
    }

    private async Task Session(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        client.NoDelay = true;
        var stream = client.GetStream();
        _stream = stream;
        RemoteEndPoint = client.Client.RemoteEndPoint?.ToString();
        _log.LogInformation("상위 연결 {Remote}", RemoteEndPoint);

        var framer = new StxEtxFramer();
        var buf = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buf, ct);
                if (n == 0) break;
                foreach (var frame in framer.Push(buf.AsSpan(0, n)))
                {
                    var msg = _enc.GetString(frame);
                    _log.LogInformation("RX {Msg}", msg);
                    var reply = _handler.Handle(msg);
                    if (reply != null) await SendAsync(reply);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            if (_stream == stream) { _stream = null; RemoteEndPoint = null; }
            _log.LogInformation("상위 연결 종료");
        }
    }

    public async Task<bool> SendAsync(string message)
    {
        var s = _stream;
        if (s == null) return false;
        await _sendLock.WaitAsync();
        try
        {
            await s.WriteAsync(StxEtxFramer.Encode(_enc.GetBytes(message)));
            _log.LogInformation("TX {Msg}", message);
            return true;
        }
        catch (Exception ex) { _log.LogWarning("송신 실패: {Msg}", ex.Message); return false; }
        finally { _sendLock.Release(); }
    }
}
