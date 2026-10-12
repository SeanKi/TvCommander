using MPCommander.Native;

namespace MPCommander.IO;

/// <summary>
/// 폴더 크기(하위 폴더 포함 모든 파일의 합) 계산. C++ 검색기(여러 스레드, I/O 시간 제한 워치독)를
/// '*', 하위 폴더 포함, 숨김 포함으로 돌려 파일 크기를 더한다. 정션·심볼릭 링크는 따라가지 않는다.
/// UI 는 Poll 로 중간 합계를 읽는다.
/// </summary>
public sealed unsafe class DirSizeCalculator : IDisposable
{
    private IntPtr _handle;
    private readonly FmEntryBatchCallback _callback;   // 네이티브가 부르는 동안 살려 둔다
    private long _bytes;

    public string Path { get; }
    public long Bytes => Interlocked.Read(ref _bytes);
    public long Files { get; private set; }
    public long Folders { get; private set; }
    public long Errors { get; private set; }

    private DirSizeCalculator(string path)
    {
        Path = path;
        _callback = OnEntries;
    }

    /// <summary>계산을 시작한다. 시작하지 못하면 null</summary>
    public static DirSizeCalculator? Start(string path)
    {
        var c = new DirSizeCalculator(path);
        var p = new FmSearchParams
        {
            Root = path,
            Mask = "*",
            Recursive = 1,
            IncludeHidden = 1,
            ThreadCount = path.StartsWith(@"\\") ? 8 : 4,   // 네트워크는 지연이 커서 더 많이
            IoTimeoutMs = 5000,
        };
        c._handle = NativeMethods.FmSearchStart(ref p, c._callback, IntPtr.Zero);
        return c._handle == IntPtr.Zero ? null : c;
    }

    /// <summary>아직 계산 중이면 true. 파일·폴더·오류 수를 갱신한다</summary>
    public bool Poll()
    {
        if (_handle == IntPtr.Zero) return false;
        NativeMethods.FmSearchGetStatus(_handle, out var st, null, 0);
        Files = st.FilesScanned;
        Folders = Math.Max(0, st.DirsScanned - 1);   // 시작 폴더 자신은 빼고
        Errors = st.Errors;
        return st.Running != 0;
    }

    public void Cancel()
    {
        if (_handle != IntPtr.Zero) NativeMethods.FmSearchCancel(_handle);
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        NativeMethods.FmSearchFree(_handle);   // 이후 콜백 없음 (남은 스레드는 스스로 끝난다)
        _handle = IntPtr.Zero;
    }

    /// <summary>네이티브 검색 스레드에서 호출된다: 파일 크기만 더한다</summary>
    private int OnEntries(FmEntry* entries, int count, IntPtr user)
    {
        long sum = 0;
        for (int i = 0; i < count; i++)
            if ((entries[i].Attributes & (uint)FileAttributes.Directory) == 0)
                sum += (long)entries[i].Size;
        Interlocked.Add(ref _bytes, sum);
        return 1;
    }
}
