using System.ComponentModel;
using System.Runtime.InteropServices;
using MPCommander.Model;
using MPCommander.Native;

namespace MPCommander.IO;

/// <summary>두 폴더에서 한 항목의 차이</summary>
public enum SyncDiff { OnlyA, OnlyB, NewerA, NewerB, Conflict }   // Conflict: 날짜는 같은데 크기(내용)가 다름

/// <summary>동기화 방식</summary>
public enum SyncMode
{
    Both,      // 양쪽 똑같이: 없는 것은 서로 복사, 다르면 더 최신 쪽으로. 지우지 않는다
    AtoB,      // A → B 최신만: B 에 없거나 A 가 더 최신인 것만
    BtoA,      // B → A 최신만
    MirrorA,   // A 만 남기기: B 를 A 와 똑같이 (B 에만 있는 것은 지운다 — 휴지통)
    MirrorB,   // B 만 남기기
}

public enum SyncAct { None, CopyAtoB, CopyBtoA, DeleteA, DeleteB }

/// <summary>파일(또는 한쪽에만 있는 폴더 묶음)의 크기·시각</summary>
public sealed record SyncSide(long Size, DateTime WriteUtc, int Files = 1);

/// <summary>비교 결과 한 줄. 한쪽에만 있는 폴더는 그 아래 전체를 한 줄로 묶는다</summary>
public sealed class SyncEntry : INotifyPropertyChanged
{
    private bool _include;
    private SyncAct _action;

    public SyncEntry(string relative, bool isDirectory, SyncDiff diff, SyncSide? a, SyncSide? b)
    {
        Relative = relative;
        IsDirectory = isDirectory;
        Diff = diff;
        A = a;
        B = b;
    }

    public string Relative { get; }
    public bool IsDirectory { get; }
    public SyncDiff Diff { get; }
    public SyncSide? A { get; }
    public SyncSide? B { get; }

    public SyncAct Action
    {
        get => _action;
        set { _action = value; Changed(nameof(Action)); Changed(nameof(ActionText)); }
    }

    /// <summary>실행에 넣을지 (목록의 체크)</summary>
    public bool Include
    {
        get => _include;
        set { if (_include == value) return; _include = value; Changed(nameof(Include)); IncludeChanged?.Invoke(); }
    }

    public string Note { get; set; } = "";

    public string DisplayPath => IsDirectory ? Relative + "\\" : Relative;
    public string DiffText => Diff switch
    {
        SyncDiff.OnlyA => Loc.T("A 에만"),
        SyncDiff.OnlyB => Loc.T("B 에만"),
        SyncDiff.NewerA => Loc.T("A 가 최신"),
        SyncDiff.NewerB => Loc.T("B 가 최신"),
        _ => Loc.T("날짜 같고 내용 다름"),
    };
    public string ActionText => Action switch
    {
        SyncAct.CopyAtoB => "A → B",
        SyncAct.CopyBtoA => "A ← B",
        SyncAct.DeleteA => Loc.T("A 에서 삭제"),
        SyncAct.DeleteB => Loc.T("B 에서 삭제"),
        _ => Loc.T("그대로"),
    } + (Note.Length > 0 ? "  (" + Note + ")" : "");
    public string ASize => Side(A);
    public string BSize => Side(B);
    public string ADate => A == null ? "" : A.WriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string BDate => B == null ? "" : B.WriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    private string Side(SyncSide? s) => s == null ? "" :
        IsDirectory ? Loc.F($"{s.Files:N0}개, {PathUtil.FormatSize(s.Size)}") : s.Size.ToString("N0");

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? IncludeChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 두 폴더 비교 (하위 폴더 포함). 같은지는 크기 + 수정 시각(2초 오차 허용 — FAT/SMB) 으로,
/// 크기가 같고 시각만 다른 작은 파일(1 MB 이하)은 내용까지 비교해 같으면 같은 파일로 본다.
/// </summary>
public sealed class FolderCompare
{
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);
    public const long ContentCheckMax = 1024 * 1024;

    public string A { get; }
    public string B { get; }
    public List<SyncEntry> Entries { get; } = new();
    public int FilesA { get; private set; }
    public int FilesB { get; private set; }
    public long BytesA { get; private set; }
    public long BytesB { get; private set; }
    public int Same { get; private set; }
    public int ContentSame { get; private set; }
    public List<string> Errors { get; } = new();

    private FolderCompare(string a, string b) { A = a; B = b; }

    private sealed class Tree
    {
        public readonly Dictionary<string, SyncSide> Files = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Dirs = new(StringComparer.OrdinalIgnoreCase);
    }

    public static FolderCompare Run(string a, string b, IReadOnlyList<string> excludes, bool contentCheck, Action<string>? progress, CancellationToken ct)
    {
        var r = new FolderCompare(PathUtil.TrimEnd(a), PathUtil.TrimEnd(b));
        var ta = r.Scan(r.A, excludes, progress, ct);
        var tb = r.Scan(r.B, excludes, progress, ct);
        r.FilesA = ta.Files.Count; r.BytesA = ta.Files.Values.Sum(f => f.Size);
        r.FilesB = tb.Files.Count; r.BytesB = tb.Files.Values.Sum(f => f.Size);

        // 한쪽에만 있는 폴더는 맨 위 것 하나로 묶는다 (그 아래 파일은 따로 보이지 않는다)
        var onlyDirsA = TopMost(ta.Dirs.Where(d => !tb.Dirs.Contains(d)));
        var onlyDirsB = TopMost(tb.Dirs.Where(d => !ta.Dirs.Contains(d)));
        foreach (var d in onlyDirsA) r.Entries.Add(new SyncEntry(d, true, SyncDiff.OnlyA, Summary(ta, d), null));
        foreach (var d in onlyDirsB) r.Entries.Add(new SyncEntry(d, true, SyncDiff.OnlyB, null, Summary(tb, d)));
        bool Under(string rel, List<string> dirs) => dirs.Any(d => rel.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase));

        int done = 0;
        foreach (var kv in ta.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (++done % 500 == 0) progress?.Invoke(Loc.F($"비교 중... {done:N0}"));
            var rel = kv.Key;
            var fa = kv.Value;
            if (!tb.Files.TryGetValue(rel, out var fb))
            {
                if (!Under(rel, onlyDirsA)) r.Entries.Add(new SyncEntry(rel, false, SyncDiff.OnlyA, fa, null));
                continue;
            }
            var dt = fa.WriteUtc - fb.WriteUtc;
            bool sameTime = dt.Duration() <= Tolerance;
            if (fa.Size == fb.Size && sameTime) { r.Same++; continue; }
            if (fa.Size == fb.Size && contentCheck && fa.Size <= ContentCheckMax && SameContent(Path.Combine(r.A, rel), Path.Combine(r.B, rel)))
            {
                r.Same++;
                r.ContentSame++;
                continue;
            }
            var diff = sameTime ? SyncDiff.Conflict : dt > TimeSpan.Zero ? SyncDiff.NewerA : SyncDiff.NewerB;
            r.Entries.Add(new SyncEntry(rel, false, diff, fa, fb));
        }
        foreach (var kv in tb.Files)
            if (!ta.Files.ContainsKey(kv.Key) && !Under(kv.Key, onlyDirsB))
                r.Entries.Add(new SyncEntry(kv.Key, false, SyncDiff.OnlyB, null, kv.Value));

        r.Entries.Sort((x, y) => string.Compare(x.Relative, y.Relative, StringComparison.OrdinalIgnoreCase));
        return r;
    }

    private Tree Scan(string root, IReadOnlyList<string> excludes, Action<string>? progress, CancellationToken ct)
    {
        var t = new Tree();
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var stack = new Stack<string>();
        stack.Push(root);
        int n = 0;
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            IEnumerable<FileSystemInfo> items;
            try { items = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
            catch (Exception ex) { Errors.Add($"{dir} — {ex.Message}"); continue; }
            foreach (var it in items)
            {
                if (Excluded(it.Name, excludes)) continue;
                var rel = it.FullName.Substring(root.Length).TrimStart('\\');
                if (it is DirectoryInfo di)
                {
                    if ((di.Attributes & FileAttributes.ReparsePoint) != 0) continue;   // 정션·링크는 따라가지 않는다
                    t.Dirs.Add(rel);
                    stack.Push(di.FullName);
                }
                else if (it is FileInfo fi)
                {
                    t.Files[rel] = new SyncSide(fi.Length, fi.LastWriteTimeUtc);
                    if (++n % 2000 == 0) progress?.Invoke(Loc.F($"읽는 중... {root} {n:N0}"));
                }
            }
        }
        return t;
    }

    private static bool Excluded(string name, IReadOnlyList<string> patterns)
    {
        foreach (var p in patterns)
            if (p.Length > 0 && PathMatchSpec(name, p)) return true;
        return false;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern bool PathMatchSpec(string file, string spec);

    private static List<string> TopMost(IEnumerable<string> dirs)
    {
        var sorted = dirs.OrderBy(d => d.Length).ToList();
        var top = new List<string>();
        foreach (var d in sorted)
            if (!top.Any(t => d.StartsWith(t + "\\", StringComparison.OrdinalIgnoreCase))) top.Add(d);
        return top;
    }

    private static SyncSide Summary(Tree t, string dir)
    {
        var files = t.Files.Where(f => f.Key.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase)).Select(f => f.Value).ToList();
        return new SyncSide(files.Sum(f => f.Size), files.Count == 0 ? DateTime.MinValue : files.Max(f => f.WriteUtc), files.Count);
    }

    private static bool SameContent(string a, string b)
    {
        try { return File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b)); }
        catch { return false; }
    }

    // ───────────────────────── 계획 ─────────────────────────

    /// <summary>방식에 따라 각 줄의 작업을 정한다. 할 일이 있는 줄은 체크</summary>
    public void Plan(SyncMode mode)
    {
        foreach (var e in Entries)
        {
            e.Note = "";
            e.Action = mode switch
            {
                SyncMode.Both => e.Diff switch
                {
                    SyncDiff.OnlyA or SyncDiff.NewerA => SyncAct.CopyAtoB,
                    SyncDiff.OnlyB or SyncDiff.NewerB => SyncAct.CopyBtoA,
                    _ => SyncAct.None,
                },
                SyncMode.AtoB => e.Diff is SyncDiff.OnlyA or SyncDiff.NewerA ? SyncAct.CopyAtoB : SyncAct.None,
                SyncMode.BtoA => e.Diff is SyncDiff.OnlyB or SyncDiff.NewerB ? SyncAct.CopyBtoA : SyncAct.None,
                SyncMode.MirrorA => e.Diff == SyncDiff.OnlyB ? SyncAct.DeleteB : SyncAct.CopyAtoB,
                SyncMode.MirrorB => e.Diff == SyncDiff.OnlyA ? SyncAct.DeleteA : SyncAct.CopyBtoA,
                _ => SyncAct.None,
            };
            if (e.Diff == SyncDiff.Conflict && e.Action == SyncAct.None) e.Note = Loc.T("직접 고르세요");
            if (mode == SyncMode.MirrorA && e.Diff == SyncDiff.NewerB) e.Note = Loc.T("B 의 더 최신 파일을 덮어씀");
            if (mode == SyncMode.MirrorB && e.Diff == SyncDiff.NewerA) e.Note = Loc.T("A 의 더 최신 파일을 덮어씀");
            e.Include = e.Action != SyncAct.None;
        }
    }

    // ───────────────────────── 조언 ─────────────────────────

    /// <summary>어느 쪽을 남길지: 한쪽에만 있는 것과 더 최신인 것이 많은 쪽. 양쪽 다 있으면 '양쪽 똑같이' 를 권한다</summary>
    public (string Text, SyncMode Suggest) Advice()
    {
        int onlyA = Entries.Where(e => e.Diff == SyncDiff.OnlyA).Sum(e => e.A!.Files);
        int onlyB = Entries.Where(e => e.Diff == SyncDiff.OnlyB).Sum(e => e.B!.Files);
        int newerA = Entries.Count(e => e.Diff == SyncDiff.NewerA);
        int newerB = Entries.Count(e => e.Diff == SyncDiff.NewerB);
        int conflicts = Entries.Count(e => e.Diff == SyncDiff.Conflict);
        int scoreA = onlyA + newerA, scoreB = onlyB + newerB;

        var facts = Loc.F($"A: 파일 {FilesA:N0}개 ({PathUtil.FormatSize(BytesA)}), A 에만 {onlyA:N0}, A 가 최신 {newerA:N0}   │   B: 파일 {FilesB:N0}개 ({PathUtil.FormatSize(BytesB)}), B 에만 {onlyB:N0}, B 가 최신 {newerB:N0}");
        if (conflicts > 0) facts += Loc.F($"   │   날짜 같고 내용 다름 {conflicts:N0}");

        if (Entries.Count == 0) return (Loc.T("두 폴더가 같습니다.") + "\n" + facts, SyncMode.Both);
        if (scoreA == 0 && scoreB == 0)
            return (Loc.T("한쪽에만 있거나 더 최신인 파일은 없고, 날짜가 같은데 내용이 다른 파일만 있습니다 — 목록에서 직접 고르세요.") + "\n" + facts, SyncMode.Both);
        if (scoreB == 0)
            return (Loc.T("A 를 남기세요: B 에는 A 에 없는 파일도, 더 최신 파일도 없습니다. 'A 만 남기기' 나 'A → B 최신만' 으로 맞추면 잃는 것이 없습니다.") + "\n" + facts, SyncMode.MirrorA);
        if (scoreA == 0)
            return (Loc.T("B 를 남기세요: A 에는 B 에 없는 파일도, 더 최신 파일도 없습니다. 'B 만 남기기' 나 'B → A 최신만' 으로 맞추면 잃는 것이 없습니다.") + "\n" + facts, SyncMode.MirrorB);

        // 양쪽 다 상대에 없는 것·더 최신인 것이 있다 → 한쪽만 남기면 잃는다
        var more = scoreA >= scoreB ? "A" : "B";
        var less = more == "A" ? "B" : "A";
        int lose = Math.Min(scoreA, scoreB);
        return (Loc.F($"양쪽 똑같이를 권합니다: 두 폴더 모두 상대에 없는 파일이나 더 최신 파일이 있습니다. 꼭 한쪽만 남겨야 한다면 더 많은 쪽인 {more} 를 남기세요 — 이때 {less} 의 {lose:N0}개(없는 파일·더 최신 파일)를 잃습니다.") + "\n" + facts,
                SyncMode.Both);
    }
}

/// <summary>동기화 실행: 복사(파일·폴더) 먼저, 삭제(휴지통)는 마지막에 한 번에. 진행 창에 표시된다</summary>
public sealed class FolderSyncOperation : ProgressOperation
{
    private readonly string _a, _b;
    private readonly List<SyncEntry> _items;
    private readonly FmProgressCallback _progressCb;
    private long _bytesBase;

    public int Copied, Deleted;
    public List<string> Failures { get; } = new();

    public FolderSyncOperation(string a, string b, IEnumerable<SyncEntry> items)
    {
        _a = a; _b = b;
        _items = items.Where(i => i.Include && i.Action != SyncAct.None).ToList();
        _progressCb = OnProgress;
        TotalFiles = _items.Sum(i => i.IsDirectory ? (i.Action is SyncAct.CopyAtoB ? i.A!.Files : i.Action is SyncAct.CopyBtoA ? i.B!.Files : 1) : 1);
        TotalBytes = _items.Where(i => i.Action is SyncAct.CopyAtoB or SyncAct.CopyBtoA)
                           .Sum(i => i.Action == SyncAct.CopyAtoB ? i.A!.Size : i.B!.Size);
    }

    public override string Title => Loc.T("폴더 동기화");

    private int OnProgress(ulong transferred, ulong total, IntPtr user)
    {
        CurrentDone = (long)transferred;
        DoneBytes = _bytesBase + (long)transferred;
        Touch();
        return IsCancelled ? 1 : 0;
    }

    protected override void Execute()
    {
        Phase = Loc.T("복사 중");
        foreach (var it in _items.Where(i => i.Action is SyncAct.CopyAtoB or SyncAct.CopyBtoA))
        {
            ThrowIfCancelled();
            bool toB = it.Action == SyncAct.CopyAtoB;
            var src = Path.Combine(toB ? _a : _b, it.Relative);
            var dst = Path.Combine(toB ? _b : _a, it.Relative);
            if (it.IsDirectory) CopyTree(src, dst);
            else CopyOne(src, dst);
        }

        var deletes = _items.Where(i => i.Action is SyncAct.DeleteA or SyncAct.DeleteB)
                            .Select(i => Path.Combine(i.Action == SyncAct.DeleteA ? _a : _b, i.Relative)).ToList();
        if (deletes.Count > 0)
        {
            ThrowIfCancelled();
            Phase = Loc.T("휴지통으로 옮기는 중");
            CurrentFile = deletes[0];
            Touch();
            // 한 번에 휴지통으로 (네트워크 드라이브는 휴지통이 없어 바로 지워진다 — 실행 전에 알렸다)
            Attempt(deletes[0], () =>
            {
                int rc = Recycle(deletes);
                if (rc != 0) throw new IOException(Win32Errors.Message(rc));
            });
            Deleted = deletes.Count(p => !File.Exists(p) && !Directory.Exists(p));
            DoneFiles += deletes.Count;
        }
    }

    private static unsafe int Recycle(List<string> paths)
    {
        var list = string.Concat(paths.Select(p => p + "\0")) + "\0";
        fixed (char* ptr = list) return NativeMethods.FmShellDelete(ptr, 0, IntPtr.Zero);
    }

    private void CopyTree(string src, string dst)
    {
        Attempt(dst, () => Directory.CreateDirectory(dst));
        foreach (var info in new DirectoryInfo(src).EnumerateFileSystemInfos())
        {
            ThrowIfCancelled();
            var target = Path.Combine(dst, info.Name);
            if (info is DirectoryInfo d) { if ((d.Attributes & FileAttributes.ReparsePoint) == 0) CopyTree(d.FullName, target); }
            else CopyOne(info.FullName, target);
        }
    }

    private unsafe void CopyOne(string src, string dst)
    {
        CurrentFile = src;
        CurrentSize = SafeLength(src);
        CurrentDone = 0;
        Touch();
        bool ok = Attempt(dst, () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            if (File.Exists(dst))
            {
                var attr = File.GetAttributes(dst);
                if ((attr & FileAttributes.ReadOnly) != 0) File.SetAttributes(dst, attr & ~FileAttributes.ReadOnly);
            }
            // CopyFileEx 는 수정 시각도 그대로 옮긴다 → 다음 비교에서 같은 파일
            int rc = NativeMethods.FmCopyFile(src, dst, 1, _progressCb, IntPtr.Zero, CancelPtr);
            if (rc != 0) { ThrowIfCancelled(); throw Win32Errors.ToIOException(rc, src); }
        });
        if (ok) Copied++;
        else if (Failures.Count < 30) Failures.Add(dst);
        DoneFiles++;
        _bytesBase += CurrentSize;
        DoneBytes = _bytesBase;
    }

    private static long SafeLength(string p)
    {
        try { return new FileInfo(p).Length; } catch { return 0; }
    }
}
