using MPCommander.IO;

/// <summary>IoProbe --7z: 7-Zip 압축 → 풀기 왕복, 한글 이름, 진행률, 취소, ZIP 수준</summary>
internal static class SevenZipTests
{
    public static async Task Run()
    {
        Console.WriteLine($"7-Zip: {SevenZip.Exe ?? "(없음)"}");
        if (SevenZip.Exe == null) return;

        var root = Path.Combine(Path.GetTempPath(), "mpc-7z-test");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var src = Path.Combine(root, "src");
        Directory.CreateDirectory(Path.Combine(src, "폴더 하나", "깊은"));
        File.WriteAllText(Path.Combine(src, "한글 파일.txt"), "안녕하세요 7z");
        File.WriteAllText(Path.Combine(src, "폴더 하나", "깊은", "a b.txt"), new string('x', 5000));
        var big = Path.Combine(src, "big.bin");
        var rnd = new Random(1);
        var buf = new byte[1 << 20];
        using (var fs = File.Create(big)) for (int i = 0; i < 40; i++) { rnd.NextBytes(buf); fs.Write(buf, 0, buf.Length); }
        var outDir = Path.Combine(root, "out");
        Directory.CreateDirectory(outDir);

        // 1) 압축 (진행률 확인)
        var archive = Path.Combine(outDir, "결과.7z");
        var items = new[] { Path.Combine(src, "한글 파일.txt"), Path.Combine(src, "폴더 하나"), big };
        var op = SevenZipOperation.Pack(items, archive, 1);
        var seen = new HashSet<long>();
        var task = op.RunAsync();
        while (!task.IsCompleted) { seen.Add(op.DoneBytes * 100 / Math.Max(1, op.TotalBytes)); await Task.Delay(20); }
        await task;
        Console.WriteLine($"[pack] exists {File.Exists(archive)}, size {new FileInfo(archive).Length:N0}, files {op.DoneFiles}/{op.TotalFiles}, errors {op.Errors}, " +
                          $"progress steps seen {seen.Count}, temp left {File.Exists(archive + ".tvc-tmp")}");

        // 2) 풀기 → 내용 비교
        var ex = Path.Combine(outDir, "풀기");
        var op2 = SevenZipOperation.Extract(archive, ex, overwrite: true);
        await op2.RunAsync();
        bool same = File.ReadAllText(Path.Combine(ex, "한글 파일.txt")) == "안녕하세요 7z"
                    && File.ReadAllText(Path.Combine(ex, "폴더 하나", "깊은", "a b.txt")).Length == 5000
                    && new FileInfo(Path.Combine(ex, "big.bin")).Length == 40 << 20;
        Console.WriteLine($"[extract] round-trip ok {same}, errors {op2.Errors}");

        // 3) 덮어쓰지 않기: 바꾼 파일이 그대로 남아야
        File.WriteAllText(Path.Combine(ex, "한글 파일.txt"), "changed");
        await SevenZipOperation.Extract(archive, ex, overwrite: false).RunAsync();
        Console.WriteLine($"[extract skip] kept existing {File.ReadAllText(Path.Combine(ex, "한글 파일.txt")) == "changed"}");

        // 4) 취소: 최고 압축 중 취소 → 결과 파일·임시 파일 없음
        var archive2 = Path.Combine(outDir, "cancel.7z");
        var op3 = SevenZipOperation.Pack(items, archive2, 3);
        var t3 = op3.RunAsync();
        await Task.Delay(300);
        op3.Cancel();
        try { await t3; } catch (Exception e) { Console.WriteLine($"  cancel threw {e.GetType().Name}: {e.Message}"); }
        await Task.Delay(300);
        Console.WriteLine($"[cancel] cancelled {op3.IsCancelled}, archive {File.Exists(archive2)}, temp {File.Exists(archive2 + ".tvc-tmp")}");

        // 5) 내장 ZIP 수준: 저장 < 보통 크기
        var z0 = Path.Combine(outDir, "store.zip");
        var z2 = Path.Combine(outDir, "normal.zip");
        var text = Path.Combine(src, "폴더 하나");
        await ZipOperation.Pack([text], z0, 0).RunAsync();
        await ZipOperation.Pack([text], z2, 2).RunAsync();
        Console.WriteLine($"[zip level] store {new FileInfo(z0).Length:N0} > normal {new FileInfo(z2).Length:N0}: {new FileInfo(z0).Length > new FileInfo(z2).Length}");

        // 6) 폴더 내용만: '폴더 하나' 안의 항목들을 넘기면 압축 안 경로가 '깊은\...' 으로 시작
        var inner = Directory.EnumerateFileSystemEntries(Path.Combine(src, "폴더 하나")).ToList();
        var c7 = Path.Combine(outDir, "contents.7z");
        var cz = Path.Combine(outDir, "contents.zip");
        await SevenZipOperation.Pack(inner, c7, 2).RunAsync();
        await ZipOperation.Pack(inner, cz, 2).RunAsync();
        var ex7 = Path.Combine(outDir, "c7");
        await SevenZipOperation.Extract(c7, ex7, true).RunAsync();
        using (var z = System.IO.Compression.ZipFile.OpenRead(cz))
            Console.WriteLine($"[contents only] 7z top: {string.Join(",", Directory.EnumerateFileSystemEntries(ex7).Select(Path.GetFileName))}" +
                              $"  zip entries: {string.Join(",", z.Entries.Select(e => e.FullName))}   (expect 깊은, 깊은/, 깊은/a b.txt)");

        Directory.Delete(root, true);
    }
}
