using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using MPCommander.Native;

namespace MPCommander.IO;

/// <summary>
/// 뷰어의 텍스트 원본: C++ FmText 가 백그라운드로 줄 색인을 만들고, 여기서는 보이는 줄만 블록(256줄) 단위로 읽어 캐시한다.
///  - 화면 그리기(UI 스레드)는 TryGetLine 으로 캐시에 있는 것만 바로 쓰고, 없으면 읽기 스레드에 맡긴 뒤 BlockLoaded 로 다시 그린다.
///    → 느린 네트워크 파일이어도 화면이 멈추지 않는다.
///  - 찾기 결과 확인처럼 백그라운드에서 쓰는 GetLineSync 는 바로 읽는다.
/// </summary>
internal sealed unsafe class TextFileSource : IDisposable
{
    public const int BlockLines = 256;
    private const int MaxCachedBlocks = 96;
    private const int MaxLineChars = 1_000_000;   // 한 줄이 이보다 길면 잘라서 보여 준다

    private IntPtr _h;
    private readonly object _cacheLock = new();
    private readonly Dictionary<long, string[]> _cache = new();
    private readonly LinkedList<long> _lru = new();
    private readonly BlockingCollection<long> _requests = new(new ConcurrentQueue<long>());
    private readonly HashSet<long> _requested = new();
    private readonly Thread _loader;
    private volatile bool _disposed;
    // 네이티브 핸들 보호: 읽기는 읽기 잠금, 닫기는 쓰기 잠금 (읽는 중에 핸들이 풀리지 않게)
    private readonly ReaderWriterLockSlim _rw = new(LockRecursionPolicy.SupportsRecursion);

    private T Native<T>(Func<IntPtr, T> f, T fallback)
    {
        if (_disposed || !_rw.TryEnterReadLock(0) && !_rw.TryEnterReadLock(5000)) return fallback;
        try { return _disposed || _h == IntPtr.Zero ? fallback : f(_h); }
        finally { _rw.ExitReadLock(); }
    }

    public string Path { get; }
    public Encoding Encoding { get; }
    public int BomLength { get; }
    public string EncodingName => Encoding.WebName.ToUpperInvariant();

    /// <summary>블록을 읽어 왔다 (작업 스레드에서 불림)</summary>
    public event Action? BlockLoaded;

    private TextFileSource(string path, Encoding enc, int bom, IntPtr h)
    {
        Path = path;
        Encoding = enc;
        BomLength = bom;
        _h = h;
        _loader = new Thread(LoadLoop) { IsBackground = true, Name = "TextFileSource" };
        _loader.Start();
    }

    /// <summary>앞부분으로 인코딩을 판별하고 색인을 시작한다 (작업 스레드에서 부를 것 — 파일을 연다)</summary>
    public static TextFileSource Open(string path)
    {
        byte[] head;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
        {
            head = new byte[(int)Math.Min(fs.Length, 1 << 20)];
            int read = 0;
            while (read < head.Length) { int n = fs.Read(head, read, head.Length - read); if (n == 0) break; read += n; }
        }
        var enc = TextMatchCount.DetectEncoding(head, out int bom);
        int unit = enc is UnicodeEncoding ? 2 : 1;
        int be = enc.CodePage == 1201 ? 1 : 0;
        var h = NativeMethods.FmTextOpen(path, unit, be, bom);
        if (h == IntPtr.Zero) throw new IOException($"파일을 열 수 없습니다: {path}");
        return new TextFileSource(path, enc, bom, h);
    }

    public FmTextStatus Status
    {
        get
        {
            return Native(h => { NativeMethods.FmTextGetStatus(h, out var st); return st; }, default(FmTextStatus));
        }
    }

    /// <summary>캐시에 있으면 그 줄, 없으면 null (읽기를 요청해 둔다)</summary>
    public string? TryGetLine(long line)
    {
        long block = line / BlockLines;
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(block, out var lines))
            {
                Touch(block);
                int i = (int)(line - block * BlockLines);
                return i < lines.Length ? lines[i] : "";
            }
            if (_requested.Add(block)) _requests.Add(block);
        }
        return null;
    }

    /// <summary>바로 읽기 (백그라운드에서 쓸 것)</summary>
    public string GetLineSync(long line)
    {
        long block = line / BlockLines;
        string[] lines;
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(block, out var cached)) { Touch(block); lines = cached; goto have; }
        }
        lines = ReadBlock(block);
        Store(block, lines);
        have:
        int i = (int)(line - block * BlockLines);
        return i < lines.Length ? lines[i] : "";
    }

    /// <summary>바이트 위치 → 줄 번호 (0부터)</summary>
    public long LineOfOffset(long offset) => Native(h => NativeMethods.FmTextLineOfOffset(h, offset), -1L);

    /// <summary>[first, first+count) 줄을 그대로 이어 붙인 텍스트 (복사용, 백그라운드에서)</summary>
    public string GetRangeText(long first, int count, int maxChars)
    {
        var sb = new StringBuilder();
        for (long l = first; l < first + count && sb.Length < maxChars; l++)
        {
            if (sb.Length > 0) sb.Append("\r\n");
            sb.Append(GetLineSync(l));
        }
        return sb.Length > maxChars ? sb.ToString(0, maxChars) : sb.ToString();
    }

    private void LoadLoop()
    {
        try
        {
            foreach (var block in _requests.GetConsumingEnumerable())
            {
                if (_disposed) return;
                string[] lines;
                try { lines = ReadBlock(block); }
                catch { lines = []; }
                if (lines.Length == 0)
                {
                    // 아직 색인되지 않은 블록 — 나중에 다시 요청되게
                    lock (_cacheLock) _requested.Remove(block);
                    continue;
                }
                Store(block, lines);
                lock (_cacheLock) _requested.Remove(block);
                BlockLoaded?.Invoke();
            }
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private string[] ReadBlock(long block) => Native(_ => ReadBlockCore(block), Array.Empty<string>());

    private string[] ReadBlockCore(long block)
    {
        var offsets = new long[BlockLines + 1];
        int n;
        fixed (long* po = offsets) n = NativeMethods.FmTextLineOffsets(_h, block * BlockLines, BlockLines, po);
        if (n <= 0) return [];
        long start = offsets[0], end = offsets[n];
        // 아주 긴 줄이 섞인 블록은 줄마다 따로 읽는다
        if (end - start > 64L << 20)
        {
            var each = new string[n];
            for (int i = 0; i < n; i++) each[i] = ReadLine(offsets[i], offsets[i + 1]);
            return each;
        }
        var bytes = new byte[end - start];
        int read;
        fixed (byte* pb = bytes) read = NativeMethods.FmTextRead(_h, start, bytes.Length, pb);
        if (read < 0) throw new IOException(new System.ComponentModel.Win32Exception(-read).Message);
        var lines = new string[n];
        for (int i = 0; i < n; i++)
        {
            int a = (int)(offsets[i] - start), b = (int)Math.Min(read, offsets[i + 1] - start);
            lines[i] = Decode(bytes, a, Math.Max(0, b - a));
        }
        return lines;
    }

    private string ReadLine(long from, long to)
    {
        int len = (int)Math.Min(to - from, (long)MaxLineChars * 4);
        var bytes = new byte[len];
        int read;
        fixed (byte* pb = bytes) read = NativeMethods.FmTextRead(_h, from, len, pb);
        return read <= 0 ? "" : Decode(bytes, 0, read);
    }

    private string Decode(byte[] b, int offset, int count)
    {
        var s = Encoding.GetString(b, offset, count);
        int len = s.Length;
        while (len > 0 && (s[len - 1] == '\n' || s[len - 1] == '\r')) len--;
        if (len > MaxLineChars) return s.Substring(0, MaxLineChars) + " …(줄이 너무 길어 잘림)";
        return len == s.Length ? s : s.Substring(0, len);
    }

    private void Store(long block, string[] lines)
    {
        lock (_cacheLock)
        {
            if (_cache.ContainsKey(block)) return;
            _cache[block] = lines;
            _lru.AddFirst(block);
            while (_lru.Count > MaxCachedBlocks)
            {
                var old = _lru.Last!.Value;
                _lru.RemoveLast();
                _cache.Remove(old);
            }
        }
    }

    private void Touch(long block)
    {
        var node = _lru.Find(block);
        if (node != null && node != _lru.First) { _lru.Remove(node); _lru.AddFirst(node); }
    }

    // ───────── 찾기 (C++, 일반 문자열) ─────────

    /// <summary>
    /// 파일 전체에서 일반 문자열을 찾는다 (C++ 바이트 검색). onHits 는 C++ 작업 스레드에서 (줄 번호, 일치 수) 묶음으로 불린다.
    /// 대소문자 무시는 ASCII 기준으로 후보를 고르므로, 받는 쪽에서 줄을 디코딩해 다시 확인한다.
    /// </summary>
    public TextSearchHandle StartSearch(string find, bool matchCase, Func<long[], int[], long, bool> onHits)
    {
        var pattern = Encoding.GetBytes(find);
        FmTextHitCallback cb = (lines, counts, n, done, _) =>
        {
            var l = new long[n];
            var c = new int[n];
            for (int i = 0; i < n; i++) { l[i] = lines[i]; c[i] = counts[i]; }
            try { return onHits(l, c, done) ? 1 : 0; }
            catch { return 0; }
        };
        IntPtr h = Native(th => { fixed (byte* pp = pattern) return NativeMethods.FmTextSearchStart(th, pp, pattern.Length, matchCase ? 0 : 1, cb, IntPtr.Zero); }, IntPtr.Zero);
        return new TextSearchHandle(h, cb);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _requests.CompleteAdding();
        // 읽는 중인 작업이 끝나기를 잠깐 기다린다. 느린 네트워크로 오래 걸리면 핸들은 놓아 둔다 (작은 누수 > 충돌)
        if (_rw.TryEnterWriteLock(2000))
        {
            try
            {
                var h = _h;
                _h = IntPtr.Zero;
                if (h != IntPtr.Zero) NativeMethods.FmTextClose(h);
            }
            finally { _rw.ExitWriteLock(); }
        }
    }
}

/// <summary>C++ 찾기 작업. Dispose 하면 취소하고 이후 콜백은 오지 않는다.</summary>
internal sealed class TextSearchHandle : IDisposable
{
    private static readonly List<object> KeepAlive = new();   // 버려진 네이티브 스레드가 늦게 불러도 안전하게
    private IntPtr _h;

    internal TextSearchHandle(IntPtr h, Delegate cb)
    {
        _h = h;
        lock (KeepAlive) KeepAlive.Add(cb);
    }

    public bool IsValid => _h != IntPtr.Zero;

    public (long DoneBytes, bool Done) Progress
    {
        get
        {
            if (_h == IntPtr.Zero) return (0, true);
            NativeMethods.FmTextSearchGetStatus(_h, out long done, out int fin);
            return (done, fin != 0);
        }
    }

    public void Dispose()
    {
        var h = _h;
        _h = IntPtr.Zero;
        if (h != IntPtr.Zero) NativeMethods.FmTextSearchFree(h);
    }
}
