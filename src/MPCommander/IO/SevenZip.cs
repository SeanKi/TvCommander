using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using MPCommander.Model;

namespace MPCommander.IO;

/// <summary>
/// 7-Zip 명령줄(7z.exe / 7za.exe) 찾기. 7z 형식 압축과 7z·RAR·TAR 등 압축 풀기에 쓴다.
/// 찾는 순서: 프로그램 폴더(7za.exe, 7z.exe) → 레지스트리의 7-Zip 설치 경로 → Program Files → PATH
/// </summary>
public static class SevenZip
{
    private static string? _exe;
    private static bool _searched;

    /// <summary>압축 풀기를 7-Zip 에 맡기는 확장자 (ZIP 은 내장으로 푼다)</summary>
    public static readonly string[] ExtractTypes =
        ["7z", "rar", "tar", "gz", "tgz", "bz2", "tbz2", "xz", "txz", "zst", "lzma", "cab", "iso", "wim", "arj", "lzh", "z", "zipx", "jar"];

    public static string? Exe
    {
        get
        {
            if (!_searched) { _exe = Find(); _searched = true; }
            return _exe;
        }
    }

    public static bool CanExtract(string extension)
        => ExtractTypes.Contains(extension.ToLowerInvariant());

    private static string? Find()
    {
        var candidates = new List<string>();
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        candidates.Add(Path.Combine(appDir, "7za.exe"));
        candidates.Add(Path.Combine(appDir, "7z.exe"));

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var key = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(@"SOFTWARE\7-Zip");
                foreach (var name in new[] { "Path64", "Path" })
                    if (key?.GetValue(name) is string dir && dir.Length > 0) candidates.Add(Path.Combine(dir, "7z.exe"));
            }
            catch { }
        }

        foreach (var pf in new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetEnvironmentVariable("ProgramFiles"),
                                   Environment.GetEnvironmentVariable("ProgramFiles(x86)") })
            if (!Compat.IsBlank(pf)) candidates.Add(Path.Combine(pf, "7-Zip", "7z.exe"));

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").SplitTrim(';'))
        {
            candidates.Add(Path.Combine(dir, "7z.exe"));
            candidates.Add(Path.Combine(dir, "7za.exe"));
        }

        // 네트워크 경로의 PATH 항목에서 멈추지 않도록 로컬 경로만 확인
        return candidates.FirstOrDefault(c => !c.StartsWith(@"\\") && SafeExists(c));
    }

    private static bool SafeExists(string path)
    {
        try { return File.Exists(path); }
        catch { return false; }
    }
}

/// <summary>
/// 7-Zip 으로 압축(7z) 또는 압축 풀기. 7z.exe 를 띄우고 -bsp1 진행률을 읽어 진행 창에 보여 준다.
/// 압축은 임시 파일에 만든 뒤 완성되면 이름을 바꾸므로, 취소해도 깨진 파일이 남지 않는다.
/// </summary>
public sealed class SevenZipOperation : ProgressOperation
{
    private static readonly Regex ProgressLine = new(@"^\s*(\d{1,3})%(?:\s+(\d+))?(?:\s+[+\-U=]\s+(.*))?", RegexOptions.Compiled);

    private readonly bool _pack;
    private readonly IReadOnlyList<string> _sources;
    private readonly string _target;
    private readonly int _level;
    private readonly bool _overwrite;
    private readonly StringBuilder _errors = new();
    private Process? _process;

    private SevenZipOperation(bool pack, IReadOnlyList<string> sources, string target, int level, bool overwrite)
    {
        _pack = pack;
        _sources = sources;
        _target = target;
        _level = level;
        _overwrite = overwrite;
    }

    /// <summary>level: 0 저장, 1 빠르게, 2 보통, 3 최고</summary>
    public static SevenZipOperation Pack(IReadOnlyList<string> sources, string archivePath, int level) => new(true, sources, archivePath, level, true);
    /// <summary>overwrite=false 면 이미 있는 파일은 건너뛴다</summary>
    public static SevenZipOperation Extract(string archivePath, string destDir, bool overwrite) => new(false, new[] { archivePath }, destDir, 0, overwrite);

    public override string Title => _pack ? "7z 압축" : "압축 풀기";
    public string TargetDirectory => _pack ? PathUtil.Parent(_target) ?? _target : _target;

    public override void Cancel()
    {
        base.Cancel();
        try { if (_process is { HasExited: false } p) p.Kill(); } catch { }
    }

    protected override void Execute()
    {
        var exe = SevenZip.Exe ?? throw new InvalidOperationException("7-Zip(7z.exe)을 찾을 수 없습니다.");
        if (_pack) DoPack(exe);
        else DoExtract(exe);
    }

    private void DoPack(string exe)
    {
        Phase = "목록 수집 중";
        foreach (var raw in _sources)
        {
            ThrowIfCancelled();
            var src = PathUtil.TrimEnd(raw);
            if (Directory.Exists(src)) Count(src);
            else if (File.Exists(src)) { TotalFiles++; TotalBytes += new FileInfo(src).Length; }
        }

        // 고른 항목은 모두 같은 폴더에 있다: 그 폴더에서 이름만 넘겨야 압축 안의 경로가 깔끔하다
        var baseDir = PathUtil.Parent(PathUtil.TrimEnd(_sources[0])) ?? _sources[0];
        var listFile = Path.Combine(Path.GetTempPath(), $"mpc-7z-{Guid.NewGuid():N}.txt");
        File.WriteAllLines(listFile, _sources.Select(s => Path.GetFileName(PathUtil.TrimEnd(s))), new UTF8Encoding(false));

        var temp = _target + ".tvc-tmp";
        int mx = _level switch { 0 => 0, 1 => 1, 3 => 9, _ => 5 };
        try
        {
            if (File.Exists(temp)) File.Delete(temp);
            Phase = "압축 중";
            int code = Run(exe, $"a -t7z -mx={mx} -ssw -y -bsp1 -bso0 -bse2 -scsUTF-8 -sccUTF-8 \"{temp}\" \"@{listFile}\"", baseDir);
            ThrowIfCancelled();
            CheckExitCode(code, _target);
            Compat.MoveReplace(temp, _target);
            DoneBytes = TotalBytes;
            DoneFiles = TotalFiles;
        }
        finally
        {
            try { File.Delete(listFile); } catch { }
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private void DoExtract(string exe)
    {
        var archive = _sources[0];
        TotalBytes = new FileInfo(archive).Length;
        Directory.CreateDirectory(_target);
        Phase = "압축 푸는 중";
        // -spe 없이 -o: 대상 폴더 아래로 푼다. 7-Zip 은 폴더 밖을 가리키는 항목(../)을 스스로 막는다
        int code = Run(exe, $"x -y {(_overwrite ? "-aoa" : "-aos")} -bsp1 -bso0 -bse2 -sccUTF-8 \"-o{PathUtil.TrimEnd(_target)}\" \"{archive}\"",
            PathUtil.Parent(archive) ?? _target);
        ThrowIfCancelled();
        CheckExitCode(code, archive);
        DoneBytes = TotalBytes;
    }

    private void CheckExitCode(int code, string path)
    {
        var msg = _errors.ToString().Trim();
        switch (code)
        {
            case 0:
                return;
            case 1:   // 경고: 잠긴 파일 등 일부를 건너뜀. 결과물은 쓸 만하다
                ReportError(path, "일부 파일을 처리하지 못했습니다.\n" + Shorten(msg));
                return;
            case 255:
                throw new OperationCanceledException();
            default:
                throw new IOException($"7-Zip 오류 (코드 {code})" + (msg.Length > 0 ? ":\n" + Shorten(msg) : ""));
        }
    }

    private static string Shorten(string s) => s.Length > 1500 ? s.Substring(0, 1500) + "\n..." : s;

    private void Count(string dir)
    {
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            ThrowIfCancelled();
            var d = stack.Pop();
            CurrentFile = d;
            Touch();
            try
            {
                foreach (var info in new DirectoryInfo(d).EnumerateFileSystemInfos())
                {
                    if (info is DirectoryInfo sub) { if (!sub.Attributes.HasFlag(FileAttributes.ReparsePoint)) stack.Push(sub.FullName); }
                    else if (info is FileInfo f) { TotalFiles++; TotalBytes += f.Length; }
                }
            }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>7z.exe 실행. 진행률(-bsp1)은 표준 출력에 백스페이스로 덮어쓰며 나온다.</summary>
    private int Run(string exe, string args, string workDir)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = new Process { StartInfo = psi };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_errors) _errors.AppendLine(e.Data); };
        p.Start();
        _process = p;
        if (IsCancelled) { try { p.Kill(); } catch { } }
        p.BeginErrorReadLine();

        var line = new StringBuilder();
        var reader = p.StandardOutput;
        int ch;
        while ((ch = reader.Read()) >= 0)
        {
            if (ch == '\b' || ch == '\r' || ch == '\n')
            {
                if (line.Length > 0) { OnProgress(line.ToString()); line.Clear(); }
            }
            else
            {
                line.Append((char)ch);
            }
        }
        if (line.Length > 0) OnProgress(line.ToString());
        p.WaitForExit();
        _process = null;
        return p.ExitCode;
    }

    private void OnProgress(string text)
    {
        var m = ProgressLine.Match(text);
        if (!m.Success) return;
        int pct = Math.Min(100, int.Parse(m.Groups[1].Value));
        DoneBytes = TotalBytes * pct / 100;
        if (m.Groups[2].Success && int.TryParse(m.Groups[2].Value, out var files)) DoneFiles = Math.Min(files, Math.Max(TotalFiles, files));
        if (m.Groups[3].Success && m.Groups[3].Value.Trim() is { Length: > 0 } name) CurrentFile = name;
        Touch();
    }
}
