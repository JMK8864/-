using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Fa.Core.Common;

/// <summary>
/// 전용 스레드에서 일정 주기로 작업을 반복한다. IO 스캔, 모션 상태 갱신, 시퀀스 실행에 각각 하나씩 사용.
/// 실제 스캔 시간(Last/Max)을 기록해 화면에서 확인할 수 있다.
/// </summary>
public sealed class PeriodicWorker : IDisposable
{
    private readonly string _name;
    private readonly int _periodMs;
    private readonly Action _work;
    private readonly ILogger _log;
    private readonly ThreadPriority _priority;
    private Thread? _thread;
    private volatile bool _running;

    public PeriodicWorker(string name, int periodMs, Action work, ILogger log,
        ThreadPriority priority = ThreadPriority.AboveNormal)
    {
        _name = name; _periodMs = Math.Max(1, periodMs); _work = work; _log = log; _priority = priority;
    }

    public string Name => _name;
    public double LastScanMs { get; private set; }
    public double MaxScanMs { get; private set; }
    public bool IsRunning => _running;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { Name = _name, IsBackground = true, Priority = _priority };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(2000);
        _thread = null;
    }

    public void ResetMax() => MaxScanMs = 0;

    private void Loop()
    {
        if (OperatingSystem.IsWindows()) timeBeginPeriod(1); // Windows 타이머 분해능 1ms
        var sw = Stopwatch.StartNew();
        DateTime lastErrorLog = DateTime.MinValue;
        try
        {
            while (_running)
            {
                long start = sw.ElapsedTicks;
                try { _work(); }
                catch (Exception ex)
                {
                    // 같은 예외가 매 스캔 쌓이지 않도록 1초에 한 번만 기록
                    if (DateTime.Now - lastErrorLog > TimeSpan.FromSeconds(1))
                    {
                        _log.LogError(ex, "[{Worker}] 스캔 중 예외", _name);
                        lastErrorLog = DateTime.Now;
                    }
                }
                double elapsed = (sw.ElapsedTicks - start) * 1000.0 / Stopwatch.Frequency;
                LastScanMs = elapsed;
                if (elapsed > MaxScanMs) MaxScanMs = elapsed;

                int wait = _periodMs - (int)elapsed;
                Thread.Sleep(wait > 0 ? wait : 0);
            }
        }
        finally
        {
            if (OperatingSystem.IsWindows()) timeEndPeriod(1);
        }
    }

    public void Dispose() => Stop();

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);
}
