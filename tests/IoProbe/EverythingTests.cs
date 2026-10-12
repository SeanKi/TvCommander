using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using MPCommander.IO;
using MPCommander.Native;

/// <summary>IoProbe --everything: Everything IPC 질의, 검색어 만들기, 숨김 거르기, C++ 파일 목록 모드 내용 찾기</summary>
internal static class EverythingTests
{
    public static Task Run()
    {
        // Everything 의 답(WM_COPYDATA)을 받으려면 메시지 펌프가 도는 STA 스레드가 필요하다
        Exception? error = null;
        var t = new Thread(() =>
        {
            var d = Dispatcher.CurrentDispatcher;
            d.InvokeAsync(async () =>
            {
                try { await Body(); }
                catch (Exception ex) { error = ex; }
                finally { d.InvokeShutdown(); }
            });
            Dispatcher.Run();
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw error;
        return Task.CompletedTask;
    }

    private static void Ok(bool cond, string what)
    {
        Console.WriteLine((cond ? "  OK   " : "  FAIL ") + what);
        if (!cond) Environment.ExitCode = 1;
    }

    private static async Task Body()
    {
        var ver = Everything.Version();
        Console.WriteLine($"Everything version: {ver ?? "(not running)"}");
        if (ver == null) { Console.WriteLine("  SKIP: Everything is not running"); return; }

        var root = Path.Combine(Path.GetTempPath(), "mpc-everything test");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(Path.Combine(root, "sub dir"));
        var hiddenDir = Path.Combine(root, "hid");
        Directory.CreateDirectory(hiddenDir);
        File.SetAttributes(hiddenDir, FileAttributes.Directory | FileAttributes.Hidden);
        File.WriteAllText(Path.Combine(root, "a.txt"), "hello needle world");
        File.WriteAllText(Path.Combine(root, "b.log"), "needle in log");
        File.WriteAllText(Path.Combine(root, "c.txt"), "nothing here");
        File.WriteAllText(Path.Combine(root, "sub dir", "d.txt"), "deep needle");
        File.WriteAllText(Path.Combine(hiddenDir, "e.txt"), "needle hidden dir");
        var hf = Path.Combine(root, "f.txt");
        File.WriteAllText(hf, "needle hidden file");
        File.SetAttributes(hf, FileAttributes.Hidden);

        // 색인될 때까지 (보통 1초 안)
        List<EverythingItem>? all = null;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 8000)
        {
            all = await Everything.QueryAsync(Everything.BuildQuery(root, true, "*", false), 1000);
            if (all != null && all.Count >= 8) break;
            await Task.Delay(200);
        }
        Console.WriteLine($"indexed after {sw.ElapsedMilliseconds} ms: {all?.Count}");
        Ok(all is { Count: 8 }, "recursive * returns 6 files + 2 folders");

        var q = Everything.BuildQuery(root, true, "*.txt", true);
        Console.WriteLine($"query: {q}");
        var txt = await Everything.QueryAsync(q, 1000);
        Ok(txt is { Count: 5 }, $"*.txt files recursive = 5 (got {txt?.Count})");
        var a = txt!.FirstOrDefault(i => i.FullPath.EndsWith("a.txt"));
        Ok(a.Size == 18, $"size of a.txt = 18 (got {a.Size})");
        Ok(Math.Abs(DateTime.FromFileTimeUtc(a.LastWrite).Subtract(File.GetLastWriteTimeUtc(Path.Combine(root, "a.txt"))).TotalSeconds) < 2, "date modified");
        var multi = await Everything.QueryAsync(Everything.BuildQuery(root, true, "*.txt;*.log", true), 1000);
        Ok(multi is { Count: 6 }, $"*.txt;*.log = 6 (got {multi?.Count})");

        var flat = await Everything.QueryAsync(Everything.BuildQuery(root, false, "*.txt", true), 1000);
        Ok(flat is { Count: 3 }, $"non-recursive *.txt = 3 (a, c, f) (got {flat?.Count})");

        var part = await Everything.QueryAsync(Everything.BuildQuery(root, true, "*d.t*", false), 1000);
        Ok(part is { Count: 1 }, $"partial name *d.t* = 1 (got {part?.Count})");

        // C++ 파일 목록 모드: 내용 찾기
        var found = ContentSearch(txt.Select(v => v.FullPath), "needle", null);
        Ok(found.Count == 4 && !found.Any(p => p.EndsWith("c.txt")), $"C++ file-list content search: a, d, e, f (got {string.Join(", ", found.Select(Path.GetFileName))})");

        // 전체 드라이브 속도
        sw.Restart();
        var dlls = await Everything.QueryAsync(Everything.BuildQuery(null, true, "*.dll", true), 200_000);
        Console.WriteLine($"all drives *.dll: {dlls?.Count:N0} in {sw.ElapsedMilliseconds} ms");
        Ok(dlls is { Count: > 100 } && sw.ElapsedMilliseconds < 5000, "all drives *.dll under 5 s");
        Ok(dlls != null && !dlls.Any(d => d.FullPath.IndexOf(@"\$Recycle.Bin\", StringComparison.OrdinalIgnoreCase) >= 0), "no Recycle Bin items");
        sw.Restart();
        var ctxt = await Everything.QueryAsync(Everything.BuildQuery(@"C:\", true, "*.txt", true), 200_000);
        Console.WriteLine($@"C:\ *.txt: {ctxt?.Count:N0} in {sw.ElapsedMilliseconds} ms");
        Ok(ctxt is { Count: > 0 }, @"C:\ *.txt");

        Directory.Delete(root, true);
    }

    private static unsafe List<string> ContentSearch(IEnumerable<string> files, string text, System.Text.RegularExpressions.Regex? re)
    {
        var hits = new List<string>();
        FmEntryBatchCallback cb = (entries, count, _) =>
        {
            lock (hits)
                for (int i = 0; i < count; i++) hits.Add(Marshal.PtrToStringUni(entries[i].Name)!);
            return 1;
        };
        var list = Marshal.StringToHGlobalUni(string.Concat(files.Select(f => f + "\0")) + "\0");
        var p = new FmSearchParams { Root = Path.GetTempPath(), Mask = "*", Text = text, ThreadCount = 4, IoTimeoutMs = 5000, Files = list };
        var h = NativeMethods.FmSearchStart(ref p, cb, IntPtr.Zero);
        Marshal.FreeHGlobal(list);
        while (true)
        {
            NativeMethods.FmSearchGetStatus(h, out var st, null, 0);
            if (st.Running == 0) break;
            Thread.Sleep(20);
        }
        NativeMethods.FmSearchFree(h);
        GC.KeepAlive(cb);
        return hits;
    }
}
