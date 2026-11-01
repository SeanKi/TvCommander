using System.Collections.Concurrent;
using System.Text;
using MPCommander.Model;

namespace MPCommander.IO;

/// <summary>
/// 작업 기록. 진행 창의 상세 보기에 줄을 넘기고, 같은 줄을 날짜별 파일(%APPDATA%\MP-Commander\logs\yyyy-MM-dd.log)에 남긴다.
/// 파일은 7일이 지나면 지운다. 여러 스레드(동시 복사)에서 불러도 된다.
/// </summary>
public sealed class OperationLog
{
    public const int KeepDays = 7;
    public static string Folder => Path.Combine(AppPaths.DataFolder, "logs");

    private static int _cleaned;
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly object _fileLock = new();
    private StreamWriter? _writer;

    /// <summary>작업 시작: 오늘 파일을 열고 머리줄을 쓴다 (7일 지난 파일은 처음 한 번 정리)</summary>
    public void Open(string header)
    {
        if (Interlocked.Exchange(ref _cleaned, 1) == 0) Cleanup();
        try
        {
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
            _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(true));
        }
        catch { _writer = null; /* 기록 파일을 못 열어도 작업은 한다 */ }
        Add("");
        Add("== " + header);
    }

    /// <summary>한 줄 (앞에 시각을 붙인다)</summary>
    public void Add(string text)
    {
        var line = text.Length == 0 ? "" : $"{DateTime.Now:HH:mm:ss.fff}  {text}";
        if (line.Length > 0) _pending.Enqueue(line);
        lock (_fileLock)
        {
            try { _writer?.WriteLine(line); } catch { }
        }
    }

    public void Add(string kind, string path, string? detail = null)
        => Add(detail == null ? $"{kind,-6} {path}" : $"{kind,-6} {path}  — {detail}");

    public void Close(string summary)
    {
        Add("== " + summary);
        lock (_fileLock)
        {
            try { _writer?.Dispose(); } catch { }
            _writer = null;
        }
    }

    /// <summary>진행 창이 가져갈 새 줄들</summary>
    public List<string> Drain()
    {
        var list = new List<string>();
        while (_pending.TryDequeue(out var s)) list.Add(s);
        return list;
    }

    private static void Cleanup()
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            var limit = DateTime.Now.Date.AddDays(-KeepDays);
            foreach (var f in Directory.GetFiles(Folder, "*.log"))
                if (File.GetLastWriteTime(f) < limit) File.Delete(f);
        }
        catch { /* 정리 실패는 다음에 */ }
    }
}
