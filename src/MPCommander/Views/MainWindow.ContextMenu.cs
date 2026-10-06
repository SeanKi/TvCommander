using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using MPCommander.IO;
using MPCommander.Model;
using MPCommander.Native;

namespace MPCommander.Views;

/// <summary>오른쪽 버튼 메뉴 (앱 명령 + 탐색기 셸 메뉴) 와 압축/풀기 (ZIP 내장, 7z 등은 7-Zip)</summary>
public partial class MainWindow
{
    private enum MenuCmd
    {
        View = 1, Edit, CopyToTarget, MoveToTarget, Rename, Delete, CopyFullPath, CopyName, Pack, Unpack, Properties,
        NewFolder = 20, Refresh, CopyDirPath, Terminal, Unblock, Paste,
    }

    private sealed record MenuSpec(MenuCmd Id, string Text, bool Enabled = true)
    {
        public static readonly MenuSpec Separator = new(0, "");
    }

    /// <summary>
    /// 오른쪽 버튼 메뉴. clicked == null 이면 빈 곳(폴더 배경) 메뉴.
    /// PC 경로면 탐색기 메뉴(7-Zip, 반디집, 보내기, 연결 프로그램, 속성 등)를 아래에 붙인다.
    /// </summary>
    public async void ShowContextMenu(FilePanel panel, FileItem? clicked, Point screen)
    {
        OnPanelActivated(panel);
        var dir = panel.CurrentPath;
        if (dir == null) return;

        var items = clicked == null || clicked.IsParent
            ? new List<FileItem>()
            : clicked.IsMarked ? panel.GetSelectedOrCurrent() : new List<FileItem> { clicked };

        bool mtp = PathUtil.IsVirtual(dir);   // 원격(휴대폰·FTP·WebDAV): 셸 메뉴 없음
        // 셸 확장은 네트워크 경로에서 오래 걸릴 수 있다 → 서버가 응답할 때만 셸 메뉴를 붙인다
        bool shell = !mtp && (await NetworkHealth.CheckPathAsync(dir)).Ok;
        bool extended = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        var specs = items.Count > 0 ? ItemMenu(items, mtp, shell) : BackgroundMenu(mtp);
        var names = items.Count > 0 ? string.Join("\0", items.Select(i => i.Name)) + "\0\0" : "\0\0";

        int chosen = ShowNativeMenu(shell ? dir : null, names, screen, specs, extended, out bool shellInvoked);

        if (chosen == NativeMethods.FM_MENU_RENAME) CmdRename();
        else if (chosen > 0) RunMenuCommand((MenuCmd)chosen);
        else if (shellInvoked) _ = RefreshAfterShellCommandAsync(panel);
        else panel.FocusList();
    }

    /// <summary>Shift+F10 / 메뉴 키: 커서 항목 위치에 메뉴</summary>
    public void ShowContextMenuAtCursor()
    {
        var p = P;
        ShowContextMenu(p, p.CursorItem, p.CursorScreenPoint());
    }

    private List<MenuSpec> ItemMenu(List<FileItem> items, bool mtp, bool shell)
    {
        bool single = items.Count == 1;
        bool file = single && !items[0].IsDirectory;
        bool archive = file && (items[0].Extension.Equals("zip", StringComparison.OrdinalIgnoreCase) || SevenZip.CanExtract(items[0].Extension));
        string target = _target?.CurrentPath is { } t ? $" → {PathUtil.LastSegment(t)}" : "";

        var list = new List<MenuSpec>
        {
            new(MenuCmd.View, "보기\tF3", file),
            new(MenuCmd.Edit, "편집\tF4", file),
            MenuSpec.Separator,
            new(MenuCmd.CopyToTarget, $"대상 패널로 복사{target}\tF5"),
            new(MenuCmd.MoveToTarget, $"대상 패널로 이동{target}\tF6"),
            new(MenuCmd.Rename, "이름 바꾸기\tShift+F6", single),
        };
        if (!shell) list.Add(new(MenuCmd.Delete, mtp ? "삭제 (영구)\tF8" : "삭제\tF8"));
        list.Add(MenuSpec.Separator);
        list.Add(new(MenuCmd.CopyFullPath, "전체 경로 복사\tCtrl+Shift+C"));
        list.Add(new(MenuCmd.CopyName, "이름 복사"));
        if (!mtp)
        {
            list.Add(MenuSpec.Separator);
            list.Add(new(MenuCmd.Pack, $"압축 (7z / ZIP){target}...\tAlt+F5"));
            list.Add(new(MenuCmd.Unblock, "차단 해제...\tCtrl+Shift+U"));
            if (archive) list.Add(new(MenuCmd.Unpack, "압축 풀기...\tAlt+F9"));
        }
        if (!shell && !mtp) list.Add(new(MenuCmd.Properties, "속성"));
        return list;
    }

    private static List<MenuSpec> BackgroundMenu(bool mtp)
    {
        var list = new List<MenuSpec>
        {
            new(MenuCmd.NewFolder, "새 폴더\tF7"),
            new(MenuCmd.Refresh, "새로고침\tCtrl+R"),
            new(MenuCmd.CopyDirPath, "현재 경로 복사"),
        };
        list.Insert(0, new(MenuCmd.Paste, "붙여넣기\tCtrl+V"));
        if (!mtp) list.Add(new(MenuCmd.Terminal, "여기서 터미널 열기\tF9"));
        if (!mtp) list.Add(new(MenuCmd.Unblock, "이 폴더 차단 해제...\tCtrl+Shift+U"));
        return list;
    }

    private unsafe int ShowNativeMenu(string? folder, string names, Point screen, List<MenuSpec> specs, bool extended, out bool shellInvoked)
    {
        var items = new FmMenuItem[specs.Count];
        try
        {
            for (int i = 0; i < specs.Count; i++)
            {
                var s = specs[i];
                items[i] = s == MenuSpec.Separator
                    ? new FmMenuItem { Flags = 1 }
                    : new FmMenuItem { Id = (int)s.Id, Flags = s.Enabled ? 0 : 2, Text = Marshal.StringToHGlobalUni(s.Text) };
            }
            var hwnd = new WindowInteropHelper(this).Handle;
            int r, invoked;
            fixed (char* n = names)
                r = NativeMethods.FmShellContextMenu(hwnd, folder, n, (int)screen.X, (int)screen.Y, items, items.Length,
                                                     extended ? 1 : 0, out invoked);
            shellInvoked = invoked != 0;
            return r;
        }
        finally
        {
            foreach (var it in items)
                if (it.Text != IntPtr.Zero) Marshal.FreeHGlobal(it.Text);
        }
    }

    private void RunMenuCommand(MenuCmd cmd)
    {
        switch (cmd)
        {
            case MenuCmd.View: CmdView(); break;
            case MenuCmd.Edit: CmdEdit(); break;
            case MenuCmd.CopyToTarget: CmdCopyMove(OpKind.Copy); break;
            case MenuCmd.MoveToTarget: CmdCopyMove(OpKind.Move); break;
            case MenuCmd.Rename: CmdRename(); break;
            case MenuCmd.Delete: CmdDelete(false); break;
            case MenuCmd.CopyFullPath: CmdCopyText(CopyTextKind.FullPaths); break;
            case MenuCmd.CopyName: CmdCopyText(CopyTextKind.Names); break;
            case MenuCmd.Pack: CmdPack(); break;
            case MenuCmd.Unpack: CmdUnpack(); break;
            case MenuCmd.Properties:
                if (P.CursorItem is { IsParent: false } it) ShowProperties(it.FullPath);
                break;
            case MenuCmd.NewFolder: CmdMakeDir(); break;
            case MenuCmd.Refresh: _ = P.RefreshAsync(); break;
            case MenuCmd.CopyDirPath: CmdCopyText(CopyTextKind.CurrentDir); break;
            case MenuCmd.Terminal: CmdTerminal(); break;
            case MenuCmd.Unblock: CmdUnblock(); break;
            case MenuCmd.Paste: CmdPaste(); break;
        }
    }

    /// <summary>셸 명령(압축, 삭제, 붙여넣기 등)은 따로 끝나므로 잠시 뒤 두어 번 새로고침</summary>
    private async Task RefreshAfterShellCommandAsync(FilePanel panel)
    {
        panel.FocusList();
        foreach (var delay in new[] { 600, 2500 })
        {
            await Task.Delay(delay);
            await RefreshPanelsShowingAsync(panel.CurrentPath);
        }
    }

    // ───────────────────────── 압축 ─────────────────────────

    /// <summary>
    /// Alt+F5: 표시한 항목(없으면 커서 항목)을 압축. Double Commander 처럼 기본 위치는 대상 패널 폴더.
    /// 7z 는 7-Zip(7z.exe)으로, ZIP 은 내장으로 만든다.
    /// </summary>
    public async void CmdPack()
    {
        var p = P;
        var items = p.GetSelectedOrCurrent();
        if (items.Count == 0 || p.CurrentPath == null) return;
        if (PathUtil.IsVirtual(p.CurrentPath)) { p.FlashStatus("원격 파일은 PC 로 복사한 뒤 압축하세요."); return; }

        string baseName = items.Count == 1
            ? (items[0].IsDirectory ? items[0].Name : items[0].DisplayName)
            : PathUtil.LastSegment(p.CurrentPath) is { Length: > 0 } d ? d.TrimEnd(':') : "archive";
        // 대상 패널이 원격(휴대폰·FTP·WebDAV)이면 만들 수 없으므로 현재 폴더에
        var targetDir = _target?.CurrentPath is { } t && !PathUtil.IsVirtual(t) ? t : p.CurrentPath;
        string what = items.Count == 1 ? $"'{items[0].Name}'" : $"{items.Count}개 항목";

        bool singleFolder = items.Count == 1 && items[0].IsDirectory;
        var dlg = PackDialog.Show(this, _settings, $"{what}  ({p.CurrentPath})", targetDir, baseName, singleFolder);
        if (dlg == null) { p.FocusList(); return; }
        SaveSettings();
        var archive = dlg.ArchivePath;

        var (ok, host) = await NetworkHealth.CheckPathAsync(archive);
        if (!ok) { ShowError(new HostUnreachableException(host!).Message); return; }
        if (File.Exists(archive) &&
            MessageBox.Show(this, $"{archive}\n\n이미 있습니다. 덮어쓸까요?", "압축", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            p.FocusList();
            return;
        }

        var sources = items.Select(i => i.FullPath).ToList();
        if (dlg.ContentsOnly)
        {
            // 폴더 자체는 빼고 바로 안의 항목들을 압축 대상으로 (네트워크 폴더에서 멈추지 않게 시간 제한)
            var folder = items[0].FullPath;
            try
            {
                sources = await GuardedIo.RunAsync(_ => Directory.EnumerateFileSystemEntries(folder).ToList(), 5000, folder);
            }
            catch (Exception ex) { ShowError(ex.Message); return; }
            if (sources.Count == 0) { ShowError($"{folder}\n\n폴더가 비어 있어 압축할 것이 없습니다."); return; }
        }
        ProgressOperation op = dlg.Use7z ? SevenZipOperation.Pack(sources, archive, dlg.Level) : ZipOperation.Pack(sources, archive, dlg.Level);
        await RunWithProgressAsync(op);
        if (!op.IsCancelled) p.ClearMarks();

        var dir = PathUtil.Parent(archive) ?? archive;
        await RefreshPanelsShowingAsync(dir);
        // 압축 파일을 보여 주는 패널(대상 패널 우선)에서 커서를 새 파일에 둔다
        foreach (var panel in new[] { _target, p })
            if (panel?.CurrentPath != null && PathUtil.Same(panel.CurrentPath, dir)) await panel.RefreshAsync(Path.GetFileName(archive));
        if (!op.IsCancelled && File.Exists(archive))
            p.FlashStatus($"압축 완료: {archive}  ({PathUtil.FormatSize(new FileInfo(archive).Length)})");
        p.FocusList();
    }

    /// <summary>Alt+F9: 커서의 압축 파일 풀기. ZIP 은 내장, 7z·RAR·TAR 등은 7-Zip 으로.</summary>
    public async void CmdUnpack()
    {
        var p = P;
        var it = p.CursorItem;
        if (it == null || it.IsDirectory || p.CurrentPath == null) return;
        bool zip = it.Extension.Equals("zip", StringComparison.OrdinalIgnoreCase);
        if (!zip && !SevenZip.CanExtract(it.Extension))
        {
            p.FlashStatus("압축 파일(ZIP, 7z, RAR, TAR 등)이 아닙니다.");
            return;
        }
        if (!zip && SevenZip.Exe == null)
        {
            p.FlashStatus("ZIP 외의 형식은 7-Zip 이 있어야 풀 수 있습니다. 7-Zip 을 설치하거나 7za.exe 를 MP-Commander 폴더에 두세요.");
            return;
        }
        if (PathUtil.IsVirtual(p.CurrentPath)) { p.FlashStatus("원격 파일은 PC 로 복사한 뒤 압축을 푸세요."); return; }

        var defaultDest = PathUtil.WithSlash(Path.Combine(p.CurrentPath, it.DisplayName));
        var input = InputDialog.Show(this, "압축 풀기", $"'{it.Name}' 의 압축을 풀 폴더:", defaultDest);
        if (Compat.IsBlank(input)) { p.FocusList(); return; }

        string dest;
        try { dest = PathUtil.Normalize(input, p.CurrentPath); }
        catch (Exception ex) { ShowError(ex.Message); return; }
        var (ok, host) = await NetworkHealth.CheckPathAsync(dest);
        if (!ok) { ShowError(new HostUnreachableException(host!).Message); return; }

        ProgressOperation op;
        if (zip)
        {
            op = ZipOperation.Extract(it.FullPath, dest);
        }
        else
        {
            // 7-Zip 은 파일마다 묻지 못하므로, 이미 있는 폴더에 풀 때만 한 번 묻는다
            bool overwrite = true;
            if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any())
            {
                var answer = MessageBox.Show(this, $"{dest}\n\n폴더에 이미 파일이 있습니다. 같은 이름의 파일을 덮어쓸까요?\n\n예: 덮어쓰기   아니요: 있는 파일은 건너뛰기",
                    "압축 풀기", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No);
                if (answer == MessageBoxResult.Cancel) { p.FocusList(); return; }
                overwrite = answer == MessageBoxResult.Yes;
            }
            op = SevenZipOperation.Extract(it.FullPath, dest, overwrite);
        }
        await RunWithProgressAsync(op);
        await RefreshPanelsShowingAsync(dest, PathUtil.Parent(dest));
        if (PathUtil.Same(p.CurrentPath, PathUtil.Parent(dest))) await p.RefreshAsync(PathUtil.LastSegment(dest));
        p.FocusList();
    }
}
