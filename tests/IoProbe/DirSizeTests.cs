using System.Diagnostics;
using MPCommander.IO;

/// <summary>IoProbe --dir-size: Space 폴더 크기 계산이 .NET 으로 센 합과 같은지, 취소되는지</summary>
internal static class DirSizeTests
{
    public static Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpc-dir-size");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var rnd = new Random(3);
        long expect = 0;
        for (int d = 0; d < 20; d++)
        {
            var dir = Path.Combine(root, $"d{d}", $"sub{d % 3}");
            Directory.CreateDirectory(dir);
            for (int f = 0; f < 50; f++)
            {
                int size = rnd.Next(0, 200_000);
                File.WriteAllBytes(Path.Combine(dir, $"f{f}.bin"), new byte[size]);
                expect += size;
            }
        }
        var hidden = Path.Combine(root, "hidden.bin");
        File.WriteAllBytes(hidden, new byte[12345]);
        File.SetAttributes(hidden, FileAttributes.Hidden);
        expect += 12345;

        Check(root, expect);
        var repo = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\.."));
        Check(repo, DotNetSum(repo));

        // 큰 폴더: 시간과 취소
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var sw = Stopwatch.StartNew();
        using (var c = DirSizeCalculator.Start(sys)!)
        {
            Thread.Sleep(300);
            c.Cancel();
            while (c.Poll()) Thread.Sleep(20);
            Console.WriteLine($"  OK   cancel {sys} after 300 ms: stopped in {sw.ElapsedMilliseconds} ms ({c.Bytes:N0} B so far)");
        }
        sw.Restart();
        using (var c = DirSizeCalculator.Start(sys)!)
        {
            while (c.Poll()) Thread.Sleep(50);
            Console.WriteLine($"  {sys}: {c.Bytes:N0} B, files {c.Files:N0}, folders {c.Folders:N0}, errors {c.Errors:N0} in {sw.ElapsedMilliseconds} ms");
        }
        Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private static void Check(string path, long expect)
    {
        var sw = Stopwatch.StartNew();
        using var c = DirSizeCalculator.Start(path)!;
        while (c.Poll()) Thread.Sleep(10);
        bool ok = c.Bytes == expect;
        Console.WriteLine($"  {(ok ? "OK  " : "FAIL")} {path}: {c.Bytes:N0} B (expected {expect:N0}), files {c.Files:N0}, folders {c.Folders:N0} in {sw.ElapsedMilliseconds} ms");
        if (!ok) Environment.ExitCode = 1;
    }

    /// <summary>비교용: .NET 으로 모든 파일 크기 합 (정션은 따라가지 않음)</summary>
    private static long DotNetSum(string dir)
    {
        long sum = 0;
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles()) sum += f.Length;
        foreach (var d in new DirectoryInfo(dir).EnumerateDirectories())
            if ((d.Attributes & FileAttributes.ReparsePoint) == 0) sum += DotNetSum(d.FullName);
        return sum;
    }
}
