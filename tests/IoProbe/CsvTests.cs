using System.Text;
using MPCommander.IO;

/// <summary>IoProbe --csv: 따옴표 칸·"" ·칸 안 줄바꿈 읽기, 인코딩(UTF-8 / BOM / CP949) 알아내기, 고치지 않으면 바이트 그대로 저장, 고친 칸 저장</summary>
internal static class CsvTests
{
    private static void Ok(bool cond, string what)
    {
        Console.WriteLine((cond ? "  OK   " : "  FAIL ") + what);
        if (!cond) Environment.ExitCode = 1;
    }

    public static Task Run()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mpc-csv-test");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);

        const string sample = "이름,설명,값\r\n" +
                              "\"홍, 길동\",\"따옴표 \"\"안\"\" 글자\",10\r\n" +
                              "\"여러\r\n줄\",보통,\r\n" +
                              ",,\r\n" +
                              "짧은줄\r\n";
        var cases = new (string Name, Encoding Enc, CsvEncoding Expect)[]
        {
            ("utf8.csv", new UTF8Encoding(false), CsvEncoding.Utf8),
            ("utf8bom.csv", new UTF8Encoding(true), CsvEncoding.Utf8Bom),
            ("cp949.csv", Encoding.GetEncoding(949), CsvEncoding.Cp949),
        };
        foreach (var (name, enc, expect) in cases)
        {
            var path = Path.Combine(dir, name);
            var bytes = enc.GetPreamble().Concat(enc.GetBytes(sample)).ToArray();
            File.WriteAllBytes(path, bytes);
            var doc = CsvDocument.Load(path);
            Console.WriteLine($"[{name}]");
            Ok(doc.Encoding == expect, $"encoding {doc.Encoding} ({expect})");
            Ok(doc.Rows.Count == 5, $"rows {doc.Rows.Count} (5)");
            Ok(doc.Rows[1][0] == "홍, 길동" && doc.Rows[1][1] == "따옴표 \"안\" 글자", "quoted cell with comma and \"\"");
            Ok(doc.Rows[2][0] == "여러\r\n줄", "line break inside a quoted cell");
            Ok(doc.Rows[3].Count == 3 && doc.Rows[4].Count == 1, "empty cells and a short row kept");
            Ok(doc.NewLine == "\r\n" && doc.EndsWithNewLine, "CRLF and trailing newline");

            // 고치지 않고 저장 → 바이트가 같아야 한다
            doc.Save(path);
            Ok(File.ReadAllBytes(path).SequenceEqual(bytes), "unchanged save is byte-identical");

            // 고친 칸 저장 → 다시 읽으면 같은 값
            doc.Rows[2][1] = "새 값, \"인용\"";
            doc.Rows[4][2] = "늘어남";
            doc.Save(path);
            var again = CsvDocument.Load(path);
            Ok(again.Encoding == expect && again.Rows[2][1] == "새 값, \"인용\"" && again.Rows[4][2] == "늘어남" && again.Rows[4].Count == 3,
               "edited cells round-trip (quoted as needed, short row grew)");
        }

        // LF, 끝 줄바꿈 없음, 세미콜론
        var lf = Path.Combine(dir, "lf.csv");
        File.WriteAllText(lf, "a;b;c\n1;\"2;3\";4", new UTF8Encoding(false));
        var d2 = CsvDocument.Load(lf);
        Ok(d2.Delimiter == ';' && d2.NewLine == "\n" && !d2.EndsWithNewLine && d2.Rows[1][1] == "2;3", "semicolon, LF, no trailing newline");
        var before = File.ReadAllBytes(lf);
        d2.Save(lf);
        Ok(File.ReadAllBytes(lf).SequenceEqual(before), "LF file unchanged save is byte-identical");

        // CP949 로 나타낼 수 없는 글자
        var cp = CsvDocument.Load(Path.Combine(dir, "cp949.csv"));
        cp.Rows[0][0] = "emoji 😀";
        bool refused = false;
        try { cp.Save(Path.Combine(dir, "cp949.csv")); } catch (EncoderFallbackException) { refused = true; }
        Ok(refused && CsvDocument.Load(Path.Combine(dir, "cp949.csv")).Rows[0][0] == "이름", "CP949 save refuses unsupported chars and keeps the file");

        // 큰 파일 속도
        var big = Path.Combine(dir, "big.csv");
        var sb = new StringBuilder();
        for (int i = 0; i < 200_000; i++) sb.Append(i).Append(",\"name ").Append(i).Append("\",값 ").Append(i % 97).Append(",2026-10-17\r\n");
        File.WriteAllText(big, sb.ToString(), new UTF8Encoding(false));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var bd = CsvDocument.Load(big);
        Console.WriteLine($"  big.csv {new FileInfo(big).Length / 1024 / 1024} MB, {bd.Rows.Count:N0} rows loaded in {sw.ElapsedMilliseconds} ms");
        Ok(bd.Rows.Count == 200_000, "big file rows");

        Directory.Delete(dir, true);
        return Task.CompletedTask;
    }
}
