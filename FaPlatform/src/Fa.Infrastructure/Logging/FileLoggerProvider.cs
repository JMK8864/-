using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Fa.Infrastructure.Logging;

/// <summary>
/// 텍스트 파일 로거. 로그는 큐에 넣고 전용 스레드가 파일에 쓰므로 시퀀스/IO 스레드가 디스크 때문에 멈추지 않는다.
/// 파일: {Directory}/{yyyy-MM-dd}/{Route}.txt  (예: Logs/2026-11-17/Sequence.txt)
/// 형식: 2026-11-17 10:20:30.123 [INF] [T12] Seq.Loader: 메시지
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly FileLogOptions _options;
    private readonly string _root;
    private readonly BlockingCollection<(DateTime Time, string Route, string Line)> _queue = new(100_000);
    private readonly Thread _writer;
    private readonly Dictionary<string, (string Date, StreamWriter Writer)> _files = new();

    public FileLoggerProvider(FileLogOptions options, string dataDirectory)
    {
        _options = options;
        _root = Path.IsPathRooted(options.Directory) ? options.Directory : Path.Combine(dataDirectory, options.Directory);
        Directory.CreateDirectory(_root);
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "FileLogger", Priority = ThreadPriority.BelowNormal };
        _writer.Start();
        Task.Run(DeleteOldFolders);
    }

    public string LogRoot => _root;

    public ILogger CreateLogger(string categoryName)
    {
        var route = _options.Routes.FirstOrDefault(r => categoryName.StartsWith(r.Key, StringComparison.Ordinal)).Value ?? "System";
        return new FileLogger(this, categoryName, route);
    }

    internal void Enqueue(DateTime time, string route, string line) => _queue.TryAdd((time, route, line));

    private void WriteLoop()
    {
        foreach (var (time, route, line) in _queue.GetConsumingEnumerable())
        {
            try
            {
                GetWriter(time, route).WriteLine(line);
                if (_queue.Count == 0) foreach (var f in _files.Values) f.Writer.Flush();
            }
            catch { /* 로그 실패로 설비가 멈추면 안 된다 */ }
        }
    }

    private StreamWriter GetWriter(DateTime time, string route)
    {
        var date = time.ToString("yyyy-MM-dd");
        if (_files.TryGetValue(route, out var f) && f.Date == date) return f.Writer;
        f.Writer?.Dispose();
        var dir = Path.Combine(_root, date);
        Directory.CreateDirectory(dir);
        var w = new StreamWriter(new FileStream(Path.Combine(dir, route + ".txt"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8);
        _files[route] = (date, w);
        return w;
    }

    private void DeleteOldFolders()
    {
        try
        {
            var limit = DateTime.Today.AddDays(-_options.RetentionDays);
            foreach (var d in Directory.GetDirectories(_root))
                if (DateTime.TryParse(Path.GetFileName(d), out var day) && day < limit) Directory.Delete(d, true);
        }
        catch { }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(2000);
        foreach (var f in _files.Values) f.Writer.Dispose();
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _p;
        private readonly string _category, _route;
        public FileLogger(FileLoggerProvider p, string category, string route) { _p = p; _category = category; _route = route; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var now = DateTime.Now;
            var lvl = logLevel switch
            {
                LogLevel.Information => "INF", LogLevel.Warning => "WRN", LogLevel.Error => "ERR",
                LogLevel.Critical => "CRT", LogLevel.Debug => "DBG", _ => "TRC"
            };
            var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{lvl}] [T{Environment.CurrentManagedThreadId}] {_category}: {formatter(state, exception)}";
            if (exception != null) line += Environment.NewLine + exception;
            _p.Enqueue(now, _route, line);
        }
    }
}
