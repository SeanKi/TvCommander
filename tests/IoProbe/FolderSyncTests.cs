using MPCommander.IO;

/// <summary>IoProbe --folder-sync: 두 폴더 비교(분류·내용 비교·폴더 묶음), 조언, 방식별 계획과 실행 결과</summary>
internal static class FolderSyncTests
{
    private static void Ok(bool cond, string what)
    {
        Console.WriteLine((cond ? "  OK   " : "  FAIL ") + what);
        if (!cond) Environment.ExitCode = 1;
    }

    private static string Root => Path.Combine(Path.GetTempPath(), "mpc-sync-test");

    /// <summary>A·B 를 새로 만든다. 같음 / A 에만 / B 에만 / A 최신 / B 최신 / 날짜 같고 내용 다름 / 내용만 같음 / 한쪽에만 있는 폴더</summary>
    private static (string A, string B) Make()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
        string a = Path.Combine(Root, "A"), b = Path.Combine(Root, "B");
        var t0 = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
        void W(string dir, string rel, string text, DateTime t)
        {
            var p = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
            File.SetLastWriteTimeUtc(p, t);
        }
        W(a, "same.txt", "same", t0); W(b, "same.txt", "same", t0);
        W(a, "onlyA.txt", "a", t0);
        W(b, @"sub\onlyB.txt", "b", t0); Directory.CreateDirectory(Path.Combine(a, "sub"));
        W(a, "newerA.txt", "new A", t0.AddHours(2)); W(b, "newerA.txt", "old", t0);
        W(a, "newerB.txt", "old", t0); W(b, "newerB.txt", "new B!", t0.AddHours(3));
        W(a, "conflict.txt", "xxxx", t0); W(b, "conflict.txt", "yyyyyy", t0);
        W(a, "touched.txt", "identical", t0); W(b, "touched.txt", "identical", t0.AddDays(1));   // 내용 같고 날짜만 다름
        W(a, @"dirA\x\1.txt", "1", t0); W(a, @"dirA\2.txt", "22", t0);                          // A 에만 있는 폴더
        W(b, @"dirB\3.txt", "333", t0);                                                           // B 에만 있는 폴더
        W(a, @".git\HEAD", "ref", t0);                                                            // 제외
        return (a, b);
    }

    private static FolderCompare Cmp(string a, string b) => FolderCompare.Run(a, b, [".git"], true, null, CancellationToken.None);

    private static async Task Exec(FolderCompare r)
    {
        var op = new FolderSyncOperation(r.A, r.B, r.Entries);
        await op.RunAsync();
        Console.WriteLine($"    run: copied {op.Copied}, deleted {op.Deleted}, errors {op.Errors}");
    }

    public static async Task Run()
    {
        var (a, b) = Make();
        var r = Cmp(a, b);
        string Kinds() => string.Join(", ", r.Entries.Select(e => $"{e.DisplayPath}={e.Diff}"));
        Console.WriteLine("  entries: " + Kinds());
        Ok(r.Entries.Count == 7, $"7 differences (got {r.Entries.Count})");
        SyncDiff? K(string rel) => r.Entries.FirstOrDefault(e => e.Relative == rel)?.Diff;
        Ok(K("onlyA.txt") == SyncDiff.OnlyA && K(@"sub\onlyB.txt") == SyncDiff.OnlyB, "only-in-A / only-in-B files");
        Ok(K("newerA.txt") == SyncDiff.NewerA && K("newerB.txt") == SyncDiff.NewerB, "newer on each side");
        Ok(K("conflict.txt") == SyncDiff.Conflict, "same time, different size = conflict");
        Ok(K("touched.txt") == null && r.ContentSame == 1, "same content with a different date counts as same");
        Ok(K("dirA") == SyncDiff.OnlyA && r.Entries.First(e => e.Relative == "dirA").A!.Files == 2 && K(@"dirA\2.txt") == null, "folder only in A is one entry (2 files)");
        Ok(K("dirB") == SyncDiff.OnlyB, "folder only in B");
        Ok(!r.Entries.Any(e => e.Relative.StartsWith(".git")), ".git excluded");
        var (advice, suggest) = r.Advice();
        Console.WriteLine("  advice: " + advice.Replace("\n", " / "));
        Ok(suggest == SyncMode.Both, "both sides have unique/newer files → suggest 'both'");

        // 1) 양쪽 똑같이: 지우지 않고, 충돌만 남는다
        r.Plan(SyncMode.Both);
        await Exec(r);
        var after = Cmp(a, b);
        Ok(after.Entries.Count == 1 && after.Entries[0].Diff == SyncDiff.Conflict, $"Both: only the conflict remains (got {after.Entries.Count})");
        Ok(File.ReadAllText(Path.Combine(b, "newerA.txt")) == "new A" && File.ReadAllText(Path.Combine(a, "newerB.txt")) == "new B!", "Both: newer side won");

        // 2) A → B 최신만
        (a, b) = Make();
        r = Cmp(a, b); r.Plan(SyncMode.AtoB);
        await Exec(r);
        Ok(File.Exists(Path.Combine(b, "onlyA.txt")) && File.Exists(Path.Combine(b, @"dirA\x\1.txt")) && File.ReadAllText(Path.Combine(b, "newerA.txt")) == "new A", "AtoB: missing and newer copied to B");
        Ok(File.ReadAllText(Path.Combine(b, "newerB.txt")) == "new B!" && File.Exists(Path.Combine(b, @"dirB\3.txt")), "AtoB: B's newer and B-only files kept");
        Ok(!File.Exists(Path.Combine(a, @"sub\onlyB.txt")), "AtoB: nothing copied to A");

        // 3) 한쪽만 남기기 (A 를 남김 → B 를 A 와 똑같이). 조언: B 가 최신인 것이 있어 잃는다
        (a, b) = Make();
        r = Cmp(a, b); r.Plan(SyncMode.MirrorA);
        Ok(r.Entries.First(e => e.Relative == "newerB.txt").Note.Length > 0, "MirrorA warns that B's newer file is overwritten");
        await Exec(r);
        var m = Cmp(a, b);
        Ok(m.Entries.Count == 0, $"MirrorA: folders identical afterwards (got {m.Entries.Count}: {string.Join(", ", m.Entries.Select(e => e.DisplayPath))})");
        Ok(!Directory.Exists(Path.Combine(b, "dirB")) && !File.Exists(Path.Combine(b, @"sub\onlyB.txt")), "MirrorA: B-only items deleted (recycle bin)");

        // 4) 조언: 한쪽이 다른 쪽을 다 포함하면 그쪽을 남기라고 한다
        (a, b) = Make();
        foreach (var rel in new[] { @"sub\onlyB.txt", @"dirB\3.txt" }) File.Delete(Path.Combine(b, rel));
        Directory.Delete(Path.Combine(b, "dirB"));
        File.Copy(Path.Combine(a, "newerB.txt"), Path.Combine(b, "newerB.txt"), true);
        File.Copy(Path.Combine(a, "conflict.txt"), Path.Combine(b, "conflict.txt"), true);
        r = Cmp(a, b);
        var (adv2, sug2) = r.Advice();
        Console.WriteLine("  advice: " + adv2.Replace("\n", " / "));
        Ok(sug2 == SyncMode.MirrorA, "A contains everything of B → suggest keeping A");

        Directory.Delete(Root, true);
    }
}
