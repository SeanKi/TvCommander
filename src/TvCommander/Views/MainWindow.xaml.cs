using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using TvCommander.IO;
using TvCommander.Model;
using TvCommander.Native;

namespace TvCommander.Views;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DirectoryHistory _history = DirectoryHistory.Load();
    private readonly List<FilePanel> _panels = new();
    private int _panelCount = 2;
    private bool _grid;
    private FilePanel? _active, _target;
    private bool _targetPinned;
    private SearchWindow? _searchWindow;

    public MainWindow()
    {
        InitializeComponent();
        for (int i = 0; i < 4; i++)
        {
            var p = new FilePanel(this, i);
            p.Activated += OnPanelActivated;
            if (i < _settings.Panels.Count) p.ApplyState(_settings.Panels[i]);
            _panels.Add(p);
        }
        HiddenToggle.IsChecked = _settings.ShowHidden;
        RestoreWindowBounds();

        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public bool ShowHidden => _settings.ShowHidden;
    public DirectoryHistory History => _history;
    public FilePanel? ActivePanel => _active;
    public FilePanel? PanelAt(int index) => index >= 0 && index < _panels.Count ? _panels[index] : null;
    private FilePanel P => _active ?? _panels[0];
    private IEnumerable<FilePanel> VisiblePanels => _panels.Take(_panelCount);

    // ───────────────────────── 시작 / 종료 ─────────────────────────

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // SetLayout 이 보이는 패널들의 초기 탐색을 시작한다
        SetLayout(Math.Clamp(_settings.PanelCount, 2, 4), _settings.GridLayout);
        var first = _panels[Math.Clamp(_settings.ActivePanel, 0, _panelCount - 1)];
        ActivatePanel(first);
        while (first.IsLoading) await Task.Delay(50);
        first.FocusList();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _settings.PanelCount = _panelCount;
        _settings.GridLayout = _grid;
        _settings.ActivePanel = _active?.Index ?? 0;
        _settings.Panels = _panels.Select(p => p.GetState()).ToList();
        _settings.Maximized = WindowState == WindowState.Maximized;
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _settings.WindowLeft = b.Left;
        _settings.WindowTop = b.Top;
        _settings.WindowWidth = b.Width;
        _settings.WindowHeight = b.Height;
        _settings.Save();
        foreach (var p in _panels) p.FlushDwell();
        _history.Save();
        _searchWindow?.Close();
    }

    private void RestoreWindowBounds()
    {
        if (!double.IsNaN(_settings.WindowLeft) && !double.IsNaN(_settings.WindowTop))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = _settings.WindowLeft;
            Top = _settings.WindowTop;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        Width = Math.Max(MinWidth, _settings.WindowWidth);
        Height = Math.Max(MinHeight, _settings.WindowHeight);
        if (_settings.Maximized) WindowState = WindowState.Maximized;
    }

    // ───────────────────────── 레이아웃 (2/3/4창) ─────────────────────────

    private void SetLayout(int count, bool grid)
    {
        grid = grid && count == 4;
        _panelCount = count;
        _grid = grid;

        PanelGrid.Children.Clear();
        PanelGrid.RowDefinitions.Clear();
        PanelGrid.ColumnDefinitions.Clear();

        if (grid)
        {
            PanelGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            PanelGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            PanelGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            PanelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            PanelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            PanelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (int i = 0; i < 4; i++)
            {
                var p = _panels[i];
                Grid.SetRow(p, i / 2 * 2);
                Grid.SetColumn(p, i % 2 * 2);
                PanelGrid.Children.Add(p);
            }
            var v = NewSplitter(vertical: true);
            Grid.SetColumn(v, 1);
            Grid.SetRowSpan(v, 3);
            PanelGrid.Children.Add(v);
            var h = NewSplitter(vertical: false);
            Grid.SetRow(h, 1);
            Grid.SetColumnSpan(h, 3);
            PanelGrid.Children.Add(h);
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    PanelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var s = NewSplitter(vertical: true);
                    Grid.SetColumn(s, PanelGrid.ColumnDefinitions.Count - 1);
                    PanelGrid.Children.Add(s);
                }
                PanelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 150 });
                var p = _panels[i];
                Grid.SetRow(p, 0);
                Grid.SetColumn(p, PanelGrid.ColumnDefinitions.Count - 1);
                PanelGrid.Children.Add(p);
            }
        }

        foreach (var p in _panels) p.SetShownInLayout(p.Index < count);

        Layout2.IsChecked = count == 2;
        Layout3.IsChecked = count == 3;
        Layout4.IsChecked = count == 4 && !grid;
        Layout4Grid.IsChecked = count == 4 && grid;

        // 새로 보이는 패널은 활성 패널 경로(또는 저장된 경로)로 연다
        foreach (var p in VisiblePanels.Where(p => p.CurrentPath == null && !p.IsLoading))
            _ = p.NavigateInitialAsync(_active?.CurrentPath);

        if (_active == null || _active.Index >= count) ActivatePanel(_panels[0]);
        if (_target != null && _target.Index >= count) { _target = null; _targetPinned = false; }
        FixTarget();
        UpdatePanelRoles();
    }

    private static GridSplitter NewSplitter(bool vertical) => new()
    {
        Width = vertical ? 5 : double.NaN,
        Height = vertical ? double.NaN : 5,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        ResizeBehavior = GridResizeBehavior.PreviousAndNext,
        ResizeDirection = vertical ? GridResizeDirection.Columns : GridResizeDirection.Rows,
        Background = SystemColors.ControlBrush,
        Focusable = false,
    };

    private void Layout_Click(object sender, RoutedEventArgs e)
    {
        var tag = (string)((FrameworkElement)sender).Tag;
        SetLayout(tag == "4g" ? 4 : int.Parse(tag), tag == "4g");
        P.FocusList();
    }

    // ───────────────────────── 활성 / 대상 패널 ─────────────────────────

    private void OnPanelActivated(FilePanel p)
    {
        if (_active == p) return;
        var prev = _active;
        _active = p;
        if (_panelCount == 2)
            _target = VisiblePanels.First(x => x != p);
        else if (!_targetPinned && prev != null && prev.Index < _panelCount)
            _target = prev;
        FixTarget();
        UpdatePanelRoles();
    }

    private void FixTarget()
    {
        if (_active == null) return;
        if (_panelCount == 2)
            _target = VisiblePanels.First(x => x != _active);
        if (_target == null || _target == _active || _target.Index >= _panelCount)
        {
            _targetPinned = false;
            _target = VisiblePanels.FirstOrDefault(x => x != _active);
        }
    }

    private void UpdatePanelRoles()
    {
        foreach (var p in _panels)
            p.SetRole(p == _active, p == _target && _panelCount > 2);
    }

    private void ActivatePanel(FilePanel p)
    {
        OnPanelActivated(p);
        p.FocusList();
    }

    private void SetTarget(int index)
    {
        if (index >= _panelCount || _panels[index] == _active) return;
        _target = _panels[index];
        _targetPinned = true;
        UpdatePanelRoles();
    }

    /// <summary>같은 줄에서 왼쪽(-1)/오른쪽(+1) 패널</summary>
    private FilePanel? Neighbour(FilePanel p, int dir)
    {
        if (_grid)
        {
            int col = p.Index % 2 + dir;
            return col is < 0 or > 1 ? null : _panels[p.Index / 2 * 2 + col];
        }
        int i = p.Index + dir;
        return i < 0 || i >= _panelCount ? null : _panels[i];
    }

    private void FocusPanelByOffset(int delta)
    {
        int i = ((P.Index + delta) % _panelCount + _panelCount) % _panelCount;
        ActivatePanel(_panels[i]);
    }

    /// <summary>Alt+F1/F2: 2창이면 왼쪽/오른쪽, 3·4창이면 활성/대상 패널</summary>
    private FilePanel DrivePanelFor(int n)
    {
        if (_panelCount == 2) return _panels[n];
        return n == 0 ? P : _target ?? P;
    }

    // ───────────────────────── 키보드 ─────────────────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox or ComboBox or ComboBoxItem) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        const ModifierKeys None = ModifierKeys.None, Ctrl = ModifierKeys.Control, Shift = ModifierKeys.Shift, Alt = ModifierKeys.Alt;

        bool handled = true;
        switch ((key, mods))
        {
            case (Key.F3, None): CmdView(); break;
            case (Key.F4, None): CmdEdit(); break;
            case (Key.F5, None): CmdCopyMove(OpKind.Copy); break;
            case (Key.F6, None): CmdCopyMove(OpKind.Move); break;
            case (Key.F6, Shift):
            case (Key.F2, None): CmdRename(); break;
            case (Key.F7, None): CmdMakeDir(); break;
            case (Key.F7, Alt): CmdSearch(); break;
            case (Key.F8, None):
            case (Key.Delete, None): CmdDelete(false); break;
            case (Key.F8, Shift):
            case (Key.Delete, Shift): CmdDelete(true); break;
            case (Key.F9, None): CmdTerminal(); break;
            case (Key.F10, None):
            case (Key.F4, Alt): Close(); break;

            case (Key.Tab, None): FocusPanelByOffset(+1); break;
            case (Key.Tab, Shift): FocusPanelByOffset(-1); break;
            case (Key.Left, Ctrl): CmdOpenInSide(-1); break;
            case (Key.Right, Ctrl): CmdOpenInSide(+1); break;
            case (Key.Left, Alt): _ = P.GoBackAsync(); break;
            case (Key.Right, Alt): _ = P.GoForwardAsync(); break;
            case (Key.F1, Alt): DrivePanelFor(0).OpenDriveMenu(); break;
            case (Key.F2, Alt): DrivePanelFor(1).OpenDriveMenu(); break;
            case (Key.Down, Alt): P.ShowHistoryMenu(); break;

            case (Key.U, Ctrl): CmdSwap(); break;
            case (Key.R, Ctrl): _ = P.RefreshAsync(); break;
            case (Key.H, Ctrl): ToggleHidden(); break;
            case (Key.L, Ctrl): P.FocusPathBox(); break;
            case (Key.C, Ctrl | Shift): CmdCopyText(CopyTextKind.FullPaths); break;
            case (Key.F2, Ctrl): SetLayout(2, false); P.FocusList(); break;
            case (Key.F3, Ctrl): SetLayout(3, false); P.FocusList(); break;
            case (Key.F4, Ctrl): SetLayout(4, _grid); P.FocusList(); break;

            case (Key.Escape, None) when P.IsLoading: P.CancelLoad(); break;

            case (>= Key.D1 and <= Key.D4, Ctrl):
                if (key - Key.D1 < _panelCount) ActivatePanel(_panels[key - Key.D1]);
                break;
            case (>= Key.D1 and <= Key.D4, Ctrl | Shift):
                SetTarget(key - Key.D1);
                break;

            default: handled = false; break;
        }
        e.Handled = handled;
    }

    private void FnKey_Click(object sender, RoutedEventArgs e)
    {
        switch ((string)((FrameworkElement)sender).Tag)
        {
            case "F3": CmdView(); break;
            case "F4": CmdEdit(); break;
            case "F5": CmdCopyMove(OpKind.Copy); break;
            case "F6": CmdCopyMove(OpKind.Move); break;
            case "F7": CmdMakeDir(); break;
            case "F8": CmdDelete(false); break;
            case "F9": CmdTerminal(); break;
            case "F10": Close(); break;
        }
    }

    // ───────────────────────── 툴바 ─────────────────────────

    private void Back_Click(object sender, RoutedEventArgs e) { _ = P.GoBackAsync(); P.FocusList(); }
    private void Forward_Click(object sender, RoutedEventArgs e) { _ = P.GoForwardAsync(); P.FocusList(); }
    private void Up_Click(object sender, RoutedEventArgs e) { _ = P.GoUpAsync(); P.FocusList(); }
    private void Refresh_Click(object sender, RoutedEventArgs e) { NetworkHealth.InvalidateAll(); _ = P.RefreshAsync(); P.FocusList(); }
    private void Search_Click(object sender, RoutedEventArgs e) => CmdSearch();
    private void CopyPath_Click(object sender, RoutedEventArgs e) => CmdCopyText(CopyTextKind.CurrentDir);
    private void CopyFullPaths_Click(object sender, RoutedEventArgs e) => CmdCopyText(CopyTextKind.FullPaths);
    private void CopyNames_Click(object sender, RoutedEventArgs e) => CmdCopyText(CopyTextKind.Names);
    private void NewFolder_Click(object sender, RoutedEventArgs e) => CmdMakeDir();
    private void Terminal_Click(object sender, RoutedEventArgs e) => CmdTerminal();
    private void Swap_Click(object sender, RoutedEventArgs e) => CmdSwap();
    private void Hidden_Click(object sender, RoutedEventArgs e) => ToggleHidden();

    private void ToggleHidden()
    {
        _settings.ShowHidden = !_settings.ShowHidden;
        HiddenToggle.IsChecked = _settings.ShowHidden;
        foreach (var p in VisiblePanels) _ = p.RefreshAsync();
        P.FocusList();
    }

    // ───────────────────────── 명령 ─────────────────────────

    public void CmdView()
    {
        var it = P.CursorItem;
        if (it == null || it.IsDirectory) return;
        new ViewerWindow(it.FullPath) { Owner = this }.Show();
    }

    public void CmdEdit()
    {
        var it = P.CursorItem;
        if (it == null || it.IsDirectory) return;
        StartProcess(_settings.Editor, Quote(it.FullPath), it.DirectoryPath);
    }

    public async void CmdCopyMove(OpKind kind)
    {
        var src = P;
        var items = src.GetSelectedOrCurrent();
        if (items.Count == 0 || src.CurrentPath == null) return;

        string title = kind == OpKind.Copy ? "복사" : "이동";
        string what = items.Count == 1 ? $"'{items[0].Name}'" : $"{items.Count}개 항목";
        var targetPath = _target?.CurrentPath ?? src.CurrentPath;
        var dest = InputDialog.Show(this, title, $"{what}을(를) 다음 위치로 {title}:", PathUtil.WithSlash(targetPath));
        if (string.IsNullOrWhiteSpace(dest)) { src.FocusList(); return; }

        await RunFileOperationAsync(kind, items.Select(i => i.FullPath).ToList(), dest, src);
    }

    /// <summary>드래그 앤 드롭에서 호출. 대상 폴더가 확정되어 있으므로 확인 대화상자 없이 실행.</summary>
    public void StartDropOperation(OpKind kind, IReadOnlyList<string> paths, string targetDir, FilePanel? source)
        => _ = RunFileOperationAsync(kind, paths, PathUtil.WithSlash(targetDir), source);

    private async Task RunFileOperationAsync(OpKind kind, IReadOnlyList<string> sources, string destination, FilePanel? sourcePanel)
    {
        // 네트워크 사전 체크 (먹통 방지)
        foreach (var path in new[] { sources[0], destination })
        {
            if (!Path.IsPathRooted(path)) continue;
            var (ok, host) = await NetworkHealth.CheckPathAsync(path);
            if (!ok) { ShowError(new HostUnreachableException(host!).Message); return; }
        }

        var op = new FileOperation(kind, sources, destination);
        var win = new ProgressWindow(op) { Owner = this };
        var showTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        showTimer.Tick += (_, _) => { showTimer.Stop(); win.Show(); };
        showTimer.Start();

        Window DialogOwner()
        {
            if (!win.IsVisible) { showTimer.Stop(); win.Show(); }
            return win;
        }
        op.AskConflict = info => Dispatcher.Invoke(() => OverwriteDialog.Ask(DialogOwner(), info));
        op.AskError = (path, msg) => Dispatcher.Invoke(() => AskError(DialogOwner(), path, msg));

        try
        {
            await op.RunAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            showTimer.Stop();
            win.Finish();
        }

        if (!op.IsCancelled) sourcePanel?.ClearMarks();
        var dirs = new[] { PathUtil.Parent(sources[0]), op.TargetDirectory };
        await Task.WhenAll(VisiblePanels
            .Where(p => dirs.Any(d => PathUtil.Same(d, p.CurrentPath)))
            .Select(p => p.RefreshAsync()));
        P.FocusList();
    }

    private static ErrorChoice AskError(Window owner, string path, string message)
    {
        var r = MessageBox.Show(owner,
            $"{message}\n\n{path}\n\n[예] 다시 시도    [아니요] 건너뛰기    [취소] 작업 중단",
            "오류", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.No);
        return r switch
        {
            MessageBoxResult.Yes => ErrorChoice.Retry,
            MessageBoxResult.No => ErrorChoice.Skip,
            _ => ErrorChoice.Cancel,
        };
    }

    public async void CmdRename()
    {
        var p = P;
        var it = p.CursorItem;
        if (it == null || it.IsParent) return;

        int selLength = it.IsDirectory || it.Extension.Length == 0 ? it.Name.Length : it.Name.Length - it.Extension.Length - 1;
        var name = InputDialog.Show(this, "이름 바꾸기", "새 이름:", it.Name, 0, selLength);
        if (string.IsNullOrWhiteSpace(name) || name == it.Name) { p.FocusList(); return; }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { ShowError("이름에 사용할 수 없는 문자가 있습니다."); return; }

        var src = it.FullPath;
        var dst = Path.Combine(it.DirectoryPath, name);
        try
        {
            await GuardedIo.RunAsync(_ =>
            {
                unsafe
                {
                    int rc = NativeMethods.FmMoveFile(src, dst, 0, null, IntPtr.Zero, null);
                    if (rc != 0) throw Win32Errors.ToIOException(rc, src);
                }
                return true;
            }, 5000, src);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        await p.RefreshAsync(name);
        p.FocusList();
    }

    public async void CmdMakeDir()
    {
        var p = P;
        if (p.CurrentPath == null) return;
        var name = InputDialog.Show(this, "새 폴더", "폴더 이름 (a\\b 처럼 여러 단계도 가능):", "");
        if (string.IsNullOrWhiteSpace(name)) { p.FocusList(); return; }

        var full = Path.Combine(p.CurrentPath, name.Trim());
        try
        {
            await GuardedIo.RunAsync(_ => Directory.CreateDirectory(full), 5000, full);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        await p.RefreshAsync(name.Trim().Split('\\', '/')[0]);
        p.FocusList();
    }

    public async void CmdDelete(bool permanent)
    {
        var p = P;
        var items = p.GetSelectedOrCurrent();
        if (items.Count == 0 || p.CurrentPath == null) return;

        string what = items.Count == 1 ? $"'{items[0].Name}'" : $"{items.Count}개 항목";
        string msg = permanent
            ? $"{what}을(를) 영구 삭제합니다. 복구할 수 없습니다.\n계속할까요?"
            : $"{what}을(를) 휴지통으로 보낼까요?";
        if (MessageBox.Show(this, msg, permanent ? "영구 삭제" : "삭제", MessageBoxButton.YesNo,
                permanent ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            p.FocusList();
            return;
        }

        var (ok, host) = await NetworkHealth.CheckPathAsync(p.CurrentPath);
        if (!ok) { ShowError(new HostUnreachableException(host!).Message); return; }

        var focusAfter = p.NameAfterRemoval(items);
        var buffer = (string.Join('\0', items.Select(i => i.FullPath)) + "\0\0").ToCharArray();
        int rc = await GuardedIo.RunLongAsync(() =>
        {
            unsafe
            {
                fixed (char* ptr = buffer)
                    return NativeMethods.FmShellDelete(ptr, permanent ? 1 : 0, IntPtr.Zero);
            }
        }, sta: true);

        if (rc != 0 && rc != Win32Errors.Cancelled)
            ShowError($"삭제하지 못했습니다. (코드 0x{rc:X})");
        p.ClearMarks();
        await p.RefreshAsync(focusAfter);
        p.FocusList();
    }

    public void CmdTerminal()
    {
        var dir = P.CurrentPath;
        if (dir == null) return;
        var term = _settings.Terminal;
        bool unc = dir.StartsWith(@"\\");

        if (!term.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            StartProcess(term, "", dir);
            return;
        }
        if (unc)
        {
            // cmd 는 UNC 를 작업 폴더로 못 쓰므로 pushd 로 임시 드라이브를 잡는다
            StartProcess("cmd.exe", $"/k pushd {Quote(dir)}", Environment.SystemDirectory);
            return;
        }
        var wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\wt.exe");
        if (File.Exists(wt))
            StartProcess(wt, $"-d {Quote(dir.EndsWith('\\') ? dir + "." : dir)}", dir);
        else
            StartProcess("cmd.exe", "", dir);
    }

    public void CmdSearch()
    {
        var root = P.CurrentPath ?? "C:\\";
        if (_searchWindow == null)
        {
            _searchWindow = new SearchWindow(this, root) { Owner = this };
            _searchWindow.Closed += (_, _) => { _searchWindow = null; P.FocusList(); };
            _searchWindow.Show();
        }
        else
        {
            _searchWindow.SetRoot(root);
            _searchWindow.Activate();
        }
    }

    public enum CopyTextKind { CurrentDir, FullPaths, Names }

    public void CmdCopyText(CopyTextKind kind)
    {
        var p = P;
        string? text = kind switch
        {
            CopyTextKind.CurrentDir => p.CurrentPath,
            CopyTextKind.FullPaths => string.Join(Environment.NewLine, p.GetSelectedOrCurrent().Select(i => i.FullPath)),
            _ => string.Join(Environment.NewLine, p.GetSelectedOrCurrent().Select(i => i.Name)),
        };
        if (string.IsNullOrEmpty(text)) return;

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                var lines = text.Split(Environment.NewLine);
                p.FlashStatus(lines.Length == 1 ? $"클립보드에 복사: {text}" : $"클립보드에 {lines.Length}개 복사");
                break;
            }
            catch (COMException)
            {
                Thread.Sleep(50);
            }
        }
        p.FocusList();
    }

    public async void CmdSwap()
    {
        var a = P;
        var b = _target;
        if (b == null || a.CurrentPath == null || b.CurrentPath == null) return;
        string pa = a.CurrentPath, pb = b.CurrentPath;
        await Task.WhenAll(a.NavigateAsync(pb), b.NavigateAsync(pa));
        a.FocusList();
    }

    /// <summary>Ctrl+←/→: 커서의 폴더(아니면 현재 폴더)를 왼쪽/오른쪽 패널에서 연다. 포커스는 유지.</summary>
    public async void CmdOpenInSide(int dir)
    {
        var src = P;
        var target = Neighbour(src, dir) ?? src;
        var cur = src.CursorItem;
        string? path = cur switch
        {
            { IsParent: true } => cur.FullPath,
            { IsDirectory: true } => cur.FullPath,
            _ => src.CurrentPath,
        };
        if (path == null) return;
        await target.NavigateAsync(path);
        src.FocusList();
    }

    public async void GoToFile(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath);
        if (dir == null) return;
        var p = P;
        Activate();
        await p.NavigateAsync(dir, Path.GetFileName(fullPath));
        p.FocusList();
    }

    public void OpenWithShell(string path) => StartProcess(path, "", Path.GetDirectoryName(path));

    public void ShowProperties(string path)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SHObjectProperties(hwnd, NativeMethods.SHOP_FILEPATH, path, null);
    }

    // ───────────────────────── 도우미 ─────────────────────────

    private static string Quote(string s) => "\"" + s + "\"";

    /// <summary>외부 프로세스 실행. 네트워크 경로에서 셸이 멈춰도 UI 는 막히지 않게 백그라운드에서.</summary>
    private void StartProcess(string file, string args, string? workDir)
    {
        Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo(file, args) { UseShellExecute = true };
                if (!string.IsNullOrEmpty(workDir) && !workDir.StartsWith(@"\\")) psi.WorkingDirectory = workDir;
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() => ShowError($"실행하지 못했습니다: {file}\n{ex.Message}"));
            }
        });
    }

    public void ShowError(string message)
        => MessageBox.Show(this, message, "TvCommander", MessageBoxButton.OK, MessageBoxImage.Warning);
}
