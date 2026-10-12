using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using MPCommander.IO;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

/// <summary>
/// IoProbe --virtual-files: 탐색기의 '가상 파일'(FileGroupDescriptorW + FileContents) 복사.
/// 휴대폰(MTP)과 같은 방식인 zip 폴더 안의 항목으로 셸 데이터 개체를 만들어 VirtualFileCopyOperation 으로 풀어 본다.
/// </summary>
internal static class VirtualFileTests
{
    public static Task Run()
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            // 셸 데이터 개체는 STA 개체라 작업 스레드가 부르면 이 스레드가 메시지를 돌려야 한다 (앱에서는 UI 스레드)
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
        var root = Path.Combine(Path.GetTempPath(), "mpc-virtual-files");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello 한글");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        var big = new byte[3 * 1024 * 1024 + 123];
        new Random(1).NextBytes(big);
        File.WriteAllBytes(Path.Combine(src, "sub", "b.bin"), big);
        var zip = Path.Combine(root, "test.zip");
        ZipFile.CreateFromDirectory(src, zip);
        var dest = Directory.CreateDirectory(Path.Combine(root, "out")).FullName;

        var data = ShellDataObject(Path.Combine(zip, "a.txt"), Path.Combine(zip, "sub"));
        Ok(data != null, "shell data object for zip items");
        // 끌어 놓기: WPF 는 다른 프로그램의 데이터 개체를 DataObject(OleConverter) 로 감싸 넘긴다
        var wpf = new System.Windows.DataObject(data!);
        Ok(VirtualFileCopyOperation.HasVirtualFiles(wpf), "WPF sees FileGroupDescriptorW");
        Ok(ReferenceEquals(VirtualFileCopyOperation.Native(wpf), data), "Native() unwraps the OLE data object");
        var op = VirtualFileCopyOperation.TryCreate(VirtualFileCopyOperation.Native(wpf), dest);
        Ok(op != null, "FileGroupDescriptorW present");
        if (op == null) return;
        Console.WriteLine("  entries: " + string.Join(", ", op.Entries.Select(e => e.RelativePath + (e.IsDirectory ? "\\" : $" ({e.Size})"))));
        Ok(op.Entries.Any(e => e.RelativePath == @"sub\b.bin" && e.Size == big.Length), "folder contents listed with size");

        await op.RunAsync();
        Ok(File.ReadAllText(Path.Combine(dest, "a.txt")) == "hello 한글", "a.txt copied");
        Ok(File.Exists(Path.Combine(dest, "sub", "b.bin")) && File.ReadAllBytes(Path.Combine(dest, "sub", "b.bin")).SequenceEqual(big), "sub\\b.bin copied (3 MB, stream)");
        Ok(op.Copied == 2 && op.Errors == 0, $"copied 2, errors 0 (got {op.Copied}, {op.Errors})");

        // 같은 이름: 건너뛰기
        File.WriteAllText(Path.Combine(dest, "a.txt"), "keep");
        var op2 = VirtualFileCopyOperation.TryCreate(ShellDataObject(Path.Combine(zip, "a.txt")), dest)!;
        int asked = 0;
        op2.AskConflict = _ => { asked++; return ConflictChoice.Skip; };
        await op2.RunAsync();
        Ok(asked == 1 && File.ReadAllText(Path.Combine(dest, "a.txt")) == "keep", "conflict asked, skipped");

        // 덮어쓰기
        var op3 = VirtualFileCopyOperation.TryCreate(ShellDataObject(Path.Combine(zip, "a.txt")), dest)!;
        op3.AskConflict = _ => ConflictChoice.Overwrite;
        await op3.RunAsync();
        Ok(File.ReadAllText(Path.Combine(dest, "a.txt")) == "hello 한글", "overwritten");

        Directory.Delete(root, true);
    }

    /// <summary>셸 경로들(zip 안 포함)의 데이터 개체 — 탐색기에서 고르고 끌거나 복사할 때와 같은 것</summary>
    private static ComDataObject? ShellDataObject(params string[] paths)
    {
        var pidls = new IntPtr[paths.Length];
        try
        {
            for (int i = 0; i < paths.Length; i++)
                Marshal.ThrowExceptionForHR(SHParseDisplayName(paths[i], IntPtr.Zero, out pidls[i], 0, out _));
            Marshal.ThrowExceptionForHR(SHCreateShellItemArrayFromIDLists((uint)pidls.Length, pidls, out var array));
            var bhid = new Guid("B8C0BD9F-ED24-455c-83E6-D5390C4FE8C4");   // BHID_DataObject
            var iid = typeof(ComDataObject).GUID;
            array.BindToHandler(IntPtr.Zero, ref bhid, ref iid, out var obj);
            return (ComDataObject)obj;
        }
        finally
        {
            foreach (var p in pidls) if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
        }
    }

    [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        // (나머지 메서드는 쓰지 않는다)
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindCtx, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);
    [DllImport("shell32.dll")]
    private static extern int SHCreateShellItemArrayFromIDLists(uint count, IntPtr[] pidls, out IShellItemArray array);
}
