using System.Diagnostics;
using System.Text;
using MPCommander.IO;

/// <summary>IoProbe --large-search: 수백 MB 로그 전체 찾기 (속도, 개수, 줄 번호·바이트 위치, 창 읽기, 취소)</summary>
internal static class LargeSearchTests
{
    public static Task Run()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mpc-large-search");
        Directory.CreateDirectory(dir);
        Check(dir, "big-utf8.log", new UTF8Encoding(false), 300);
        Check(dir, "big-cp949.log", Encoding.GetEncoding(949), 60);
        Check(dir, "big-utf16.log", new UnicodeEncoding(false, true), 60);
        Directory.Delete(dir, true);
        return Task.CompletedTask;
    }

    private static void Check(string dir, string name, Encoding enc, int targetMb)
    {
        var path = Path.Combine(dir, name);
        long expectMatches = 0, expectLines = 0, expectWarn = 0;
        var expectLineNos = new List<long>();
        var sw = Stopwatch.StartNew();
        using (var w = new StreamWriter(path, false, enc, 1 << 20))
        {
            long line = 0;
            var rnd = new Random(5);
            long target = (long)targetMb << 20;
            while (w.BaseStream.Length < target)
            {
                line++;
                string row = (line % 997) switch
                {
                    0 => $"2026-10-11 10:{line % 60:00}:00 [ERROR] 주문 저장 실패 order=A{line} 다시 오류 ERROR",
                    13 => $"2026-10-11 10:{line % 60:00}:00 [WARN ] 재고 부족 item=P{line}",
                    _ => $"2026-10-11 10:{line % 60:00}:00 [INFO ] 요청 처리 완료 user=u{rnd.Next(100)} 소요={rnd.Next(500)}ms 데이터 {new string('x', rnd.Next(10, 120))}",
                };
                w.Write(row + "\r\n");
                if (line % 997 == 0) { expectMatches += 2; expectLines++; expectLineNos.Add(line); }
                if (line % 997 == 13) expectWarn++;
            }
        }
        long size = new FileInfo(path).Length;
        var genSecs = sw.Elapsed.TotalSeconds;

        // BOM 확인은 앞부분으로 (뷰어와 같은 판별)
        var head = new byte[Math.Min(size, 8 << 20)];
        using (var fs = LargeTextSearch.Open(path)) fs.Read(head, 0, head.Length);
        var detected = TextMatchCount.DetectEncoding(head, out int bom);

        var hits = new List<LargeHit>();
        long matches = 0, lines = 0;
        int reports = 0;
        sw.Restart();
        LargeTextSearch.Search(path, detected, bom, "error", false, null, 100_000, 400, p =>
        {
            reports++;
            hits.AddRange(p.NewHits);
            matches = p.Matches;
            lines = p.MatchLines;
        }, CancellationToken.None);
        var secs = sw.Elapsed.TotalSeconds;
        bool linesOk = hits.Select(h => h.LineNumber).SequenceEqual(expectLineNos);
        Console.WriteLine($"[{name}] {size / 1048576.0:N0} MB ({detected.WebName}, bom {bom}) search {secs:N2}s = {size / 1048576.0 / secs:N0} MB/s, reports {reports}");
        Console.WriteLine($"  matches {matches} (expect {expectMatches}), lines {lines} (expect {expectLines}), line numbers ok {linesOk}");

        // 창 읽기: 마지막 일치 줄로 창을 옮겨 그 줄이 맞는지
        var last = hits[hits.Count - 1];
        var (text, firstLine, _, eof) = LargeTextSearch.ReadWindow(path, detected, bom, last.ByteOffset, last.LineNumber, 200, 8 << 20);
        var winLines = text.Split('\n');
        var lineText = winLines[last.LineNumber - firstLine].TrimEnd('\r');
        Console.WriteLine($"  window first line {firstLine:N0} (expect {last.LineNumber - 200:N0}), eof {eof}, target line ok {lineText.StartsWith(last.Preview.TrimEnd(' ', '…'))}");

        // 중간 일치 하나도
        var mid = hits[hits.Count / 2];
        (text, firstLine, _, _) = LargeTextSearch.ReadWindow(path, detected, bom, mid.ByteOffset, mid.LineNumber, 200, 8 << 20);
        lineText = text.Split('\n')[mid.LineNumber - firstLine].TrimEnd('\r');
        Console.WriteLine($"  middle line {mid.LineNumber:N0} ok {lineText.Contains("order=A" + mid.LineNumber)}");

        // 정규식
        sw.Restart();
        long rm = 0;
        LargeTextSearch.Search(path, detected, bom, @"order=A\d+|item=P\d+", false, TextRegex.Create(@"order=A\d+|item=P\d+", false, compiled: true),
            100_000, 400, p => rm = p.Matches, CancellationToken.None);
        Console.WriteLine($"  regex {sw.Elapsed.TotalSeconds:N2}s, matches {rm} (expect {expectLines + expectWarn})");

        // 취소: 0.2초 뒤 멈추는지
        var cts = new CancellationTokenSource(200);
        sw.Restart();
        try { LargeTextSearch.Search(path, detected, bom, "error", false, null, 100_000, 400, _ => { }, cts.Token); Console.WriteLine("  cancel: finished before cancel"); }
        catch (OperationCanceledException) { Console.WriteLine($"  cancel: stopped after {sw.ElapsedMilliseconds} ms"); }
        File.Delete(path);
    }
}
