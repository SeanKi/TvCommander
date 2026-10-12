using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace MPCommander.IO;

/// <summary>
/// 탐색기의 '가상 파일' 끌어 놓기·붙여넣기 (FileGroupDescriptorW + FileContents).
/// 휴대폰(MTP), zip 안의 파일, Outlook 첨부처럼 디스크 경로가 없는 항목은 FileDrop 대신 이 형식으로 온다.
/// 파일 목록은 UI 스레드에서 읽고, 내용(스트림)은 작업 스레드에서 읽는다 (데이터 개체를 그 스레드로 마샬링).
/// </summary>
public sealed class VirtualFileCopyOperation : ProgressOperation
{
    public const string DescriptorFormat = "FileGroupDescriptorW";
    private const string ContentsFormat = "FileContents";

    private const uint FD_ATTRIBUTES = 0x4, FD_WRITESTIME = 0x20, FD_FILESIZE = 0x40;
    private const int FileDescriptorSize = 592;   // FILEDESCRIPTORW
    private const uint DROPEFFECT_COPY = 1;

    public sealed record Entry(int Index, string RelativePath, bool IsDirectory, long Size, bool HasSize, DateTime? WriteTimeUtc);

    private readonly ComDataObject _ui;           // UI 스레드의 데이터 개체 (비동기 작업 알림용)
    private IntPtr _marshaled;                    // 작업 스레드로 넘길 마샬링 스트림
    private readonly bool _async;
    private readonly List<Entry> _entries;
    private readonly string _target;
    private ConflictChoice? _conflictAll;

    public override string Title => Loc.T("복사");
    public int Copied { get; private set; }
    public IReadOnlyList<Entry> Entries => _entries;

    private VirtualFileCopyOperation(ComDataObject data, List<Entry> entries, string targetDir)
    {
        _ui = data;
        _entries = entries;
        _target = targetDir;
        TotalFiles = entries.Count(e => !e.IsDirectory);
        TotalBytes = entries.Where(e => !e.IsDirectory).Sum(e => e.Size);

        // 작업 스레드에서 쓸 수 있도록 데이터 개체를 마샬링해 둔다
        var unk = Marshal.GetIUnknownForObject(data);
        try
        {
            var iid = typeof(ComDataObject).GUID;
            Marshal.ThrowExceptionForHR(CoMarshalInterThreadInterfaceInStream(ref iid, unk, out _marshaled));
        }
        finally { Marshal.Release(unk); }

        // 원본이 비동기 추출을 지원하면 알린다 (끌어 놓기가 바로 끝나고, 원본은 우리가 다 읽을 때까지 기다린다)
        if (data is IDataObjectAsyncCapability cap)
        {
            try
            {
                cap.GetAsyncMode(out var isAsync);
                if (isAsync) { cap.StartOperation(IntPtr.Zero); _async = true; }
            }
            catch { }
        }
    }

    /// <summary>이 데이터에 가상 파일이 있으면 작업을 만든다 (UI 스레드). 없으면 null</summary>
    public static VirtualFileCopyOperation? TryCreate(ComDataObject? data, string targetDir)
    {
        if (data == null) return null;
        var entries = ReadDescriptor(data);
        if (entries == null || entries.Count == 0) return null;
        return new VirtualFileCopyOperation(data, entries, targetDir);
    }

    /// <summary>WPF 데이터 개체 안의 원래 OLE 데이터 개체 (다른 프로세스의 것을 직접 써야 작업 스레드로 넘길 수 있다)</summary>
    public static ComDataObject? Native(System.Windows.IDataObject data)
    {
        try
        {
            // System.Windows.DataObject._innerData → OleConverter._innerData (ComTypes.IDataObject)
            var inner = data.GetType().GetField("_innerData", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(data);
            if (inner?.GetType().GetField("_innerData", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(inner) is ComDataObject ole)
                return ole;
        }
        catch { }
        return data as ComDataObject;
    }

    /// <summary>클립보드의 OLE 데이터 개체 (Ctrl+V)</summary>
    public static ComDataObject? FromClipboard() => OleGetClipboard(out var d) == 0 ? d : null;

    public static bool HasVirtualFiles(System.Windows.IDataObject data)
    {
        try { return data.GetDataPresent(DescriptorFormat); }
        catch { return false; }
    }

    private static List<Entry>? ReadDescriptor(ComDataObject data)
    {
        var fmt = Format(DescriptorFormat, -1, TYMED.TYMED_HGLOBAL);
        if (data.QueryGetData(ref fmt) != 0) return null;
        data.GetData(ref fmt, out var medium);
        try
        {
            var p = GlobalLock(medium.unionmember);
            if (p == IntPtr.Zero) return null;
            try
            {
                int count = Marshal.ReadInt32(p);
                var list = new List<Entry>(count);
                for (int i = 0; i < count; i++)
                {
                    var d = p + 4 + i * FileDescriptorSize;
                    uint flags = (uint)Marshal.ReadInt32(d);
                    uint attrs = (uint)Marshal.ReadInt32(d + 36);
                    long write = Marshal.ReadInt64(d + 56);
                    long size = ((long)(uint)Marshal.ReadInt32(d + 64) << 32) | (uint)Marshal.ReadInt32(d + 68);
                    var name = Marshal.PtrToStringUni(d + 72) ?? "";
                    bool dir = (flags & FD_ATTRIBUTES) != 0 && (attrs & (uint)FileAttributes.Directory) != 0;
                    list.Add(new Entry(i, name, dir, (flags & FD_FILESIZE) != 0 ? size : 0, (flags & FD_FILESIZE) != 0,
                        (flags & FD_WRITESTIME) != 0 && write > 0 ? DateTime.FromFileTimeUtc(write) : null));
                }
                return list;
            }
            finally { GlobalUnlock(medium.unionmember); }
        }
        finally { ReleaseStgMedium(ref medium); }
    }

    protected override void Execute()
    {
        int hr = 0;
        ComDataObject? data = null;
        try
        {
            var iid = typeof(ComDataObject).GUID;
            var stream = _marshaled;
            _marshaled = IntPtr.Zero;
            Marshal.ThrowExceptionForHR(CoGetInterfaceAndReleaseStream(stream, ref iid, out var obj));
            data = (ComDataObject)obj;

            Phase = Loc.T("복사 중");
            foreach (var e in _entries)
            {
                ThrowIfCancelled();
                var dest = SafeDestination(e.RelativePath);
                if (dest == null) { ReportError(e.RelativePath, Loc.T("잘못된 이름")); continue; }
                CurrentFile = e.RelativePath;
                Touch();
                if (e.IsDirectory)
                {
                    Attempt(dest, () => Directory.CreateDirectory(dest));
                    continue;
                }
                if (File.Exists(dest) && !ResolveConflict(e, dest))
                {
                    Skipped++;
                    DoneFiles++;
                    DoneBytes += e.Size;
                    continue;
                }
                long before = DoneBytes;
                if (Attempt(dest, () => { DoneBytes = before; CopyOne(data, e, dest); }))
                {
                    Copied++;
                }
                DoneFiles++;
                DoneBytes = before + e.Size;
            }
        }
        catch (OperationCanceledException) { hr = unchecked((int)0x80004004); throw; }   // E_ABORT
        catch (Exception ex) { hr = Marshal.GetHRForException(ex); throw; }
        finally
        {
            if (data != null) Marshal.ReleaseComObject(data);
            if (_async) EndAsync(hr);
        }
    }

    /// <summary>대상 폴더 밖으로 나가는 이름(.., 절대 경로)은 거른다</summary>
    private string? SafeDestination(string relative)
    {
        var rel = relative.Replace('/', '\\').TrimStart('\\');
        if (rel.Length == 0 || Path.IsPathRooted(rel) || rel.Split('\\').Any(s => s is ".." or "")) return null;
        if (rel.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
        return Path.Combine(_target, rel);
    }

    private bool ResolveConflict(Entry e, string dest)
    {
        var fi = new FileInfo(dest);
        var srcTime = e.WriteTimeUtc?.ToLocalTime() ?? DateTime.MinValue;
        var choice = _conflictAll ?? AskConflict(new ConflictInfo(e.RelativePath, dest, e.Size, srcTime, fi.Length, fi.LastWriteTime));
        Touch();
        switch (choice)
        {
            case ConflictChoice.OverwriteAll:
            case ConflictChoice.SkipAll:
            case ConflictChoice.OverwriteOlderAll:
                _conflictAll = choice;
                break;
            case ConflictChoice.Cancel:
                Cancel();
                throw new OperationCanceledException();
        }
        return choice switch
        {
            ConflictChoice.Overwrite or ConflictChoice.OverwriteAll => true,
            ConflictChoice.OverwriteOlderAll => srcTime > fi.LastWriteTime,
            _ => false,
        };
    }

    /// <summary>파일 하나: FileContents(lindex) 를 스트림(또는 메모리)으로 받아 쓴다. 실패·취소하면 쓰던 파일을 지운다</summary>
    private void CopyOne(ComDataObject data, Entry e, string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var fmt = Format(ContentsFormat, e.Index, TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL);
        data.GetData(ref fmt, out var medium);
        bool ok = false;
        try
        {
            CurrentSize = e.Size;
            CurrentDone = 0;
            using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                if (medium.tymed == TYMED.TYMED_ISTREAM)
                {
                    var stream = (IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
                    try
                    {
                        var buf = new byte[1 << 20];
                        var readPtr = Marshal.AllocHGlobal(4);
                        try
                        {
                            while (true)
                            {
                                ThrowIfCancelled();
                                stream.Read(buf, buf.Length, readPtr);
                                int n = Marshal.ReadInt32(readPtr);
                                if (n <= 0) break;
                                fs.Write(buf, 0, n);
                                CurrentDone += n;
                                DoneBytes += n;
                                Touch();
                            }
                        }
                        finally { Marshal.FreeHGlobal(readPtr); }
                    }
                    finally { Marshal.ReleaseComObject(stream); }
                }
                else if (medium.tymed == TYMED.TYMED_HGLOBAL)
                {
                    var p = GlobalLock(medium.unionmember);
                    try
                    {
                        long n = e.HasSize ? e.Size : GlobalSize(medium.unionmember).ToInt64();
                        var buf = new byte[n];
                        Marshal.Copy(p, buf, 0, (int)n);
                        fs.Write(buf, 0, buf.Length);
                        CurrentDone = n;
                        DoneBytes += n;
                    }
                    finally { GlobalUnlock(medium.unionmember); }
                }
                else throw new IOException(Loc.T("지원하지 않는 데이터 형식입니다"));
            }
            if (e.WriteTimeUtc is { } t) File.SetLastWriteTimeUtc(dest, t);
            ok = true;
        }
        finally
        {
            ReleaseStgMedium(ref medium);
            if (!ok) try { File.Delete(dest); } catch { }
        }
    }

    private void EndAsync(int hr)
    {
        try { if (_ui is IDataObjectAsyncCapability cap) cap.EndOperation(hr, IntPtr.Zero, hr == 0 ? DROPEFFECT_COPY : 0); }
        catch { }
    }

    private static FORMATETC Format(string name, int index, TYMED tymed) => new()
    {
        cfFormat = (short)RegisterClipboardFormat(name),
        dwAspect = DVASPECT.DVASPECT_CONTENT,
        lindex = index,
        ptd = IntPtr.Zero,
        tymed = tymed,
    };

    [ComImport, Guid("3D8B0590-F691-11d2-8EA9-006097DF5BD4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataObjectAsyncCapability
    {
        void SetAsyncMode([MarshalAs(UnmanagedType.Bool)] bool doOpAsync);
        void GetAsyncMode([MarshalAs(UnmanagedType.Bool)] out bool isOpAsync);
        void StartOperation(IntPtr bindCtx);
        void InOperation([MarshalAs(UnmanagedType.Bool)] out bool inAsyncOp);
        void EndOperation(int hResult, IntPtr bindCtx, uint effects);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalSize(IntPtr h);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("ole32.dll")] private static extern int OleGetClipboard(out ComDataObject data);
    [DllImport("ole32.dll")] private static extern int CoMarshalInterThreadInterfaceInStream(ref Guid iid, IntPtr unk, out IntPtr stream);
    [DllImport("ole32.dll")] private static extern int CoGetInterfaceAndReleaseStream(IntPtr stream, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
}
