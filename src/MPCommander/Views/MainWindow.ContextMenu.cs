using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using MPCommander.IO;
using MPCommander.Model;
using MPCommander.Native;

namespace MPCommander.Views;

/// <summary>오른쪽 버튼 메뉴 (앱 명령 + 탐색기 셸 메뉴) 와 ZIP 압축/풀기</summary>
public partial class MainWindow
{
    private enum MenuCmd
    {
        View = 1, Edit, CopyToTarget, MoveToTarget, Rename, Delete, CopyFullPath, CopyName, Pack, Unpack, Properties,
        NewFolder = 20, Refresh, CopyDirPath, Terminal,
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
        bool zip = file && items[0].Extension.Equals("zip", StringComparison.OrdinalIgnoreCase);
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
            list.Add(new(MenuCmd.Pack, "ZIP으로 압축...\tAlt+F5"));
            if (zip) list.Add(new(MenuCmd.Unpack, "압축 풀기...\tAlt+F9"));
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
        if (!mtp) list.Add(new(MenuCmd.Terminal, "여기서 터미널 열기\tF9"));
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

    // ───────────────────────── ZIP ─────────────────────────

    /// <summary>Alt+F5: 선택 항목을 ZIP 으로 압축</summary>
    public async void CmdPack()
    {
        var p = P;
        var items = p.GetSelectedOrCurrent();
        if (items.Count == 0 || p.CurrentPath == null) return;
        if (PathUtil.IsVirtual(p.CurrentPath)) { p.FlashStatus("원격 파일은 PC 로 복사한 뒤 압축하세요."); return; }

        string baseName = items.Count == 1
            ? (items[0].IsDirectory ? items[0].Name : items[0].DisplayName)
            : PathUtil.LastSegment(p.CurrentPath) is { Length: > 0 } d ? d.TrimEnd(':') : "archive";
        var input = InputDialog.Show(this, "ZIP으로 압축",
            $"{(items.Count == 1 ? $"'{items[0].Name}'" : $"{items.Count}개 항목")}을(를) 압축할 파일:",
            Path.Combine(p.CurrentPath, baseName + ".zip"), p.CurrentPath.Length + 1, baseName.Length);
        if (Compat.IsBlank(input)) { p.FocusList(); return; }

        string zipPath;
        try { zipPath = PathUtil.Normalize(input, p.CurrentPath); }
        catch (Exception ex) { ShowError(ex.Message); return; }
        if (!zipPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) zipPath += ".zip";

        var (ok, host) = await NetworkHealth.CheckPathAsync(zipPath);
        if (!ok) { ShowError(new HostUnreachableException(host!).Message); return; }
        if (File.Exists(zipPath) &&
            MessageBox.Show(this, $"{zipPath}\n\n이미 있습니다. 덮어쓸까요?", "ZIP으로 압축", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            p.FocusList();
            return;
        }

        var op = ZipOperation.Pack(items.Select(i => i.FullPath).ToList(), zipPath);
        await RunWithProgressAsync(op);
        if (!op.IsCancelled) p.ClearMarks();
        await RefreshPanelsShowingAsync(op.TargetDirectory);
        if (PathUtil.Same(p.CurrentPath, op.TargetDirectory)) await p.RefreshAsync(Path.GetFileName(zipPath));
        p.FocusList();
    }

    /// <summary>Alt+F9: 커서의 ZIP 을 풀기</summary>
    public async void CmdUnpack()
    {
        var p = P;
        var it = p.CursorItem;
        if (it == null || it.IsDirectory || p.CurrentPath == null) return;
        if (!it.Extension.Equals("zip", StringComparison.OrdinalIgnoreCase))
        {
            p.FlashStatus("내장 압축 풀기는 ZIP 만 지원합니다. 다른 형식은 오른쪽 버튼 메뉴의 압축 프로그램을 쓰세요.");
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

        var op = ZipOperation.Extract(it.FullPath, dest);
        await RunWithProgressAsync(op);
        await RefreshPanelsShowingAsync(dest, PathUtil.Parent(dest));
        if (PathUtil.Same(p.CurrentPath, PathUtil.Parent(dest))) await p.RefreshAsync(PathUtil.LastSegment(dest));
        p.FocusList();
    }
}
