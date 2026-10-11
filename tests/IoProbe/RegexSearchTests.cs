using System.Runtime.InteropServices;
using System.Text;
using MPCommander.IO;
using MPCommander.Native;

/// <summary>IoProbe --regex-search: 파일 찾기 '포함 텍스트' 정규식 (C++ 이 읽어 UTF-16 으로 넘긴 텍스트를 .NET 정규식으로 판정)</summary>
internal static unsafe class RegexSearchTests
{
    public static Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpc-regex-search");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        var cp949 = Encoding.GetEncoding(949);
        File.WriteAllText(Path.Combine(root, "utf8.log"), "a\r\nline 2 : 오류 code=E123\r\nend\r\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "utf8bom.log"), "x\r\n경고 WARN 끝\r\n", new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(root, "utf16.log"), "first\r\n오류 code=E777 끝\r\n", Encoding.Unicode);
        File.WriteAllBytes(Path.Combine(root, "ansi.log"), cp949.GetBytes("첫줄\r\n한글 오류 code=E949\r\n"));
        File.WriteAllText(Path.Combine(root, "none.log"), "nothing here\r\ncode=X1\r\n");
        // 5 MB 한 줄 (줄 바꿈 없이 강제로 끊는 경로) 끝에 일치
        File.WriteAllText(Path.Combine(root, "longline.log"), new string('z', 5 * 1024 * 1024) + " code=E555", new UTF8Encoding(false));

        List<string> Search(string pattern, bool matchCase, bool regex = true)
        {
            var re = TextRegex.Create(regex ? pattern : "x", matchCase, compiled: true);
            var found = new List<string>();
            int calls = 0;
            FmTextMatchCallback match = (t, len, _) => { Interlocked.Increment(ref calls); return re.IsMatch(new string(t, 0, len)) ? 1 : 0; };
            FmEntryBatchCallback hits = (e, n, _) =>
            {
                lock (found) for (int i = 0; i < n; i++) found.Add(Path.GetFileName(Marshal.PtrToStringUni(e[i].Name)!));
                return 1;
            };
            var p = new FmSearchParams
            {
                Root = root, Mask = "*", Text = pattern, CaseSensitive = matchCase ? 1 : 0, Recursive = 1, IncludeHidden = 0,
                ThreadCount = 4, IoTimeoutMs = 5000, TextMatch = regex ? Marshal.GetFunctionPointerForDelegate(match) : IntPtr.Zero,
            };
            var h = NativeMethods.FmSearchStart(ref p, hits, IntPtr.Zero);
            while (true)
            {
                NativeMethods.FmSearchGetStatus(h, out var st, null, 0);
                if (st.Running == 0) break;
                Thread.Sleep(20);
            }
            NativeMethods.FmSearchFree(h);
            GC.KeepAlive(match); GC.KeepAlive(hits);
            found.Sort(StringComparer.Ordinal);
            return found;
        }

        foreach (var (pat, cs, expect) in new[]
        {
            (@"code=E\d+", false, "ansi.log,longline.log,utf16.log,utf8.log"),
            (@"오류 code=E\d{3}", false, "ansi.log,utf16.log,utf8.log"),
            (@"^경고 WARN 끝$", false, "utf8bom.log"),
            (@"끝$", false, "utf16.log,utf8bom.log"),
            (@"warn", true, ""),
            (@"warn", false, "utf8bom.log"),
        })
        {
            var got = string.Join(",", Search(pat, cs));
            Console.WriteLine($"{(got == expect ? "OK  " : "FAIL")} {pat,-22} case={cs,-5} → {got}{(got == expect ? "" : "   expected " + expect)}");
        }
        // 정규식 끔: 예전처럼 C++ 바이트 검색 (UTF-8 / ANSI / UTF-16)
        var plain = string.Join(",", Search("오류 code=E", false, regex: false));
        Console.WriteLine($"{(plain == "ansi.log,utf16.log,utf8.log" ? "OK  " : "FAIL")} plain '오류 code=E' → {plain}");
        Directory.Delete(root, true);
        return Task.CompletedTask;
    }
}
