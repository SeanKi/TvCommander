using System.Diagnostics;
using MPCommander.IO;

/// <summary>
/// IoProbe --restore: 백업 복사 → 그 백업에서 복원 → last_one 에서 다시 복원.
/// 복원은 기록(backup-copy.log)을 보고 백업한 파일은 되돌리고, 그때 새로 생긴 파일은 치우며, 지금 파일은 last_one 에 보관한다.
/// 새로 생겼던 파일이 실행 중인 exe 여도 옮길 수 있어야 한다.
/// </summary>
internal static class RestoreTests
{
    private static void Ok(bool cond, string what)
    {
        Console.WriteLine((cond ? "  OK   " : "  FAIL ") + what);
        if (!cond) Environment.ExitCode = 1;
    }

    public static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpc-restore-test");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        string src = Path.Combine(root, "new"), dst = Path.Combine(root, "app");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        Directory.CreateDirectory(Path.Combine(dst, "sub"));
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);

        // 옛 버전: a.txt, sub\b.txt, keep.txt / 새 버전: a.txt, sub\b.txt, sub\c.txt(새), tool.exe(새)
        File.WriteAllText(Path.Combine(dst, "a.txt"), "old a");
        File.WriteAllText(Path.Combine(dst, "sub", "b.txt"), "old b");
        File.WriteAllText(Path.Combine(dst, "keep.txt"), "keep");
        File.WriteAllText(Path.Combine(src, "a.txt"), "new a");
        File.WriteAllText(Path.Combine(src, "sub", "b.txt"), "new b");
        File.WriteAllText(Path.Combine(src, "sub", "c.txt"), "new c");
        File.Copy(Path.Combine(sys, "PING.EXE"), Path.Combine(src, "tool.exe"));

        // 1) 백업 복사
        var plan = BackupCopyPlan.Build(new[] { "a.txt", "sub", "tool.exe" }.Select(n => Path.Combine(src, n)).ToList(), dst);
        var backup = plan.MakeBackupPath("backup", "v1_", DateTime.Now);
        var op = new BackupCopyOperation(plan, backup);
        await op.RunAsync();
        Ok(op.Copied == 4 && op.BackedUp == 2, $"backup-copy: copied {op.Copied} (4), backed up {op.BackedUp} (2)");

        // 새로 생긴 tool.exe 를 실행해 둔다 (복원 때 실행 중인 새 파일을 치워야 한다)
        var running = Process.Start(new ProcessStartInfo(Path.Combine(dst, "tool.exe"), "-n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
        await Task.Delay(300);

        // 2) 복원 계획
        Ok(BackupCopyPlan.IsBackupFolder(backup), "backup folder has backup-copy.log");
        var rp = BackupCopyPlan.BuildRestore(backup);
        Ok(rp.TargetDirectory == PathUtil.TrimEnd(dst), $"restore target = {rp.TargetDirectory}");
        Ok(rp.Items.Count == 2 && rp.Items.All(i => i.DestinationExists), $"restore items: {string.Join(", ", rp.Items.Select(i => i.Relative))} (a.txt, sub\\b.txt)");
        Ok(rp.Removals.Count == 2 && rp.Removals.Contains(@"sub\c.txt") && rp.Removals.Contains("tool.exe"), $"removals: {string.Join(", ", rp.Removals)} (sub\\c.txt, tool.exe)");

        // 3) 복원 실행: 지금 파일은 backup\last_one 으로
        var keep = Path.Combine(dst, "backup", "last_one");
        var rop = new BackupCopyOperation(rp, keep);
        await rop.RunAsync();
        Ok(rop.Copied == 2 && rop.BackedUp == 2 && rop.Removed == 2 && rop.Errors == 0,
           $"restore: restored {rop.Copied} (2), kept {rop.BackedUp} (2), moved aside {rop.Removed} (2), errors {rop.Errors}");
        Ok(File.ReadAllText(Path.Combine(dst, "a.txt")) == "old a" && File.ReadAllText(Path.Combine(dst, "sub", "b.txt")) == "old b", "target back to old a / old b");
        Ok(!File.Exists(Path.Combine(dst, "sub", "c.txt")) && !File.Exists(Path.Combine(dst, "tool.exe")), "new files removed from target (tool.exe even while running)");
        Ok(File.ReadAllText(Path.Combine(dst, "keep.txt")) == "keep", "untouched file kept");
        Ok(File.ReadAllText(Path.Combine(keep, "a.txt")) == "new a" && File.ReadAllText(Path.Combine(keep, "sub", "c.txt")) == "new c"
           && File.Exists(Path.Combine(keep, "tool.exe")) && File.Exists(Path.Combine(keep, "backup-copy.log")), "last_one holds the replaced/removed files + log");
        Console.WriteLine($"  running tool.exe still alive: {!running.HasExited}");
        try { running.Kill(); running.WaitForExit(3000); } catch { }

        // 4) last_one 에서 다시 복원 → 새 버전으로 돌아간다
        var rp2 = BackupCopyPlan.BuildRestore(keep);
        var keep2 = Path.Combine(dst, "backup", "last_one_again");
        var rop2 = new BackupCopyOperation(rp2, keep2);
        await rop2.RunAsync();
        Ok(File.ReadAllText(Path.Combine(dst, "a.txt")) == "new a" && File.ReadAllText(Path.Combine(dst, "sub", "c.txt")) == "new c" && File.Exists(Path.Combine(dst, "tool.exe")),
           $"restore from last_one: back to the new version (restored {rop2.Copied}, removed {rop2.Removed})");

        Console.WriteLine("[restore log]\n" + rop.LogText);
        Directory.Delete(root, true);
    }
}
