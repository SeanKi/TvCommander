using System.Diagnostics;
using System.IO.Pipes;
using MPCommander.IO;

// 사용법: IoProbe <경로>...   각 경로를 DirectoryLoader 로 열어 결과와 걸린 시간을 출력한다.
//        IoProbe --hang      응답 없는 동기 I/O(파이프 읽기)를 GuardedIo 가 끊어내는지 확인한다.
if (args is [var mode, var remote] && (mode == "--mtp-put" || mode == "--mtp-del")) { MtpPutTest.Run(mode, remote); return; }
if (args is ["--unblock"]) { await UnblockTests.Run(); return; }
if (args is ["--7z"]) { await SevenZipTests.Run(); return; }
if (args is ["--star"]) { StarTest.Run(); return; }
if (args is ["--stall"]) { await StallTest.Run(); return; }
if (args is ["--remote"]) { await RemoteTests.Run(); return; }

if (args is ["--hang"])
{
    using var server = new NamedPipeServerStream("tvc-hang", PipeDirection.Out);
    var sw = Stopwatch.StartNew();
    try
    {
        await GuardedIo.RunAsync(_ =>
        {
            using var pipe = new NamedPipeClientStream(".", "tvc-hang", PipeDirection.In);
            pipe.Connect(1000);
            return pipe.Read(new byte[16], 0, 16);   // 서버가 아무것도 안 보내므로 영원히 대기 (동기 ReadFile)
        }, 3000, "pipe");
        Console.WriteLine("UNEXPECTED: read returned");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL  {sw.ElapsedMilliseconds,6} ms  {ex.GetType().Name}: {ex.Message}");
    }
    return;
}

if (args is ["--zip"])
{
    // 내장 ZIP 압축/풀기 점검: 한글 이름, 하위 폴더, 빈 폴더, CP949 이름 ZIP
    var work = Path.Combine(Path.GetTempPath(), "tvc-zip-test");
    if (Directory.Exists(work)) Directory.Delete(work, true);
    var src = Path.Combine(work, "자료");
    Directory.CreateDirectory(Path.Combine(src, "하위", "빈폴더"));
    var rnd = new Random(7);
    var files = new Dictionary<string, byte[]> { ["큰파일.bin"] = new byte[5_000_000], [@"하위\메모.txt"] = System.Text.Encoding.UTF8.GetBytes("안녕하세요 zip") };
    foreach (var kv in files) { if (kv.Value.Length > 100) rnd.NextBytes(kv.Value); File.WriteAllBytes(Path.Combine(src, kv.Key), kv.Value); }

    var zipPath = Path.Combine(work, "자료.zip");
    var sw = Stopwatch.StartNew();
    var pack = ZipOperation.Pack(new[] { src }, zipPath);
    await pack.RunAsync();
    Console.WriteLine($"pack: files {pack.DoneFiles}/{pack.TotalFiles}, errors {pack.Errors}, {new FileInfo(zipPath).Length:N0} B, {sw.ElapsedMilliseconds} ms, temp left: {File.Exists(zipPath + ".tvc-tmp")}");

    var outDir = Path.Combine(work, "풀기");
    var ex = ZipOperation.Extract(zipPath, outDir);
    ex.AskError = (p, m) => { Console.WriteLine($"  extract error: {m} ({p})"); return ErrorChoice.Skip; };
    await ex.RunAsync();
    Console.WriteLine($"  extracted: {string.Join(", ", Directory.EnumerateFileSystemEntries(outDir, "*", SearchOption.AllDirectories).Select(x => x.Substring(outDir.Length + 1)))}");
    bool same = files.All(f => File.ReadAllBytes(Path.Combine(outDir, "자료", f.Key)).SequenceEqual(f.Value));
    Console.WriteLine($"extract: files {ex.DoneFiles}/{ex.TotalFiles}, errors {ex.Errors}, identical {same}, empty dir kept {Directory.Exists(Path.Combine(outDir, "자료", "하위", "빈폴더"))}");

    // CP949 이름 (UTF-8 표시 없음)
    var legacyZip = Path.Combine(work, "legacy.zip");
    using (var fs = File.Create(legacyZip))
    using (var z = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create, false, System.Text.Encoding.GetEncoding(949)))
    using (var w = new StreamWriter(z.CreateEntry("한글폴더/보고서.txt").Open())) w.Write("cp949");
    var ex2 = ZipOperation.Extract(legacyZip, Path.Combine(work, "legacy"));
    await ex2.RunAsync();
    Console.WriteLine($"cp949 names decoded: {File.Exists(Path.Combine(work, "legacy", "한글폴더", "보고서.txt"))}");

    // zip slip 방지
    var evil = Path.Combine(work, "evil.zip");
    using (var fs = File.Create(evil))
    using (var z = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
    using (var w = new StreamWriter(z.CreateEntry("../../escaped.txt").Open())) w.Write("x");
    var ex3 = ZipOperation.Extract(evil, Path.Combine(work, "evil"));
    ex3.AskError = (p, m) => { Console.WriteLine($"  blocked: {p} ({m})"); return ErrorChoice.Skip; };
    await ex3.RunAsync();
    Console.WriteLine($"zip slip escaped file exists: {File.Exists(Path.Combine(Path.GetTempPath(), "escaped.txt"))}");

    // 취소하면 깨진 zip 이 안 남는다
    var cancelZip = Path.Combine(work, "cancel.zip");
    var pc = ZipOperation.Pack(new[] { src }, cancelZip);
    var run = pc.RunAsync(); pc.Cancel(); await run;
    Console.WriteLine($"cancelled pack leaves zip: {File.Exists(cancelZip)}, temp: {File.Exists(cancelZip + ".tvc-tmp")}");
    Directory.Delete(work, true);
    return;
}
if (args is ["--mtp-roundtrip", var device])
{
    // 휴대폰 왕복 점검: PC 폴더 → 폰 (FileOperation) → PC, 바이트 비교, 이름 바꾸기, 정리
    var phoneRoot = MPCommander.IO.Mtp.Prefix + device;
    var storages = await GuardedIo.RunAsync(ctx => MPCommander.IO.Mtp.List(phoneRoot, true, ctx), 10000, phoneRoot);
    var testDir = MPCommander.Model.PathUtil.Combine(MPCommander.Model.PathUtil.Combine(phoneRoot, storages[0].Name), "Download/MPCommander_test");

    var work = Path.Combine(Path.GetTempPath(), "tvc-mtp-test");
    if (Directory.Exists(work)) Directory.Delete(work, true);
    var src = Path.Combine(work, "src", "pack");
    Directory.CreateDirectory(Path.Combine(src, "sub"));
    var rnd = new Random(42);
    var files = new Dictionary<string, byte[]> { ["a.bin"] = new byte[3 * 1024 * 1024], ["한글 이름.txt"] = new byte[1234], [@"sub\c.dat"] = new byte[70_000] };
    foreach (var kv in files) { rnd.NextBytes(kv.Value); File.WriteAllBytes(Path.Combine(src, kv.Key), kv.Value); }

    async Task Run(OpKind kind, string from, string to)
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

    try
    {
        await GuardedIo.RunAsync(_ => MPCommander.IO.Mtp.EnsureFolder(testDir), 10000, testDir);
        await Run(OpKind.Copy, src, testDir + "/");                                  // PC → 폰
        var back = Path.Combine(work, "back");
        Directory.CreateDirectory(back);
        await Run(OpKind.Copy, testDir + "/pack", back + "\\");                       // 폰 → PC
        bool same = files.All(f => File.ReadAllBytes(Path.Combine(back, "pack", f.Key)).SequenceEqual(f.Value));
        Console.WriteLine($"  bytes identical after round trip: {same}");

        await Run(OpKind.Copy, src, testDir + "/");                                  // 덮어쓰기
        try
        {
            MPCommander.IO.Mtp.Rename(testDir + "/pack/a.bin", "a2.bin");
            var names = MPCommander.IO.Mtp.ListChildren(testDir + "/pack").Select(e => e.Name);
            Console.WriteLine($"  rename: {string.Join(", ", names)}");
        }
        catch (Exception ex) { Console.WriteLine($"  rename not supported: {ex.Message}"); }
    }
    finally
    {
        try { MPCommander.IO.Mtp.Delete(testDir, recursive: true); Console.WriteLine("  cleanup: test folder deleted"); }
        catch (Exception ex) { Console.WriteLine($"  cleanup FAILED: {ex.Message}"); }
        var left = MPCommander.IO.Mtp.ListChildren(MPCommander.Model.PathUtil.Parent(testDir)!).Any(e => e.Name == "MPCommander_test");
        Console.WriteLine($"  test folder still on phone: {left}");
        Directory.Delete(work, true);
    }
    return;
}
if (args is ["--mtp", ..])
{
    // 경로 도우미 점검
    string[][] cases =
    {
        new[] { "Parent", MPCommander.Model.PathUtil.Parent("mtp://Galaxy S23/내장 저장공간/DCIM") ?? "(null)", "mtp://Galaxy S23/내장 저장공간" },
        new[] { "ParentRoot", MPCommander.Model.PathUtil.Parent("mtp://Galaxy S23") ?? "(null)", "(null)" },
        new[] { "Root", MPCommander.Model.PathUtil.Root("mtp://Galaxy S23/내장 저장공간/DCIM"), "mtp://Galaxy S23" },
        new[] { "Normalize", MPCommander.Model.PathUtil.Normalize(@"mtp://Galaxy S23\내장 저장공간//DCIM/../Download/"), "mtp://Galaxy S23/내장 저장공간/Download" },
        new[] { "Relative", MPCommander.Model.PathUtil.Normalize("Camera", "mtp://Galaxy S23/내장 저장공간/DCIM"), "mtp://Galaxy S23/내장 저장공간/DCIM/Camera" },
        new[] { "Combine", MPCommander.Model.PathUtil.Combine("mtp://P/S", "a.jpg"), "mtp://P/S/a.jpg" },
        new[] { "LocalParent", MPCommander.Model.PathUtil.Parent(@"C:\a\b") ?? "(null)", @"C:\a" },
    };
    foreach (var c in cases) Console.WriteLine($"{(c[1] == c[2] ? "PASS" : "FAIL")}  {c[0]}: {c[1]}");

    var sw = Stopwatch.StartNew();
    var devices = MPCommander.IO.Mtp.ListDevices();
    Console.WriteLine($"devices: {devices.Count} ({sw.ElapsedMilliseconds} ms)");
    foreach (var d in devices)
    {
        Console.WriteLine($"  [{d.Name}]  {d.Id}");
        try
        {
            var root = MPCommander.IO.Mtp.Prefix + d.Name;
            sw.Restart();
            var storages = await GuardedIo.RunAsync(ctx => MPCommander.IO.Mtp.List(root, true, ctx), 10000, root, onTimeout: () => MPCommander.IO.Mtp.Abandon(root));
            Console.WriteLine($"    storages: {string.Join(", ", storages.Select(s => s.Name))} ({sw.ElapsedMilliseconds} ms)");
            if (storages.Count > 0)
            {
                var first = MPCommander.Model.PathUtil.Combine(root, storages[0].Name);
                sw.Restart();
                var items = await GuardedIo.RunAsync(ctx => MPCommander.IO.Mtp.List(first, true, ctx), 10000, first, onTimeout: () => MPCommander.IO.Mtp.Abandon(first));
                Console.WriteLine($"    {first}: {items.Count} items ({sw.ElapsedMilliseconds} ms)  e.g. {string.Join(", ", items.Take(6).Select(i => i.Name))}");
                Console.WriteLine($"    storage info: {MPCommander.IO.Mtp.StorageInfo(first)}");
            }
        }
        catch (Exception ex) { Console.WriteLine($"    FAIL {ex.GetType().Name}: {ex.Message}"); }
    }
    return;
}
if (args is ["--history", var iniPath])
{
    // 히스토리 저장/불러오기, 스마트 순위, 최대 개수 점검
    File.Delete(iniPath);
    var h = MPCommander.Model.DirectoryHistory.Load(iniPath);
    for (int i = 0; i < 40; i++) { h.RecordVisit($@"C:\tmp\d{i}"); h.AddRecent($@"C:\tmp\d{i}"); }
    for (int i = 0; i < 3; i++) h.RecordVisit(@"C:\work\proj_a");
    h.AddDwell(@"C:\work\proj_a", TimeSpan.FromMinutes(12));
    h.RecordVisit(@"D:\data"); h.AddDwell(@"D:\data", TimeSpan.FromHours(5));   // 1회 최대 30분으로 잘림
    h.RecordVisit(@"\\nas\share"); h.RecordVisit(@"\\nas\share");
    h.AddRecent(@"C:\work\proj_a");
    h.Save();

    var r = MPCommander.Model.DirectoryHistory.Load(iniPath);
    Console.WriteLine($"MaxHistory={r.MaxHistory} SmartCount={r.SmartCount}");
    Console.WriteLine("-- smart");
    foreach (var e in r.GetSmart()) Console.WriteLine($"  {e.Path}  visits={e.Visits} dwell={e.DwellSeconds:0}s");
    var recent = r.GetRecent(r.GetSmart().Select(e => e.Path));
    Console.WriteLine($"-- recent (smart 제외) count={recent.Count}, first={recent[0]}, last={recent[^1]}");
    return;
}

foreach (var path in args)
{
    var sw = Stopwatch.StartNew();
    try
    {
        var items = await DirectoryLoader.LoadAsync(path, showHidden: true, null, CancellationToken.None);
        Console.WriteLine($"OK    {sw.ElapsedMilliseconds,6} ms  {items.Count,8:N0} items  {path}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL  {sw.ElapsedMilliseconds,6} ms  {ex.GetType().Name}: {ex.Message}");
    }
}
