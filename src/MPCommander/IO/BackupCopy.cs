using System.Runtime.InteropServices;
using System.Text;
using MPCommander.Model;
using MPCommander.Native;

namespace MPCommander.IO;

/// <summary>백업 복사에서 파일 하나: 원본 → 대상 (대상에 이미 있으면 먼저 백업)</summary>
public sealed record BackupCopyItem(string Source, string Destination, string Relative, long Size, bool DestinationExists);

/// <summary>
/// 백업 복사 계획: 고른 파일·폴더를 대상 폴더에 복사할 때 어떤 파일이 생기고, 어떤 파일을 덮어쓰는지.
/// UI 와 무관 — 나중에 스크립트/명령줄(자동 업데이트)에서도 같은 계획·작업을 쓴다.
/// </summary>
public sealed class BackupCopyPlan
{
    public string TargetDirectory { get; }
    public IReadOnlyList<string> Sources { get; }
    public List<BackupCopyItem> Items { get; } = new();
    public List<string> Directories { get; } = new();   // 대상에 만들 폴더 (상대 경로)

    public IEnumerable<BackupCopyItem> Overwrites => Items.Where(i => i.DestinationExists);
    public long TotalBytes => Items.Sum(i => i.Size);

    private BackupCopyPlan(IReadOnlyList<string> sources, string targetDir)
    {
        Sources = sources;
        TargetDirectory = PathUtil.TrimEnd(targetDir);
    }

    /// <summary>원본을 훑어 계획을 만든다. ctx.Report 로 진행을 알린다 (GuardedIo 타임아웃용).</summary>
    public static BackupCopyPlan Build(IReadOnlyList<string> sources, string targetDir, IoContext? ctx = null)
    {
        var plan = new BackupCopyPlan(sources, targetDir);
        foreach (var raw in sources)
        {
            var src = PathUtil.TrimEnd(raw);
            var name = Path.GetFileName(src);
            if (Directory.Exists(src)) plan.Walk(src, name, ctx);
            else plan.Add(src, name);
            ctx?.Report();
        }
        return plan;
    }

    private void Walk(string root, string relRoot, IoContext? ctx)
    {
        var stack = new Stack<(string Dir, string Rel)>();
        stack.Push((root, relRoot));
        while (stack.Count > 0)
        {
            if (ctx?.IsCancelled == true) throw new OperationCanceledException();
            var (dir, rel) = stack.Pop();
            Directories.Add(rel);
            foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                var childRel = Path.Combine(rel, info.Name);
                if (info is DirectoryInfo sub)
                {
                    if (!sub.Attributes.HasFlag(FileAttributes.ReparsePoint)) stack.Push((sub.FullName, childRel));
                }
                else if (info is FileInfo f)
                {
                    Add(f.FullName, childRel, f.Length);
                }
            }
            ctx?.Report();
        }
    }

    private void Add(string src, string rel, long size = -1)
    {
        var dst = Path.Combine(TargetDirectory, rel);
        if (size < 0) size = new FileInfo(src).Length;
        Items.Add(new BackupCopyItem(src, dst, rel, size, File.Exists(dst)));
    }

    /// <summary>백업 폴더: root 가 상대 경로면 대상 폴더 아래. 이름 = prefix + yyyyMMdd_HHmm (겹치면 _2, _3 ...)</summary>
    public string MakeBackupPath(string root, string prefix, DateTime now)
    {
        var baseDir = Path.IsPathRooted(root) ? root : Path.Combine(TargetDirectory, Compat.IsBlank(root) ? "backup" : root);
        var name = (prefix ?? "").Trim() + now.ToString("yyyyMMdd_HHmm");
        var path = Path.Combine(baseDir, name);
        for (int n = 2; Directory.Exists(path); n++) path = Path.Combine(baseDir, $"{name}_{n}");
        return path;
    }
}

/// <summary>사용 중이라 덮어쓸 수 없는 파일을 만났을 때의 선택</summary>
public enum LockedChoice { CloseAndRetry, KillAndRetry, Retry, RenameAndReplace, Skip, Cancel }

/// <summary>
/// 백업 복사 실행: (1) 덮어쓸 파일을 백업 폴더에 같은 구조로 복사 → (2) 원본을 대상에 복사 → (3) 백업 폴더에 기록(backup-copy.log).
/// 사용 중인 파일은 AskLocked 로 묻는다 (다시 시도 / 이름 바꿔 교체 / 건너뛰기 / 중단).
/// </summary>
public sealed class BackupCopyOperation : ProgressOperation
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorAccessDenied = 5;

    private readonly BackupCopyPlan _plan;
    private readonly FmProgressCallback _progressCb;
    private readonly StringBuilder _log = new();
    private long _bytesBase;

    public BackupCopyOperation(BackupCopyPlan plan, string backupPath)
    {
        _plan = plan;
        BackupPath = backupPath;
        _progressCb = OnProgress;
    }

    public override string Title => Loc.T("백업 후 복사");
    public string BackupPath { get; }
    public int BackedUp, Copied, Renamed;
    public bool BackupCreated { get; private set; }
    public List<string> Failures { get; } = new();
    /// <summary>복사 중에 닫은 프로그램 (끝나고 다시 실행할 후보)</summary>
    public List<LockingProcess> Closed { get; } = new();

    /// <summary>파일이 사용 중일 때: (파일, 그 파일을 쓰는 프로세스) → 선택. UI 가 프로그램을 닫았으면 Retry 를 돌려준다.</summary>
    public Func<string, IReadOnlyList<LockingProcess>, LockedChoice> AskLocked { get; set; } = (_, _) => LockedChoice.Skip;

    private int OnProgress(ulong transferred, ulong total, IntPtr user)
    {
        CurrentDone = (long)transferred;
        DoneBytes = _bytesBase + (long)transferred;
        Touch();
        return IsCancelled ? 1 : 0;
    }

    protected override void Execute()
    {
        var overwrites = _plan.Overwrites.ToList();
        TotalFiles = _plan.Items.Count + overwrites.Count;
        TotalBytes = _plan.TotalBytes + overwrites.Sum(o => SafeLength(o.Destination));
        Log(Loc.F($"# MP-Commander 백업 복사  {DateTime.Now:yyyy-MM-dd HH:mm:ss}"));
        Log(Loc.F($"대상: {_plan.TargetDirectory}"));
        foreach (var s in _plan.Sources) Log(Loc.F($"원본: {s}"));
        Log(Loc.F($"백업: {(overwrites.Count > 0 ? BackupPath : Loc.T("(덮어쓸 파일 없음)"))}"));

        try
        {
            // 1) 백업
            if (overwrites.Count > 0)
            {
                Phase = Loc.T("백업 중");
                Directory.CreateDirectory(BackupPath);
                BackupCreated = true;
                foreach (var it in overwrites)
                {
                    ThrowIfCancelled();
                    var bak = Path.Combine(BackupPath, it.Relative);
                    if (Attempt(it.Destination, () =>
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(bak)!);
                            Copy(it.Destination, bak);
                        }))
                    {
                        BackedUp++;
                        Log(Loc.F($"백업  {it.Relative}"));
                    }
                    else
                    {
                        // 백업이 안 된 파일은 덮어쓰지 않는다 (되돌릴 수 없게 되므로)
                        Fail(it.Destination, Loc.T("백업하지 못해 덮어쓰지 않았습니다."));
                        _skipOverwrite.Add(it.Destination);
                    }
                    DoneFiles++;
                    _bytesBase += SafeLength(it.Destination);
                    DoneBytes = _bytesBase;
                }
            }

            // 2) 복사
            Phase = Loc.T("복사 중");
            foreach (var rel in _plan.Directories) Attempt(rel, () => Directory.CreateDirectory(Path.Combine(_plan.TargetDirectory, rel)));
            foreach (var it in _plan.Items)
            {
                ThrowIfCancelled();
                if (_skipOverwrite.Contains(it.Destination)) { DoneFiles++; _bytesBase += it.Size; continue; }
                CopyWithLockHandling(it);
                DoneFiles++;
                _bytesBase += it.Size;
                DoneBytes = _bytesBase;
            }
        }
        finally
        {
            Log(Loc.F($"# 결과: 백업 {BackedUp}, 복사 {Copied}, 이름 바꿔 교체 {Renamed}, 실패 {Errors}") + (IsCancelled ? Loc.T(" (취소됨)") : ""));
            WriteLog();
        }
    }

    private readonly HashSet<string> _skipOverwrite = new(StringComparer.OrdinalIgnoreCase);

    private void CopyWithLockHandling(BackupCopyItem it)
    {
        CurrentFile = it.Source;
        CurrentSize = it.Size;
        CurrentDone = 0;
        Touch();
        while (true)
        {
            ThrowIfCancelled();
            Directory.CreateDirectory(Path.GetDirectoryName(it.Destination)!);
            int rc = NativeCopy(it.Source, it.Destination);
            if (rc == 0)
            {
                Copied++;
                Log(Loc.F($"복사  {it.Relative}"));
                return;
            }
            ThrowIfCancelled();

            if ((rc is ErrorSharingViolation or ErrorLockViolation or ErrorAccessDenied) && File.Exists(it.Destination))
            {
                var users = SafeFind(it.Destination);
                var choice = AskLocked(it.Destination, users);
                Touch();
                switch (choice)
                {
                    case LockedChoice.CloseAndRetry:
                    case LockedChoice.KillAndRetry:
                        Phase = Loc.T("프로그램 닫는 중");
                        foreach (var lp in users.Where(u => !u.IsSelf && !u.IsService))
                        {
                            bool ok = choice == LockedChoice.KillAndRetry ? RunningProcesses.Kill(lp) : RunningProcesses.CloseGracefully(lp);
                            if (ok) { Closed.Add(lp); Log(Loc.F($"닫음  {lp.Name} (PID {lp.Pid})")); }
                            Touch();
                        }
                        Phase = Loc.T("복사 중");
                        continue;
                    case LockedChoice.Retry:
                        continue;
                    case LockedChoice.RenameAndReplace:
                        // 실행 중인 exe/dll 도 이름은 바꿀 수 있다: 옛 파일을 비켜 두고 새 파일을 놓는다 (다시 실행하면 적용)
                        var old = it.Destination + $".old-{DateTime.Now:yyyyMMdd_HHmmss}";
                        try
                        {
                            File.Move(it.Destination, old);
                            Renamed++;
                            Log(Loc.F($"이름 바꿈  {it.Relative} → {Path.GetFileName(old)}"));
                        }
                        catch (Exception ex)
                        {
                            ReportLockFailure(it, ex.Message);
                            return;
                        }
                        continue;
                    case LockedChoice.Skip:
                        Fail(it.Destination, Loc.T("사용 중이라 건너뜀"));
                        return;
                    default:
                        Cancel();
                        throw new OperationCanceledException();
                }
            }

            // 그 밖의 오류는 일반 오류 창 (다시 시도 / 건너뛰기 / 중단)
            var msg = Win32Errors.Message(rc);
            switch (AskError(it.Destination, msg))
            {
                case ErrorChoice.Retry: continue;
                case ErrorChoice.Skip: Fail(it.Destination, msg); return;
                default: Cancel(); throw new OperationCanceledException();
            }
        }
    }

    private void ReportLockFailure(BackupCopyItem it, string message)
    {
        Fail(it.Destination, message);
        if (AskError(it.Destination, message) == ErrorChoice.Cancel)
        {
            Cancel();
            throw new OperationCanceledException();
        }
    }

    private static IReadOnlyList<LockingProcess> SafeFind(string file)
    {
        try { return RunningProcesses.FindUsing([file]); }
        catch { return []; }
    }

    private unsafe int NativeCopy(string src, string dst)
        => NativeMethods.FmCopyFile(src, dst, 1, _progressCb, IntPtr.Zero, CancelPtr);

    private void Copy(string src, string dst)
    {
        CurrentFile = src;
        CurrentSize = SafeLength(src);
        CurrentDone = 0;
        int rc = NativeCopy(src, dst);
        if (rc != 0)
        {
            ThrowIfCancelled();
            throw Win32Errors.ToIOException(rc, src);
        }
    }

    private void Fail(string path, string message)
    {
        Errors++;
        if (Failures.Count < 30) Failures.Add($"{path} — {message}");
        Log(Loc.F($"실패  {path} — {message}"));
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    private void Log(string line) => _log.AppendLine(line);

    /// <summary>백업 폴더에 기록을 남긴다 (무엇을 백업·복사했는지 — 되돌릴 때·자동화할 때 참고)</summary>
    private void WriteLog()
    {
        if (!BackupCreated) return;
        try { File.WriteAllText(Path.Combine(BackupPath, "backup-copy.log"), _log.ToString(), new UTF8Encoding(true)); }
        catch { }
    }

    public string LogText => _log.ToString();
}
