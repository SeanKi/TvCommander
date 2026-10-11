using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace MPCommander.IO;

/// <summary>큰 파일에서 찾은 줄 하나 (줄 번호는 1부터, 바이트 위치는 그 줄의 시작)</summary>
public sealed class LargeHit
{
    public long LineNumber;
    public long ByteOffset;
    public long FirstMatch;          // 파일 전체에서 이 줄의 첫 일치가 몇 번째인지 (0부터)
    public int Count;
    public string Preview = "";
    public readonly List<(int Off, int Len)> Offsets = new();   // 미리보기 안의 일치 위치
}

/// <summary>진행 상황 (부분 결과)</summary>
public sealed class LargeSearchProgress
{
    public List<LargeHit> NewHits = new();
    public long DoneBytes, TotalBytes, Matches, MatchLines;
    public bool Finished;
    public bool HitsTruncated;
}

/// <summary>
/// 수백 MB 텍스트 파일 전체에서 찾기 (뷰어용). UI 와 무관.
///  - 파일을 줄 경계로 끊은 4 MB 블록으로 읽고, 블록마다 여러 스레드에서 동시에 디코딩·검색한다.
///  - 블록 결과를 순서대로 합쳐 전체 줄 번호와 줄 시작 바이트 위치를 매긴다 (그 위치로 본문 창을 다시 읽는다).
///  - 파일은 읽기·쓰기·삭제 공유로 열어, 다른 프로그램이 로그를 계속 써도 된다.
/// </summary>
public static class LargeTextSearch
{
    public const int BlockBytes = 4 << 20;
    private const int ForcedCutBytes = 16 << 20;   // 줄 바꿈 없이 이만큼 쌓이면 강제로 끊는다

    private sealed class BlockResult
    {
        public readonly List<LargeHit> Hits = new();   // LineNumber: 블록 안 줄 번호(0부터), ByteOffset: 블록 안 위치, FirstMatch: 블록 안 순번
        public long Matches, MatchLines, Newlines;
        public int Bytes;
    }

    /// <summary>파일 열기 (다른 프로그램의 쓰기·삭제를 막지 않는다)</summary>
    public static FileStream Open(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);

    private static int Unit(Encoding enc) => enc is UnicodeEncoding ? 2 : 1;

    private static bool IsNewlineAt(byte[] b, int i, Encoding enc)
    {
        if (enc is UnicodeEncoding)
        {
            bool be = enc.CodePage == 1201;
            return i + 1 < b.Length && (be ? b[i] == 0 && b[i + 1] == 0x0A : b[i] == 0x0A && b[i + 1] == 0);
        }
        return b[i] == 0x0A;
    }

    /// <summary>[0, len) 에서 마지막 줄 바꿈 다음 위치 (없으면 0)</summary>
    private static int LastLineEnd(byte[] b, int len, Encoding enc)
    {
        int unit = Unit(enc);
        for (int i = (len - unit) / unit * unit; i >= 0; i -= unit)
            if (IsNewlineAt(b, i, enc)) return i + unit;
        return 0;
    }

    /// <summary>줄 바꿈 없이 너무 길 때 문자 중간을 피해 끊을 위치</summary>
    private static int SafeCut(byte[] b, int len, Encoding enc)
    {
        if (enc is UnicodeEncoding) return len & ~1;
        if (enc.CodePage == 65001)
        {
            int c = len;
            while (c > 0 && (b[c - 1] & 0xC0) == 0x80) c--;          // 이어지는 바이트
            if (c > 0 && b[c - 1] >= 0xC0) c--;                         // 잘린 문자의 첫 바이트
            return c > 0 ? c : len;
        }
        int a = len;
        while (a > 0 && b[a - 1] >= 0x80) a--;                          // ANSI 2바이트 문자: ASCII 뒤에서
        return a > 0 ? a : len;
    }

    /// <summary>
    /// 파일 전체에서 찾는다. report 는 약 0.2초마다(그리고 끝에) 작업 스레드에서 불린다.
    /// 잘못된 정규식이면 ArgumentException.
    /// </summary>
    public static void Search(string path, Encoding enc, int bomLength, string find, bool matchCase, Regex? re,
                              int maxHits, int previewChars, Action<LargeSearchProgress> report, CancellationToken ct)
    {
        using var fs = Open(path);
        long total = fs.Length;
        fs.Position = bomLength;

        int workers = Math.Max(1, Math.Min(Environment.ProcessorCount, 8));
        var pending = new Queue<(Task<BlockResult> Task, long ByteBase)>();
        long lineBase = 0, matchBase = 0, matchLines = 0, done = bomLength;
        int hitCount = 0;
        bool truncated = false;
        LargeHit? lastHit = null;
        var batch = new LargeSearchProgress { TotalBytes = total };
        var sw = Stopwatch.StartNew();

        void Merge(BlockResult r, long byteBase)
        {
            foreach (var h in r.Hits)
            {
                long line = lineBase + h.LineNumber + 1;
                // 블록 경계를 강제로 끊은 아주 긴 줄은 이어지는 블록의 같은 줄과 합친다
                if (lastHit != null && lastHit.LineNumber == line)
                {
                    lastHit.Count += h.Count;
                    continue;
                }
                if (hitCount >= maxHits) { truncated = true; continue; }
                h.LineNumber = line;
                h.ByteOffset += byteBase;
                h.FirstMatch += matchBase;
                batch.NewHits.Add(h);
                lastHit = h;
                hitCount++;
            }
            lineBase += r.Newlines;
            matchBase += r.Matches;
            matchLines += r.MatchLines;
            done += r.Bytes;
        }

        void Flush(bool finished)
        {
            batch.DoneBytes = done;
            batch.Matches = matchBase;
            batch.MatchLines = matchLines;
            batch.Finished = finished;
            batch.HitsTruncated = truncated;
            report(batch);
            batch = new LargeSearchProgress { TotalBytes = total };
            sw.Restart();
        }

        var buf = new byte[BlockBytes + ForcedCutBytes];
        int have = 0;
        long blockStart = bomLength;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int n = fs.Read(buf, have, Math.Min(BlockBytes, buf.Length - have));
            bool eof = n == 0;
            have += n;

            int cut = eof ? have : LastLineEnd(buf, have, enc);
            if (!eof && cut == 0 && have >= ForcedCutBytes) cut = SafeCut(buf, have, enc);
            if (cut > 0)
            {
                var block = new byte[cut];
                Buffer.BlockCopy(buf, 0, block, 0, cut);
                Buffer.BlockCopy(buf, cut, buf, 0, have - cut);
                have -= cut;
                long bb = blockStart;
                blockStart += cut;
                pending.Enqueue((Task.Run(() => SearchBlock(block, enc, find, matchCase, re, previewChars, ct), ct), bb));
            }

            // 순서대로 합친다 (스레드 수만큼 앞서 읽어 둔다)
            while (pending.Count > 0 && (pending.Count >= workers || eof || pending.Peek().Task.IsCompleted))
            {
                var (t, bb) = pending.Dequeue();
                Merge(t.GetAwaiter().GetResult(), bb);
                if (sw.ElapsedMilliseconds >= 200) Flush(false);
            }
            if (eof) break;
        }
        Flush(true);
    }

    private static BlockResult SearchBlock(byte[] block, Encoding enc, string find, bool matchCase, Regex? re, int previewChars, CancellationToken ct)
    {
        // 스레드 풀 스레드: 이 블록 동안만 우선순위를 낮춰 UI 가 먼저 돌게
        var thread = Thread.CurrentThread;
        var old = thread.Priority;
        thread.Priority = ThreadPriority.BelowNormal;
        try { return SearchBlockCore(block, enc, find, matchCase, re, previewChars, ct); }
        finally { thread.Priority = old; }
    }

    private static BlockResult SearchBlockCore(byte[] block, Encoding enc, string find, bool matchCase, Regex? re, int previewChars, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var r = new BlockResult { Bytes = block.Length };
        string s = enc.GetString(block);
        int lineIdx = 0, lineStart = 0, scan = 0;
        int byteCountedChar = 0;
        long byteAt = 0;
        LargeHit? last = null;
        foreach (var (pos, len) in TextMatchCount.Matches(s, find, matchCase, re))
        {
            for (int nl = s.IndexOf('\n', scan); nl >= 0 && nl < pos; nl = s.IndexOf('\n', nl + 1))
            {
                lineIdx++;
                lineStart = nl + 1;
            }
            scan = pos;
            r.Matches++;
            if (last != null && last.LineNumber == lineIdx)
            {
                last.Count++;
                if (pos - lineStart < previewChars) last.Offsets.Add((pos - lineStart, len));
                continue;
            }
            r.MatchLines++;
            byteAt += enc.GetByteCount(s.ToCharArray(byteCountedChar, lineStart - byteCountedChar));
            byteCountedChar = lineStart;
            int end = s.IndexOf('\n', lineStart);
            if (end < 0) end = s.Length;
            int lineLen = end - lineStart;
            while (lineLen > 0 && s[lineStart + lineLen - 1] == '\r') lineLen--;
            var preview = s.Substring(lineStart, Math.Min(lineLen, previewChars)).Replace('\t', ' ');
            if (lineLen > previewChars) preview += " …";
            last = new LargeHit { LineNumber = lineIdx, ByteOffset = byteAt, FirstMatch = r.Matches - 1, Count = 1, Preview = preview };
            last.Offsets.Add((pos - lineStart, len));
            r.Hits.Add(last);
            if ((r.Matches & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
        }
        // 블록의 줄 바꿈 수 (다음 블록의 줄 번호 기준)
        long count = 0;
        for (int i = s.IndexOf('\n'); i >= 0; i = s.IndexOf('\n', i + 1)) count++;
        r.Newlines = count;
        return r;
    }

    /// <summary>
    /// 본문에 보일 창 읽기: lineByte(줄 시작 바이트, 줄 번호 lineNumber 1부터) 앞에 contextLines 줄을 두고 maxBytes 까지.
    /// 반환: 텍스트, 창 첫 줄의 번호(1부터), 창 시작 바이트, 파일 끝까지 읽었는지.
    /// </summary>
    public static (string Text, long FirstLine, long StartByte, bool Eof) ReadWindow(string path, Encoding enc, int bomLength,
        long lineByte, long lineNumber, int contextLines, int maxBytes)
    {
        using var fs = Open(path);
        int unit = Unit(enc);
        long start = lineByte;
        long firstLine = lineNumber;
        if (contextLines > 0 && lineByte > bomLength)
        {
            long back = Math.Max(bomLength, lineByte - 512 * 1024);
            back -= (back - bomLength) % unit;
            var pre = new byte[lineByte - back];
            fs.Position = back;
            ReadFully(fs, pre);
            // 줄 시작 위치들 (앞에서부터)
            var starts = new List<int>();
            if (back == bomLength) starts.Add(0);
            for (int i = 0; i + unit <= pre.Length; i += unit)
                if (IsNewlineAt(pre, i, enc) && i + unit < pre.Length) starts.Add(i + unit);
            int take = Math.Min(contextLines, starts.Count);
            if (take > 0)
            {
                start = back + starts[starts.Count - take];
                firstLine = lineNumber - take;
            }
        }

        fs.Position = start;
        var buf = new byte[(int)Math.Min(maxBytes, Math.Max(0, fs.Length - start))];
        int read = ReadFully(fs, buf);
        bool eof = start + read >= fs.Length;
        int cut = read;
        if (!eof)
        {
            int le = LastLineEnd(buf, read, enc);
            cut = le > 0 ? le : SafeCut(buf, read, enc);
        }
        return (enc.GetString(buf, 0, cut), firstLine, start, eof);
    }

    private static int ReadFully(Stream s, byte[] buf)
    {
        int read = 0;
        while (read < buf.Length)
        {
            int n = s.Read(buf, read, buf.Length - read);
            if (n == 0) break;
            read += n;
        }
        return read;
    }
}
