using System.Runtime.InteropServices;
using MPCommander.Model;
using MPCommander.Native;

namespace MPCommander.IO;

/// <summary>
/// 차단 해제: 인터넷에서 받은 파일에 Windows 가 붙이는 표시(Zone.Identifier 대체 데이터 스트림, "Mark of the Web")를 지운다.
/// PowerShell 의 Unblock-File, 파일 속성의 '차단 해제' 와 같은 동작이다.
///  - 직접 고른 파일: 형식과 관계없이 처리
///  - 고른 폴더: 안의 파일 중 지정한 형식만 (하위 폴더는 Recursive 일 때)
/// </summary>
public sealed class UnblockOperation : ProgressOperation
{
    private const string StreamSuffix = ":Zone.Identifier";
    private const int ErrorFileNotFound = 2;
    private const int ErrorAccessDenied = 5;

    private readonly IReadOnlyList<string> _paths;
    private readonly bool _recursive;
    private readonly string[] _patterns;   // 비어 있으면 모든 파일
    private readonly List<string> _failures = new();

    public UnblockOperation(IReadOnlyList<string> paths, bool recursive, IEnumerable<string> extensions, bool allFiles)
    {
        _paths = paths;
        _recursive = recursive;
        _patterns = allFiles ? [] : extensions.Select(e => "*." + e.Trim().TrimStart('*', '.')).Where(p => p.Length > 2).ToArray();
    }

    public override string Title => "차단 해제";
    /// <summary>표시를 지운 파일 수</summary>
    public int Unblocked;
    /// <summary>검사했지만 원래 차단 표시가 없던 파일 수</summary>
    public int NotBlocked;
    public IReadOnlyList<string> Failures => _failures;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DeleteFileW(string path);

    protected override void Execute()
    {
        Phase = "차단 해제 중";
        foreach (var raw in _paths)
        {
            ThrowIfCancelled();
            var path = PathUtil.TrimEnd(raw);
            if (Directory.Exists(path)) Walk(path);
            else if (File.Exists(path)) Unblock(path);
        }
    }

    private unsafe void Walk(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ThrowIfCancelled();
            var dir = stack.Pop();
            CurrentFile = dir;
            Touch();

            var entries = new List<(string Name, uint Attr)>();
            FmEntryBatchCallback cb = (e, n, _) =>
            {
                for (int i = 0; i < n; i++) entries.Add((Marshal.PtrToStringUni(e[i].Name)!, e[i].Attributes));
                Touch();
                return IsCancelled ? 0 : 1;
            };
            int rc = NativeMethods.FmEnumDirectory(dir, cb, IntPtr.Zero, CancelPtr, null);
            GC.KeepAlive(cb);
            if (rc != 0)
            {
                ThrowIfCancelled();
                Fail(dir, Win32Errors.Message(rc));
                continue;
            }

            foreach (var (name, attr) in entries)
            {
                var full = Path.Combine(dir, name);
                if ((attr & (uint)FileAttributes.Directory) != 0)
                {
                    // 정션·심볼릭 링크는 따라가지 않는다 (순환 방지)
                    if (_recursive && (attr & (uint)FileAttributes.ReparsePoint) == 0) stack.Push(full);
                }
                else if (_patterns.Length == 0 || _patterns.Any(p => NativeMethods.PathMatchSpecW(name, p)))
                {
                    Unblock(full);
                }
            }
        }
    }

    private void Unblock(string file)
    {
        ThrowIfCancelled();
        TotalFiles++;
        CurrentFile = file;
        Touch();

        var stream = Extended(file) + StreamSuffix;
        if (DeleteFileW(stream))
        {
            Unblocked++;
        }
        else
        {
            int err = Marshal.GetLastWin32Error();
            if (err == ErrorFileNotFound) NotBlocked++;
            else if (err == ErrorAccessDenied && TryWithoutReadOnly(file, stream)) Unblocked++;
            else Fail(file, Win32Errors.Message(err));
        }
        DoneFiles++;
    }

    /// <summary>읽기 전용 파일은 스트림을 지울 수 없어서, 속성을 잠깐 풀었다가 되돌린다.</summary>
    private static bool TryWithoutReadOnly(string file, string stream)
    {
        try
        {
            var attr = File.GetAttributes(file);
            if (!attr.HasFlag(FileAttributes.ReadOnly)) return false;
            File.SetAttributes(file, attr & ~FileAttributes.ReadOnly);
            try { return DeleteFileW(stream); }
            finally { File.SetAttributes(file, attr); }
        }
        catch
        {
            return false;
        }
    }

    private void Fail(string path, string message)
    {
        Errors++;
        if (_failures.Count < 20) _failures.Add($"{path} — {message}");
    }

    private static string Extended(string p)
    {
        if (p.Length < 240 || p.StartsWith(@"\\?\")) return p;
        return p.StartsWith(@"\\") ? @"\\?\UNC\" + p.Substring(2) : @"\\?\" + p;
    }
}
