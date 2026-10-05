using System.Diagnostics;
using MPCommander.IO;
using MPCommander.Model;

/// <summary>
/// FTP / WebDAV 점검 (IoProbe --remote). 로컬 시험 서버가 필요하다:
///   FTP     127.0.0.1:60021  test/pass
///   WebDAV  http://127.0.0.1:8088/dav  test/pass
/// 연결은 저장하지 않고 임시로만 등록한다.
/// </summary>
internal static class RemoteTests
{
    public static async Task Run()
    {
        var ftp = new RemoteConnection { Name = "tftp", Kind = RemoteKind.Ftp, Address = "127.0.0.1", Port = 60021, User = "test", Password = "pass" };
        var dav = new RemoteConnection { Name = "tdav", Kind = RemoteKind.WebDav, Address = "http://127.0.0.1:8088/dav", User = "test", Password = "pass" };
        var dead = new RemoteConnection { Name = "tdead", Kind = RemoteKind.Ftp, Address = "10.255.255.1", Port = 21 };
        var wrongPw = new RemoteConnection { Name = "tbadpw", Kind = RemoteKind.WebDav, Address = dav.Address, User = "test", Password = "nope" };
        using var r1 = RemoteConnections.Temporary(ftp);
        using var r2 = RemoteConnections.Temporary(dav);
        using var r3 = RemoteConnections.Temporary(dead);
        using var r4 = RemoteConnections.Temporary(wrongPw);

        var work = Path.Combine(Path.GetTempPath(), "mpc-remote-test");
        if (Directory.Exists(work)) Directory.Delete(work, true);
        var src = Path.Combine(work, "src", "pack");
        Directory.CreateDirectory(Path.Combine(src, "sub", "빈폴더"));
        var rnd = new Random(3);
        var files = new Dictionary<string, byte[]>
        {
            ["a.bin"] = new byte[3 * 1024 * 1024],
            ["한글 이름.txt"] = new byte[1500],
            [@"sub\c.dat"] = new byte[70_000],
        };
        foreach (var kv in files) { rnd.NextBytes(kv.Value); File.WriteAllBytes(Path.Combine(src, kv.Key), kv.Value); }

        foreach (var c in new[] { ftp, dav })
        {
            var root = c.RootPath;
            var fs = VirtualFs.For(root);
            var dir = root + "/mpc_test";
            Console.WriteLine($"== {c.KindText} ({root})");
            try
            {
                Step("list root", () => string.Join(", ", fs.List(root, null).Select(e => e.IsFolder ? e.Name + "/" : e.Name)));
                Step("ensure nested folder", () => { fs.EnsureFolder(dir + "/깊은/폴더"); return fs.GetEntry(dir + "/깊은/폴더")?.IsFolder.ToString() ?? "missing"; });
                await Copy(OpKind.Copy, src, dir + "/");
                Step("remote listing", () => string.Join(", ", fs.List(dir + "/pack", null).Select(e => $"{e.Name}{(e.IsFolder ? "/" : $" {e.Size:N0}")}")));
                var back = Path.Combine(work, "back-" + c.Scheme);
                Directory.CreateDirectory(back);
                await Copy(OpKind.Copy, dir + "/pack", back + "\\");
                bool same = files.All(f => File.ReadAllBytes(Path.Combine(back, "pack", f.Key)).SequenceEqual(f.Value));
                Console.WriteLine($"  bytes identical after round trip: {same}, empty dir kept: {Directory.Exists(Path.Combine(back, "pack", "sub", "빈폴더"))}");
                await Copy(OpKind.Copy, src, dir + "/");   // 덮어쓰기
                Step("rename a.bin -> a2.bin", () => { fs.Rename(dir + "/pack/a.bin", "a2.bin"); return string.Join(", ", fs.List(dir + "/pack", null).Select(e => e.Name)); });
                Step("GetEntry exists / missing", () => $"{fs.GetEntry(dir + "/pack/a2.bin") != null} / {fs.GetEntry(dir + "/pack/nothing.txt") != null}");
                Step("space info", () => fs.SpaceInfo(root)?.ToString() ?? "(not reported)");

                var moveFile = Path.Combine(work, "move-me.txt");
                File.WriteAllText(moveFile, "move");
                await Copy(OpKind.Move, moveFile, dir + "/");
                Console.WriteLine($"  move: local file gone {!File.Exists(moveFile)}, remote exists {fs.GetEntry(dir + "/move-me.txt") != null}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  FAIL {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine("== FTP -> WebDAV direct copy");
        await Copy(OpKind.Copy, ftp.RootPath + "/mpc_test/pack", dav.RootPath + "/mpc_test/from_ftp/");
        var cross = VirtualFs.For(dav.RootPath).List(dav.RootPath + "/mpc_test/from_ftp/pack", null);
        Console.WriteLine($"  on WebDAV: {string.Join(", ", cross.Select(e => e.Name))}");

        foreach (var c in new[] { ftp, dav })
        {
            var fs = VirtualFs.For(c.RootPath);
            Step($"{c.KindText} delete test folder", () => { fs.Delete(c.RootPath + "/mpc_test", recursive: true); return $"gone {fs.GetEntry(c.RootPath + "/mpc_test") == null}"; });
        }

        Console.WriteLine("== failures");
        await Timed("unreachable FTP server", () => VirtualFs.For(dead.RootPath).List(dead.RootPath, null).Count);
        await Timed("WebDAV wrong password", () => VirtualFs.For(wrongPw.RootPath).List(wrongPw.RootPath, null).Count);
        Directory.Delete(work, true);
    }

    private static void Step(string name, Func<string> run)
    {
        var sw = Stopwatch.StartNew();
        try { Console.WriteLine($"  {name}: {run()} ({sw.ElapsedMilliseconds} ms)"); }
        catch (Exception ex) { Console.WriteLine($"  {name}: FAIL {ex.GetType().Name}: {ex.Message}"); }
    }

    private static async Task Copy(OpKind kind, string from, string to)
    {
        var op = new FileOperation(kind, new[] { from }, to)
        {
            AskError = (p, m) => { Console.WriteLine($"    error: {m} ({p})"); return ErrorChoice.Skip; },
            AskConflict = _ => ConflictChoice.Overwrite,
        };
        var sw = Stopwatch.StartNew();
        await op.RunAsync();
        Console.WriteLine($"  {kind} {from} -> {to}: files {op.DoneFiles}/{op.TotalFiles}, {op.DoneBytes:N0} B, errors {op.Errors}, {sw.ElapsedMilliseconds} ms");
    }

    private static async Task Timed(string name, Func<int> run)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var n = await GuardedIo.RunAsync(_ => run(), 15000, name);
            Console.WriteLine($"  {name}: unexpected success {n}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  {name}: {sw.ElapsedMilliseconds} ms  {ex.GetType().Name}: {ex.Message}");
        }
    }
}

internal static class StallTest
{
    /// <summary>IoProbe --stall: TCP 는 받지만 응답이 없는 서버(127.0.0.1:60031)에서 먹통 없이 끊기는지</summary>
    public static async Task Run()
    {
        var c = new RemoteConnection { Name = "tstall", Kind = RemoteKind.Ftp, Address = "127.0.0.1", Port = 60031, User = "test", Password = "pass" };
        using var _ = RemoteConnections.Temporary(c);
        var fs = VirtualFs.For(c.RootPath);
        var sw = Stopwatch.StartNew();
        try
        {
            await GuardedIo.RunAsync(ctx => fs.List(c.RootPath, ctx).Count, fs.IdleTimeoutMs, c.RootPath, onTimeout: () => fs.Abandon(c.RootPath));
            Console.WriteLine("unexpected success");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"stalled server: {sw.ElapsedMilliseconds} ms  {ex.GetType().Name}: {ex.Message}");
        }
        sw.Restart();
        try { fs.List(c.RootPath, null); }
        catch (Exception ex) { Console.WriteLine($"retry right after (marked down): {sw.ElapsedMilliseconds} ms  {ex.GetType().Name}"); }
    }
}

internal static class StarTest
{
    /// <summary>IoProbe --star: 별표(자주 머문 폴더 올리기/내리기) 동작</summary>
    public static void Run()
    {
        var ini = Path.Combine(Path.GetTempPath(), "mpc-star-test.ini");
        File.Delete(ini);
        var h = DirectoryHistory.Load(ini);
        for (int i = 0; i < 3; i++) h.RecordVisit(@"C:\auto");          // 자동 순위 후보 (3회)
        h.RecordVisit(@"C:\once");                                       // 1회 → 후보 아님
        Show(h, "start");
        Console.WriteLine($"  toggle once -> added={h.ToggleSmart(@"C:\once")}");     // 올리기(고정)
        Console.WriteLine($"  toggle auto -> added={h.ToggleSmart(@"C:\auto")}");     // 자동 순위에서 내리기
        for (int i = 0; i < 5; i++) h.RecordVisit(@"C:\auto");          // 더 와도 다시 안 올라와야 함
        Show(h, "after toggles + 5 more visits to auto");
        h.Save();
        var r = DirectoryHistory.Load(ini);
        Show(r, "reloaded");
        Console.WriteLine($"  toggle auto again -> added={r.ToggleSmart(@"C:\auto")}");  // 다시 올리기
        Console.WriteLine($"  toggle once again -> added={r.ToggleSmart(@"C:\once")}");  // 내리기
        Show(r, "final");
        File.Delete(ini);
    }

    private static void Show(DirectoryHistory h, string label)
        => Console.WriteLine($"{label}: [{string.Join(", ", h.GetSmart().Select(e => e.Path + (e.Pinned ? " (pinned)" : "")))}]" +
                             $"  IsSmart auto={h.IsSmart(@"C:\auto")} once={h.IsSmart(@"C:\once")}");
}

internal static class MtpPutTest
{
    /// <summary>IoProbe --mtp-put remotePath / --mtp-del remotePath : 다른 프로세스에서 폰 파일 만들기/지우기 (자동 새로고침 시험용)</summary>
    public static void Run(string mode, string remote)
    {
        var fs = VirtualFs.For(remote);
        if (mode == "--mtp-put")
        {
            var tmp = Path.GetTempFileName();
            File.WriteAllText(tmp, "auto refresh test");
            fs.Upload(tmp, remote, overwrite: true, null);
            File.Delete(tmp);
        }
        else
        {
            fs.Delete(remote, recursive: false);
        }
        Console.WriteLine($"{mode} {remote}: ok");
    }
}
