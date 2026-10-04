using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using MPCommander.IO;
using MPCommander.Model;
using MPCommander.Native;

namespace MPCommander.Views;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DirectoryHistory _history = DirectoryHistory.Load();
    private readonly List<FilePanel> _panels = new();
    private const int MaxPanels = 8;
    /// <summary>지원하는 패널 수와 배치 (행 × 열)</summary>
    private static readonly Dictionary<int, (int Rows, int Cols)> Layouts = new()
    {
        [2] = (1, 2), [3] = (1, 3), [4] = (2, 2), [6] = (2, 3), [8] = (2, 4),
    };
    private int _panelCount = 2;
    private int _cols = 2;
    private FilePanel? _active, _target;
    private bool _targetPinned;
    private SearchWindow? _searchWindow;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"MP-Commander {AppVersion.Display}";
        for (int i = 0; i < MaxPanels; i++)
        {
            var p = new FilePanel(this, i);
            p.Activated += OnPanelActivated;
            p.PathChanged += OnPanelPathChanged;
            if (i < _settings.Panels.Count) p.ApplyState(_settings.Panels[i]);
            _panels.Add(p);
        }
        HiddenToggle.IsChecked = _settings.ShowHidden;
        InitToolbarIcons();
        InitCommandLine();
        ApplyFontLevel(_settings.FontLevel);
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
        SetLayout(_settings.PanelCount);
        var first = _panels[Compat.Clamp(_settings.ActivePanel, 0, _panelCount - 1)];
        ActivatePanel(first);
        while (first.IsLoading) await Task.Delay(50);
        first.FocusList();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _settings.PanelCount = _panelCount;
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

    // ───────────────────────── 레이아웃 (2·3창 가로, 4·6·8창 2줄 격자) ─────────────────────────

    private void SetLayout(int count)
    {
        if (!Layouts.TryGetValue(count, out var shape)) { count = 2; shape = Layouts[2]; }
        var (rows, cols) = shape;
        _panelCount = count;
        _cols = cols;

        PanelGrid.Children.Clear();
        PanelGrid.RowDefinitions.Clear();
        PanelGrid.ColumnDefinitions.Clear();

        // 패널 사이마다 분할선용 Auto 행/열
        for (int r = 0; r < rows; r++)
        {
            if (r > 0) PanelGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            PanelGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 });
        }
        for (int c = 0; c < cols; c++)
        {
            if (c > 0) PanelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            PanelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 150 });
        }

        for (int i = 0; i < count; i++)
        {
            var p = _panels[i];
            Grid.SetRow(p, i / cols * 2);
            Grid.SetColumn(p, i % cols * 2);
            PanelGrid.Children.Add(p);
        }
        for (int c = 1; c < cols; c++)
        {
            var v = NewSplitter(vertical: true);
            Grid.SetColumn(v, c * 2 - 1);
            Grid.SetRowSpan(v, rows * 2 - 1);
            PanelGrid.Children.Add(v);
        }
        for (int r = 1; r < rows; r++)
        {
            var h = NewSplitter(vertical: false);
            Grid.SetRow(h, r * 2 - 1);
            Grid.SetColumnSpan(h, cols * 2 - 1);
            PanelGrid.Children.Add(h);
        }

        foreach (var p in _panels) p.SetShownInLayout(p.Index < count);
        foreach (var rb in new[] { Layout2, Layout3, Layout4, Layout6, Layout8 })
            rb.IsChecked = (string)rb.Tag == count.ToString();

        // 새로 보이는 패널은 저장된 경로(없으면 활성 패널 경로)로 연다
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
        SetLayout(int.Parse((string)((FrameworkElement)sender).Tag));
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
        UpdateCommandPrompt();
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
        int col = p.Index % _cols + dir;
        if (col < 0 || col >= _cols) return null;
        int i = p.Index / _cols * _cols + col;
        return i < _panelCount ? _panels[i] : null;
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
            case (Key.F5, Alt): CmdPack(); break;
            case (Key.F9, Alt): CmdUnpack(); break;
            case (Key.F10, Shift):
            case (Key.Apps, None): ShowContextMenuAtCursor(); break;
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
            case (Key.E, Ctrl): FocusCommandLine(); break;
            case (Key.F, Ctrl): CmdConnections(); break;
            case (Key.Enter, Ctrl):
                if (P.CursorItem is { IsParent: false } ce) AppendToCommandLine(ce.Name);
                break;
            case (Key.Enter, Ctrl | Shift):
                if (P.CursorItem is { IsParent: false } cf) AppendToCommandLine(cf.FullPath);
                break;
            case (Key.P, Ctrl):
                if (P.CurrentPath is { } cp) AppendToCommandLine(cp);
                break;
            case (Key.R, Ctrl): _ = P.RefreshAsync(); break;
            case (Key.H, Ctrl): ToggleHidden(); break;
            case (Key.L, Ctrl): P.FocusPathBox(); break;
            case (Key.C, Ctrl | Shift): CmdCopyText(CopyTextKind.FullPaths); break;
            case (Key.F2, Ctrl): SetLayout(2); P.FocusList(); break;
            case (Key.F3, Ctrl): SetLayout(3); P.FocusList(); break;
            case (Key.F4, Ctrl): SetLayout(4); P.FocusList(); break;
            case (Key.F6, Ctrl): SetLayout(6); P.FocusList(); break;
            case (Key.F8, Ctrl): SetLayout(8); P.FocusList(); break;
            case (Key.OemPlus or Key.Add, Ctrl): ApplyFontLevel(_settings.FontLevel + 1); break;
            case (Key.OemMinus or Key.Subtract, Ctrl): ApplyFontLevel(_settings.FontLevel - 1); break;

            case (Key.Escape, None) when P.IsLoading: P.CancelLoad(); break;

            case (>= Key.D1 and <= Key.D8, Ctrl):
                if (key - Key.D1 < _panelCount) ActivatePanel(_panels[key - Key.D1]);
                break;
            case (>= Key.D1 and <= Key.D8, Ctrl | Shift):
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
    private void Connections_Click(object sender, RoutedEventArgs e) => CmdConnections();
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

    public async void CmdView()
    {
        var it = P.CursorItem;
        if (it == null || it.IsDirectory) return;
        var local = await MaterializeAsync(it.FullPath);
        if (local != null) new ViewerWindow(local) { Owner = this }.Show();
    }

    public async void CmdEdit()
    {
        var it = P.CursorItem;
        if (it == null || it.IsDirectory) return;
        var local = await MaterializeAsync(it.FullPath);
        if (local == null) return;
        if (PathUtil.IsVirtual(it.FullPath))
            P.FlashStatus("원격 파일은 임시 사본으로 열립니다. 수정한 뒤에는 F5 로 다시 복사하세요.");
        StartProcess(_settings.Editor, Quote(local), Path.GetDirectoryName(local));
    }

    /// <summary>원격(휴대폰·FTP·WebDAV) 파일이면 임시 폴더로 받아 로컬 경로를 돌려준다. 실패하면 null.</summary>
    private async Task<string?> MaterializeAsync(string path)
    {
        if (!PathUtil.IsVirtual(path)) return path;
        var p = P;
        var fs = VirtualFs.For(path);
        var dir = Path.Combine(Path.GetTempPath(), "MP-Commander", "remote", Guid.NewGuid().ToString("N").Substring(0, 8));
        var local = Path.Combine(dir, PathUtil.LastSegment(path));
        p.FlashStatus($"{fs.DisplayName}에서 가져오는 중... " + PathUtil.LastSegment(path));
        try
        {
            await GuardedIo.RunAsync(ctx =>
            {
                Directory.CreateDirectory(dir);
                fs.Download(path, local, true, (_, _) => { ctx.Report(); return ctx.IsCancelled; });
                return true;
            }, fs.IdleTimeoutMs, path, onTimeout: () => fs.Abandon(path));
            return local;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            return null;
        }
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
        if (Compat.IsBlank(dest)) { src.FocusList(); return; }

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
        await RunWithProgressAsync(op);

        if (!op.IsCancelled) sourcePanel?.ClearMarks();
        await RefreshPanelsShowingAsync(PathUtil.Parent(sources[0]), op.TargetDirectory);
        P.FocusList();
    }

    /// <summary>진행 창과 함께 작업 실행. 짧게 끝나면 창을 띄우지 않는다.</summary>
    private async Task RunWithProgressAsync(ProgressOperation op)
    {
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
    }

    private Task RefreshPanelsShowingAsync(params string?[] dirs)
        => Task.WhenAll(VisiblePanels
            .Where(p => dirs.Any(d => PathUtil.Same(d, p.CurrentPath)))
            .Select(p => p.RefreshAsync()));

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
        if (Compat.IsBlank(name) || name == it.Name) { p.FocusList(); return; }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { ShowError("이름에 사용할 수 없는 문자가 있습니다."); return; }

        var src = it.FullPath;
        var dst = PathUtil.Combine(it.DirectoryPath, name);
        try
        {
            if (PathUtil.IsVirtual(src))
            {
                var fs = VirtualFs.For(src);
                await GuardedIo.RunAsync(_ => { fs.Rename(src, name); return true; }, fs.IdleTimeoutMs, src, onTimeout: () => fs.Abandon(src));
            }
            else
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
        if (Compat.IsBlank(name)) { p.FocusList(); return; }

        var full = PathUtil.Combine(p.CurrentPath, name.Trim());
        try
        {
            if (PathUtil.IsVirtual(full))
            {
                var fs = VirtualFs.For(full);
                await GuardedIo.RunAsync(_ => { fs.EnsureFolder(full); return true; }, fs.IdleTimeoutMs, full, onTimeout: () => fs.Abandon(full));
            }
            else
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
        if (PathUtil.IsVirtual(p.CurrentPath))
        {
            await DeleteRemoteAsync(p, items, what);
            return;
        }
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
        var buffer = (string.Join("\0", items.Select(i => i.FullPath)) + "\0\0").ToCharArray();
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

    /// <summary>원격(휴대폰·FTP·WebDAV)에는 휴지통이 없으므로 항상 영구 삭제</summary>
    private async Task DeleteRemoteAsync(FilePanel p, List<FileItem> items, string what)
    {
        var fs = VirtualFs.For(p.CurrentPath!);
        if (MessageBox.Show(this, $"{what}을(를) {fs.DisplayName}에서 영구 삭제합니다. 휴지통이 없어 복구할 수 없습니다.\n계속할까요?",
                $"{fs.DisplayName}에서 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            p.FocusList();
            return;
        }
        var focusAfter = p.NameAfterRemoval(items);
        var paths = items.Select(i => i.FullPath).ToList();
        try
        {
            await GuardedIo.RunAsync(ctx =>
            {
                foreach (var path in paths)
                {
                    if (ctx.IsCancelled) break;
                    fs.Delete(path, recursive: true);
                    ctx.Report();
                }
                return true;
            }, 30000, p.CurrentPath, onTimeout: () => fs.Abandon(paths[0]));
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        p.ClearMarks();
        await p.RefreshAsync(focusAfter);
        p.FocusList();
    }

    public void CmdTerminal()
    {
        var dir = P.CurrentPath;
        if (dir == null) return;
        if (PathUtil.IsVirtual(dir)) { P.FlashStatus("원격(휴대폰·FTP·WebDAV) 폴더에서는 터미널을 열 수 없습니다."); return; }
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
        if (PathUtil.IsVirtual(root)) root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);   // 찾기는 PC 경로만
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

    /// <summary>Ctrl+F: 원격 연결 관리 (FTP / WebDAV 등록·편집·삭제·연결)</summary>
    public void CmdConnections()
    {
        new ConnectionsWindow(this) { Owner = this }.ShowDialog();
        P.FocusList();
    }

    /// <summary>활성 패널에서 경로 열기 (연결 관리의 '연결')</summary>
    public async void OpenPathInActivePanel(string path)
    {
        var p = P;
        await p.NavigateAsync(path);
        p.FocusList();
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
        if (Compat.IsEmpty(text)) return;

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                var lines = text.Split(new[] { Environment.NewLine }, StringSplitOptions.None);
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

    public async void OpenWithShell(string path)
    {
        var local = await MaterializeAsync(path);
        if (local != null) StartProcess(local, "", Path.GetDirectoryName(local));
    }

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
                if (!Compat.IsEmpty(workDir) && !workDir.StartsWith(@"\\")) psi.WorkingDirectory = workDir;
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() => ShowError($"실행하지 못했습니다: {file}\n{ex.Message}"));
            }
        });
    }

    public void ShowError(string message)
        => MessageBox.Show(this, message, "MP-Commander", MessageBoxButton.OK, MessageBoxImage.Warning);
}
