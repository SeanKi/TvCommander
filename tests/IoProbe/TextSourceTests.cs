using System.Diagnostics;
using System.Text;
using MPCommander.IO;

/// <summary>IoProbe --text-source: C++ 줄 색인 + 블록 읽기 + C++ 찾기 (첫 화면까지 시간, 색인 속도, 임의 줄 내용, 찾기 정확도·속도)</summary>
internal static class TextSourceTests
{
    public static Task Run()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mpc-text-source");
        Directory.CreateDirectory(dir);
        Check(dir, "s-utf8.log", new UTF8Encoding(false), 300);
        Check(dir, "s-cp949.log", Encoding.GetEncoding(949), 60);
        Check(dir, "s-utf16.log", new UnicodeEncoding(false, true), 60);
        try { Directory.Delete(dir, true); } catch (Exception ex) { Console.WriteLine("cleanup: " + ex.Message); }
        Console.WriteLine("END");
        return Task.CompletedTask;
    }

    private static string Row(long line, Random rnd) => (line % 997) switch
    {
        0 => $"{line} [ERROR] 주문 저장 실패 order=A{line} 다시 Error 오류",
        13 => $"{line} [WARN ] 재고 부족 item=P{line}",
        _ => $"{line} [INFO ] 요청 처리 완료 user=u{rnd.Next(100)} 데이터 {new string('x', rnd.Next(10, 120))}",
    };

    private static void Check(string dir, string name, Encoding enc, int targetMb)
    {
        var path = Path.Combine(dir, name);
        long lines = 0, expectLines = 0;
        using (var w = new StreamWriter(path, false, enc, 1 << 20))
        {
            var rnd = new Random(5);
            long target = (long)targetMb << 20;
            while (w.BaseStream.Length < target) { lines++; w.Write(Row(lines - 1, rnd) + "\r\n"); if ((lines - 1) % 997 == 0) expectLines++; }
        }
        long size = new FileInfo(path).Length;

        var sw = Stopwatch.StartNew();
        using var src = TextFileSource.Open(path);
        while (src.Status.LineCount < 50) Thread.Sleep(1);
        var first = src.GetLineSync(0);
        var firstMs = sw.ElapsedMilliseconds;
        while (src.Status.Done == 0) Thread.Sleep(5);
        var indexMs = sw.ElapsedMilliseconds;
        var st = src.Status;

        // 임의 줄 내용 (줄 번호로 시작)
        var rnd2 = new Random(1);
        bool contentOk = true;
        var sw2 = Stopwatch.StartNew();
        for (int i = 0; i < 200; i++)
        {
            long l = (long)(rnd2.NextDouble() * (lines - 1));
            if (!src.GetLineSync(l).StartsWith(l + " [")) { contentOk = false; Console.WriteLine($"  line {l}: '{src.GetLineSync(l)}'"); break; }
        }
        bool lastOk = src.GetLineSync(lines - 1).StartsWith((lines - 1) + " [");
        Console.WriteLine($"[{name}] {size / 1048576.0:N0} MB {src.EncodingName}: first lines {firstMs} ms ('{first.Substring(0, Math.Min(20, first.Length))}'), index {indexMs} ms = {size / 1048576.0 / (indexMs / 1000.0):N0} MB/s");
        Console.WriteLine($"  line count {st.LineCount:N0} (expect {lines:N0}), 200 random lines ok {contentOk} ({sw2.ElapsedMilliseconds} ms), last line ok {lastOk}");

        // C++ 찾기: 대소문자 무시 "error" → ERROR 줄마다 [ERROR] + Error = 2개
        foreach (var (find, matchCase, expectPerLine) in new[] { ("error", false, 2), ("ERROR", true, 1), ("주문 저장", false, 1) })
        {
            long found = 0, foundLines = 0;
            bool linesOk = true;
            var done = new ManualResetEventSlim();
            sw.Restart();
            using var h = src.StartSearch(find, matchCase, (ls, cs, doneBytes) =>
            {
                for (int i = 0; i < ls.Length; i++) { foundLines++; found += cs[i]; if (ls[i] % 997 != 0) linesOk = false; }
                return true;
            });
            while (!h.Progress.Done) Thread.Sleep(5);
            Console.WriteLine($"  find '{find}' case={matchCase}: {sw.ElapsedMilliseconds} ms = {size / 1048576.0 / (sw.ElapsedMilliseconds / 1000.0):N0} MB/s, lines {foundLines:N0} (expect {expectLines:N0}), matches {found:N0} (expect {expectLines * expectPerLine:N0}), line numbers ok {linesOk}");
        }

        // 취소
        sw.Restart();
        var hc = src.StartSearch("x", false, (_, _, _) => true);
        Thread.Sleep(30);
        hc.Dispose();
        Console.WriteLine($"  cancel: released after {sw.ElapsedMilliseconds} ms");
        src.Dispose();
        Thread.Sleep(200);
        File.Delete(path);
    }
}
