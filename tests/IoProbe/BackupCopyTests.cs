using System.Diagnostics;
using MPCommander.IO;

/// <summary>IoProbe --backup-copy: 실행 중인 exe 감지, 백업 → 복사, 사용 중 파일 처리(닫기 실패 → 강제 종료 / 이름 바꿔 교체), 다시 실행, 기록</summary>
internal static class BackupCopyTests
{
    public static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpc-backup-test");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        string src = Path.Combine(root, "new"), dst = Path.Combine(root, "app");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        Directory.CreateDirectory(Path.Combine(dst, "sub"));
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);

        // 대상(옛 버전): tool.exe = ping.exe 복사본, lib.dll, sub\a.txt, keep.txt(원본에 없음)
        File.Copy(Path.Combine(sys, "PING.EXE"), Path.Combine(dst, "tool.exe"));
        File.WriteAllText(Path.Combine(dst, "lib.dll"), "old lib");
        File.WriteAllText(Path.Combine(dst, "sub", "a.txt"), "old a");
        File.WriteAllText(Path.Combine(dst, "keep.txt"), "keep");
        // 원본(새 버전): tool.exe = ping.exe 뒤에 몇 바이트 덧붙인 것 (그대로 실행됨), lib.dll, sub\a.txt, new.txt
        MakeNewTool(sys, Path.Combine(src, "tool.exe"));
        File.WriteAllText(Path.Combine(src, "lib.dll"), "new lib");
        File.WriteAllText(Path.Combine(src, "sub", "a.txt"), "new a");
        File.WriteAllText(Path.Combine(src, "new.txt"), "new");

        var running = Start(Path.Combine(dst, "tool.exe"));
        await Task.Delay(500);

        var sources = new[] { "tool.exe", "lib.dll", "sub", "new.txt" }.Select(n => Path.Combine(src, n)).ToList();
        var plan = BackupCopyPlan.Build(sources, dst);
        Console.WriteLine($"[plan] items {plan.Items.Count} (expect 4), overwrites {plan.Overwrites.Count()} (expect 3): {string.Join(", ", plan.Overwrites.Select(o => o.Relative))}");

        var users = RunningProcesses.FindUsing(plan.Overwrites.Select(o => o.Destination).ToList());
        var found = users.FirstOrDefault(u => u.Pid == running.Id);
        Console.WriteLine($"[detect] found running tool.exe: {found != null}  name={found?.Name} exe={found?.ExePath} args='{found?.Arguments}'");

        // 1) 사용 중: 처음엔 '닫기'(콘솔이라 창이 없어 실패) → 다음엔 '강제 종료'
        var backup = plan.MakeBackupPath("backup", "t_", DateTime.Now);
        var op = new BackupCopyOperation(plan, backup);
        var asked = new List<LockedChoice>();
        op.AskLocked = (file, u) =>
        {
            var c = asked.Count == 0 ? LockedChoice.CloseAndRetry : LockedChoice.KillAndRetry;
            asked.Add(c);
            Console.WriteLine($"  locked: {Path.GetFileName(file)} by {string.Join(",", u.Select(x => x.Name + "#" + x.Pid))} → {c}");
            return c;
        };
        await op.RunAsync();
        Console.WriteLine($"[copy] copied {op.Copied} (expect 4), backed up {op.BackedUp} (expect 3), errors {op.Errors}, closed {op.Closed.Count} (expect 1)");
        bool backupOk = File.ReadAllText(Path.Combine(backup, "lib.dll")) == "old lib"
                        && File.ReadAllText(Path.Combine(backup, "sub", "a.txt")) == "old a"
                        && Same(Path.Combine(backup, "tool.exe"), Path.Combine(sys, "PING.EXE"));
        bool copyOk = File.ReadAllText(Path.Combine(dst, "lib.dll")) == "new lib"
                      && File.ReadAllText(Path.Combine(dst, "sub", "a.txt")) == "new a"
                      && File.Exists(Path.Combine(dst, "new.txt"))
                      && File.Exists(Path.Combine(dst, "keep.txt"))
                      && Same(Path.Combine(dst, "tool.exe"), Path.Combine(src, "tool.exe"));
        Console.WriteLine($"[verify] backup ok {backupOk}, copy ok {copyOk}, backup folder {Path.GetFileName(backup)}, log {File.Exists(Path.Combine(backup, "backup-copy.log"))}");
        Console.WriteLine($"[log]\n{op.LogText}");

        // 2) 다시 실행: 닫은 프로그램을 같은 인자로
        if (op.Closed.FirstOrDefault() is { } closed)
        {
            var err = RunningProcesses.Restart(closed);
            await Task.Delay(800);
            var again = Process.GetProcesses().Where(p => RunningProcesses.ImagePath(p.Id) is { } ip && ip.Equals(closed.ExePath, StringComparison.OrdinalIgnoreCase)).ToList();
            Console.WriteLine($"[restart] error={err ?? "none"}, running again {again.Count > 0}");
            foreach (var p in again) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
        }

        // 3) 이름 바꿔 교체: 대상 tool.exe(새 버전)가 실행 중인 채로 옛 ping 으로 바꾸기
        File.Copy(Path.Combine(sys, "PING.EXE"), Path.Combine(src, "tool.exe"), true);
        var running2 = Start(Path.Combine(dst, "tool.exe"));
        await Task.Delay(500);
        var plan2 = BackupCopyPlan.Build([Path.Combine(src, "tool.exe")], dst);
        var backup2 = plan2.MakeBackupPath("backup", "t_", DateTime.Now);
        var op2 = new BackupCopyOperation(plan2, backup2) { AskLocked = (_, _) => LockedChoice.RenameAndReplace };
        await op2.RunAsync();
        var olds = Directory.GetFiles(dst, "tool.exe.old-*");
        Console.WriteLine($"[rename] copied {op2.Copied}, renamed {op2.Renamed}, old file {olds.Length == 1}, new is ping {Same(Path.Combine(dst, "tool.exe"), Path.Combine(sys, "PING.EXE"))}, still running {!running2.HasExited}, backup2 differs {backup2 != backup}");
        try { running2.Kill(); running2.WaitForExit(3000); } catch { }

        await Task.Delay(300);
        if (Environment.GetEnvironmentVariable("KEEP") == null)
            try { Directory.Delete(root, true); } catch (Exception ex) { Console.WriteLine("cleanup: " + ex.Message); }
    }

    private static Process Start(string exe)
        => Process.Start(new ProcessStartInfo(exe, "-n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;

    private static void MakeNewTool(string sys, string path)
    {
        var bytes = File.ReadAllBytes(Path.Combine(sys, "PING.EXE")).Concat(new byte[] { 1, 2, 3, 4 }).ToArray();
        File.WriteAllBytes(path, bytes);
    }

    private static bool Same(string a, string b) => File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b));
}
