using MPCommander.IO;

/// <summary>IoProbe --unblock: 차단 해제와 "같은 폴더 붙여넣기 → 복사본" 점검</summary>
internal static class UnblockTests
{
    private const string Zone = "[ZoneTransfer]\r\nZoneId=3\r\n";

    public static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpc-unblock-test");
        if (Directory.Exists(root)) Directory.Delete(root, true);

        // 구조: root/a.exe a.dll a.txt, root/sub/b.exe b.txt, root/ro.exe(읽기 전용), root/pick.txt
        string[] files = ["a.exe", "a.dll", "a.txt", @"sub\b.exe", @"sub\b.txt", "ro.exe", "pick.txt"];
        void Make()
        {
            foreach (var f in files)
            {
                var p = Path.Combine(root, f);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                if (File.Exists(p)) File.SetAttributes(p, FileAttributes.Normal);
                File.WriteAllText(p, "x");
                File.WriteAllText(p + ":Zone.Identifier", Zone);   // .NET Framework 는 ':' 경로를 막는다 → 아래 Mark 사용
            }
        }

        try { Make(); }
        catch (NotSupportedException) { MakeWithNative(root, files); }
        File.SetAttributes(Path.Combine(root, "ro.exe"), FileAttributes.ReadOnly);
        Console.WriteLine($"marked: {string.Join(", ", files.Where(f => Blocked(Path.Combine(root, f))))}");

        // 1) 폴더 + exe;dll + 하위 폴더 포함
        var op = new UnblockOperation([root], recursive: true, ["exe", "dll"], allFiles: false);
        await op.RunAsync();
        Console.WriteLine($"[recursive exe;dll] unblocked {op.Unblocked}, notBlocked {op.NotBlocked}, scanned {op.TotalFiles}, errors {op.Errors}");
        Console.WriteLine($"  still blocked: {string.Join(", ", files.Where(f => Blocked(Path.Combine(root, f))))}   (expect a.txt, sub\\b.txt, pick.txt)");
        Console.WriteLine($"  ro.exe still read-only: {File.GetAttributes(Path.Combine(root, "ro.exe")).HasFlag(FileAttributes.ReadOnly)}");

        // 2) 하위 폴더 제외 + 모든 파일
        MakeWithNative(root, files);
        op = new UnblockOperation([root], recursive: false, [], allFiles: true);
        await op.RunAsync();
        Console.WriteLine($"[top only, all files] unblocked {op.Unblocked}; still blocked: {string.Join(", ", files.Where(f => Blocked(Path.Combine(root, f))))}   (expect sub\\b.exe, sub\\b.txt)");

        // 3) 직접 고른 파일은 형식과 관계없이
        MakeWithNative(root, files);
        op = new UnblockOperation([Path.Combine(root, "pick.txt")], recursive: true, ["exe"], allFiles: false);
        await op.RunAsync();
        Console.WriteLine($"[picked pick.txt with exe filter] unblocked {op.Unblocked}; pick.txt blocked: {Blocked(Path.Combine(root, "pick.txt"))}   (expect False)");

        // 4) 같은 폴더 붙여넣기 → 복사본
        var src = Path.Combine(root, "a.txt");
        for (int i = 0; i < 2; i++)
        {
            var copy = new FileOperation(OpKind.Copy, [src], root + "\\") { RenameCopiesInSameFolder = true };
            await copy.RunAsync();
        }
        var folderCopy = new FileOperation(OpKind.Copy, [Path.Combine(root, "sub")], root + "\\") { RenameCopiesInSameFolder = true };
        await folderCopy.RunAsync();
        Console.WriteLine($"[paste into same folder] {string.Join(", ", Directory.GetFileSystemEntries(root).Select(Path.GetFileName).Where(n => n!.Contains("복사본")))}");

        foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(root, true);
    }

    // ── 차단 표시 쓰기/확인 (Win32 직접 호출: .NET Framework 는 ':' 스트림 경로를 막는다) ──

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr template);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr h, byte[] buf, int len, out int written, IntPtr ov);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint GetFileAttributesW(string name);

    private static void MakeWithNative(string root, string[] files)
    {
        foreach (var f in files)
        {
            var p = Path.Combine(root, f);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            bool ro = File.Exists(p) && File.GetAttributes(p).HasFlag(FileAttributes.ReadOnly);
            if (ro) File.SetAttributes(p, FileAttributes.Normal);
            if (!File.Exists(p)) File.WriteAllText(p, "x");
            var h = CreateFileW(p + ":Zone.Identifier", 0x40000000, 0, IntPtr.Zero, 2, 0x80, IntPtr.Zero);   // GENERIC_WRITE, CREATE_ALWAYS
            var data = System.Text.Encoding.ASCII.GetBytes(Zone);
            WriteFile(h, data, data.Length, out _, IntPtr.Zero);
            CloseHandle(h);
            if (ro) File.SetAttributes(p, FileAttributes.ReadOnly);
        }
    }

    private static bool Blocked(string file) => GetFileAttributesW(file + ":Zone.Identifier") != 0xFFFFFFFF;
}
