using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using MPCommander.IO;
using MPCommander.Model;

namespace MPCommander.Views;

public partial class FilePanel : UserControl
{
    private const string DragFormat = "MPCommander.Panel";
    private const string PathsFormat = "MPCommander.Paths";
    private static readonly Brush ActiveBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x78, 0xD7));
    private static readonly Brush TargetBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0xA0, 0x30));
    private static readonly Brush ActivePathBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xEB, 0xFF));

    private sealed record DriveEntry(string Root, string Display);

    private readonly MainWindow _host;
    private readonly FileItemComparer _comparer = new();
    private readonly Stack<string> _back = new(), _forward = new();
    private readonly Dictionary<string, string> _driveLastPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.ObjectModel.ObservableCollection<DriveEntry> _drives = new();
    private readonly DispatcherTimer _loadingDelay, _watchDebounce, _flashTimer, _pollTimer;

    private List<FileItem> _items = new();
    private CancellationTokenSource? _loadCts;
    private FileSystemWatcher? _watcher;
    private long _watchFirstEvent;
    private string? _initialPath, _failedPath;
    private string _quick = "";
    private long _quickTick;
    private Point _dragStart;
    private FileItem? _dragItem;
    private bool _updatingDrives;
    private int _fileCount, _dirCount, _markedFiles, _markedDirs;
    private long _totalSize, _markedSize;
    private string _freeText = "";
    private int _blockedCount;
    private int _zoneGeneration;
    private long _dwellStart;          // 현재 폴더에 들어온 시각 (0 = 측정 안 함: 패널이 숨겨짐)
    private bool _shownInLayout = true;

    public FilePanel(MainWindow host, int index)
    {
        InitializeComponent();
        _host = host;
        Index = index;

        _loadingDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _loadingDelay.Tick += (_, _) => { _loadingDelay.Stop(); LoadingOverlay.Visibility = Visibility.Visible; };
        _watchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _watchDebounce.Tick += (_, _) =>
        {
            _watchDebounce.Stop();
            if (IsLoading) { _watchDebounce.Start(); return; }   // 읽는 중이면 끝난 뒤 다시
            _watchFirstEvent = 0;
            _ = RefreshAsync();
        };
        _pollTimer = new DispatcherTimer();
        _pollTimer.Tick += async (_, _) => await PollAsync();
        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _flashTimer.Tick += (_, _) => { _flashTimer.Stop(); UpdateStatus(); };

        FileList.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(Header_Click));
        GotKeyboardFocus += (_, _) => Activated?.Invoke(this);
        PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton is MouseButton.XButton1 or MouseButton.XButton2) return;   // 뒤로/앞으로 버튼은 활성 패널 기준
            Activated?.Invoke(this);
        };

        UpdateHeaders();
        PopulateDrives();
        RemoteConnections.Changed += () => Dispatcher.BeginInvoke(PopulateDrives);
        _host.History.Changed += UpdateStar;   // 다른 패널에서 올리고 내려도 별표가 따라온다
        UpdateStar();
    }

    public event Action<FilePanel>? Activated;
    /// <summary>폴더를 성공적으로 열었을 때 (명령줄 프롬프트 갱신용)</summary>
    public event Action<FilePanel>? PathChanged;

    public int Index { get; }
    public string? CurrentPath { get; private set; }
    public bool IsLoading => _loadCts != null;
    public FileItem? CursorItem => FileList.SelectedItem as FileItem;

    // ───────────────────────── 상태 저장 ─────────────────────────

    public void ApplyState(PanelState? s)
    {
        if (s == null) return;
        _initialPath = s.Path;
        _comparer.Column = s.Sort;
        _comparer.Descending = s.Descending;
        UpdateHeaders();
    }

    /// <summary>명령줄로 받은 시작 경로 (저장된 경로보다 우선)</summary>
    public void SetStartPath(string path) => _initialPath = path;

    public PanelState GetState() => new()
    {
        Path = CurrentPath ?? _initialPath,
        Sort = _comparer.Column,
        Descending = _comparer.Descending,
    };

    public void SetRole(bool active, bool target)
    {
        OuterBorder.BorderBrush = active ? ActiveBrush : target ? TargetBrush : Brushes.Transparent;
        PathBox.Background = active ? ActivePathBrush : SystemColors.WindowBrush;
        TargetBadge.Visibility = target ? Visibility.Visible : Visibility.Collapsed;
    }

    // ───────────────────────── 탐색 ─────────────────────────

    public async Task NavigateInitialAsync(string? fallback = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = _initialPath ?? fallback ?? home;
        _initialPath = null;
        if (await NavigateAsync(path) || _failedPath == null) return;   // 성공 또는 취소

        var error = ErrorText.Text;
        var failed = _failedPath;
        if (await NavigateAsync(home))
            ShowError(error + "\n→ 홈 폴더를 대신 열었습니다.", failed);
    }

    /// <summary>폴더를 연다. 실패하면 현재 목록은 그대로 두고 오류 표시줄을 띄운다.</summary>
    public async Task<bool> NavigateAsync(string path, string? focusName = null, bool addHistory = true, bool keepState = false)
    {
        string full;
        try
        {
            full = PathUtil.Normalize(path, CurrentPath);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message, null);
            return false;
        }
        if (PathUtil.IsServerOnly(full))
        {
            ShowError(@"공유 폴더 이름까지 입력하세요. (예: \\server\share)", null);
            return false;
        }

        HashSet<string>? marks = null;
        int oldIndex = FileList.SelectedIndex;
        if (keepState)
        {
            marks = _items.Where(i => i.IsMarked).Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            focusName ??= CursorItem?.Name;
        }

        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        LoadingText.Text = $"불러오는 중... (Esc: 취소)\n{full}";
        _loadingDelay.Start();

        try
        {
            var progress = new Progress<int>(n =>
            {
                if (_loadCts == cts) LoadingText.Text = $"불러오는 중... {n:N0}개 (Esc: 취소)\n{full}";
            });
            var items = await DirectoryLoader.LoadAsync(full, _host.ShowHidden, progress, cts.Token);
            if (_loadCts != cts) return false;   // 더 새로운 탐색이 시작됨

            bool changed = !PathUtil.Same(CurrentPath, full);
            if (addHistory && CurrentPath != null && changed)
            {
                _back.Push(CurrentPath);
                _forward.Clear();
            }
            if (changed) OnFolderChanged(CurrentPath, full);
            CurrentPath = full;
            _driveLastPath[DriveKey(full)] = full;
            ApplyItems(items, focusName, marks, keepState ? oldIndex : 0);
            _ = CheckBlockedAsync();
            HideError();
            PathBox.Text = full;
            PathChanged?.Invoke(this);
            UpdateStar();
            UpdateDriveSelection();
            SetupWatcher();
            _ = UpdateFreeSpaceAsync();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            if (_loadCts == cts) ShowError(ex.Message, full);
            return false;
        }
        finally
        {
            if (_loadCts == cts)
            {
                _loadCts = null;
                _loadingDelay.Stop();
                LoadingOverlay.Visibility = Visibility.Collapsed;
            }
            cts.Dispose();
        }
    }

    public void CancelLoad()
    {
        var cts = _loadCts;
        if (cts == null) return;
        _loadCts = null;
        _loadingDelay.Stop();
        LoadingOverlay.Visibility = Visibility.Collapsed;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        FlashStatus("불러오기를 취소했습니다.");
    }

    public Task<bool> RefreshAsync(string? focusName = null)
    {
        var path = CurrentPath ?? _failedPath;
        return path == null ? Task.FromResult(false) : NavigateAsync(path, focusName, addHistory: false, keepState: true);
    }

    public Task GoUpAsync()
    {
        if (CurrentPath == null) return Task.CompletedTask;
        var parent = PathUtil.Parent(CurrentPath);
        return parent == null ? Task.CompletedTask : NavigateAsync(parent, PathUtil.LastSegment(CurrentPath));
    }

    public async Task GoBackAsync()
    {
        if (_back.Count == 0) return;
        var target = _back.Pop();
        var cur = CurrentPath;
        if (await NavigateAsync(target, addHistory: false))
        {
            if (cur != null) _forward.Push(cur);
        }
        else
        {
            _back.Push(target);
        }
    }

    public async Task GoForwardAsync()
    {
        if (_forward.Count == 0) return;
        var target = _forward.Pop();
        var cur = CurrentPath;
        if (await NavigateAsync(target, addHistory: false))
        {
            if (cur != null) _back.Push(cur);
        }
        else
        {
            _forward.Push(target);
        }
    }

    private void ApplyItems(List<FileItem> items, string? focusName, HashSet<string>? marks, int fallbackIndex)
    {
        if (!PathUtil.IsRoot(CurrentPath!)) items.Add(FileItem.CreateParent(CurrentPath!));
        items.Sort(_comparer);

        _fileCount = _dirCount = _markedFiles = _markedDirs = 0;
        _totalSize = _markedSize = 0;
        foreach (var it in items)
        {
            if (it.IsParent) continue;
            if (it.IsDirectory) _dirCount++;
            else { _fileCount++; _totalSize += it.Size; }
            if (marks != null && marks.Contains(it.Name)) SetMark(it, true);
        }

        _items = items;
        FileList.ItemsSource = items;

        int idx = focusName != null ? items.FindIndex(i => string.Equals(i.Name, focusName, StringComparison.OrdinalIgnoreCase)) : -1;
        if (idx < 0) idx = Compat.Clamp(fallbackIndex, 0, Math.Max(0, items.Count - 1));
        bool typing = Keyboard.FocusedElement is TextBox or ComboBox or ComboBoxItem;   // 경로 입력·명령줄 입력 중
        SetCursor(idx, _host.ActivePanel == this && !typing);
        UpdateStatus();
    }

    private void OpenItem(FileItem it)
    {
        if (it.IsParent) _ = GoUpAsync();
        else if (it.IsDirectory) _ = NavigateAsync(it.FullPath);
        else _host.OpenWithShell(it.FullPath);
    }

    // ───────────────────────── 폴더 히스토리 ─────────────────────────

    /// <summary>폴더가 바뀌면 떠난 폴더를 최근 목록에 넣고, 체류 시간과 방문을 기록한다.</summary>
    private void OnFolderChanged(string? oldPath, string newPath)
    {
        _quick = "";   // 이전 폴더에서 치던 빠른 검색은 이어지지 않는다
        var history = _host.History;
        if (oldPath != null)
        {
            EndDwell(oldPath);
            history.AddRecent(oldPath);
        }
        history.RecordVisit(newPath);
        if (_shownInLayout) _dwellStart = Compat.TickCount64;
        history.Save();
    }

    private void EndDwell(string path)
    {
        if (_dwellStart == 0) return;
        _host.History.AddDwell(path, TimeSpan.FromMilliseconds(Compat.TickCount64 - _dwellStart));
        _dwellStart = 0;
    }

    /// <summary>지금까지 머문 시간을 기록 (종료 시). 측정은 계속된다.</summary>
    public void FlushDwell()
    {
        if (CurrentPath == null || _dwellStart == 0) return;
        EndDwell(CurrentPath);
        if (_shownInLayout) _dwellStart = Compat.TickCount64;
    }

    /// <summary>레이아웃에서 숨겨진 패널은 체류 시간을 세지 않는다.</summary>
    public void SetShownInLayout(bool shown)
    {
        if (_shownInLayout == shown) return;
        if (!shown && CurrentPath != null) EndDwell(CurrentPath);
        _shownInLayout = shown;
        if (shown && CurrentPath != null) _dwellStart = Compat.TickCount64;
    }

    private void History_Click(object sender, RoutedEventArgs e) => ShowHistoryMenu();

    // ───────────────────────── 별표 (자주 머문 폴더 올리기/내리기) ─────────────────────────

    private static readonly Brush StarOnBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0xB0, 0x05));

    private void Star_Click(object sender, RoutedEventArgs e)
    {
        ToggleStar();
        FocusList();
    }

    /// <summary>Ctrl+D / 별표 버튼: 현재 폴더를 자주 머문 폴더에 올리거나 내린다.</summary>
    public void ToggleStar()
    {
        if (CurrentPath == null) return;
        FlushDwell();
        bool added = _host.History.ToggleSmart(CurrentPath);   // Changed → 모든 패널 UpdateStar
        _host.History.Save();
        FlashStatus(added ? "★ 자주 머문 폴더에 올렸습니다 (Alt+↓ 에서 맨 위)" : "☆ 자주 머문 폴더에서 내렸습니다");
    }

    /// <summary>현재 폴더가 자주 머문 폴더 목록에 있으면 노란 별(채움), 아니면 빈 별</summary>
    private void UpdateStar()
    {
        bool on = CurrentPath != null && _host.History.IsSmart(CurrentPath);
        StarGlyph.Text = on ? "" : "";   // FavoriteStarFill / FavoriteStar
        StarGlyph.Foreground = on ? StarOnBrush : SystemColors.ControlTextBrush;
        StarButton.ToolTip = on
            ? "자주 머문 폴더에 있음 — 누르면 내립니다 (Ctrl+D)"
            : "자주 머문 폴더에 올리기 (Ctrl+D)";
    }

    /// <summary>히스토리 메뉴: 자주 머문 폴더(스마트) → 최근 폴더</summary>
    public void ShowHistoryMenu()
    {
        Activated?.Invoke(this);
        FlushDwell();   // 지금 폴더의 체류 시간도 점수에 반영
        var history = _host.History;
        var smart = history.GetSmart();
        var recent = history.GetRecent(smart.Select(s => s.Path));

        var menu = new ContextMenu
        {
            PlacementTarget = PathBox,
            Placement = PlacementMode.Bottom,
            MaxHeight = 640,
        };
        bool chosen = false;

        MenuItem Entry(string path, string? info)
        {
            // Header 를 TextBlock 으로: 문자열이면 '_' 가 단축키로 해석되어 사라진다
            var item = new MenuItem
            {
                Header = new TextBlock { Text = path },
                InputGestureText = info ?? "",
                FontWeight = PathUtil.Same(path, CurrentPath) ? FontWeights.SemiBold : FontWeights.Normal,
                ToolTip = path,
            };
            item.Click += async (_, _) =>
            {
                chosen = true;
                await NavigateAsync(path);
                FocusList();
            };
            return item;
        }

        MenuItem Title(string text) => new()
        {
            Header = new TextBlock { Text = text, FontWeight = FontWeights.Bold, Foreground = Brushes.DimGray },
            IsEnabled = false,
        };

        if (smart.Count > 0)
        {
            menu.Items.Add(Title("★ 자주 머문 폴더"));
            foreach (var s in smart)
                menu.Items.Add(Entry(s.Path, s.Pinned
                    ? (s.Visits > 0 ? $"★ 고정 · {s.Visits}회" : "★ 고정")
                    : $"{s.Visits}회 · {FormatDwell(s.DwellSeconds)}"));
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(Title($"최근 폴더 ({recent.Count}/{history.MaxHistory})"));
        if (recent.Count == 0)
            menu.Items.Add(new MenuItem { Header = new TextBlock { Text = "(없음)" }, IsEnabled = false });
        foreach (var p in recent) menu.Items.Add(Entry(p, null));

        menu.Items.Add(new Separator());
        var clear = new MenuItem { Header = new TextBlock { Text = "히스토리 지우기..." } };
        clear.Click += (_, _) =>
        {
            chosen = true;
            if (MessageBox.Show(Window.GetWindow(this), "최근 폴더와 자주 머문 폴더 기록을 모두 지울까요?", "히스토리 지우기",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            {
                history.Clear();
                history.Save();
            }
            FocusList();
        };
        menu.Items.Add(clear);

        menu.Opened += (_, _) =>
        {
            // 키보드로 바로 고를 수 있게 첫 항목에 포커스
            var first = menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.IsEnabled);
            first?.Focus();
        };
        menu.Closed += (_, _) => { if (!chosen) FocusList(); };
        menu.IsOpen = true;
    }

    private static string FormatDwell(double seconds) => seconds switch
    {
        < 60 => $"{seconds:0}초",
        < 3600 => $"{seconds / 60:0}분",
        _ => $"{seconds / 3600:0.#}시간",
    };

    // ───────────────────────── 커서 / 포커스 ─────────────────────────

    public void FocusList()
    {
        if (FileList.Items.Count == 0) { FileList.Focus(); return; }
        SetCursor(FileList.SelectedIndex < 0 ? 0 : FileList.SelectedIndex, true);
    }

    private void SetCursor(int index, bool focus)
    {
        if (index < 0 || index >= FileList.Items.Count) { if (focus) FileList.Focus(); return; }
        FileList.SelectedIndex = index;
        FileList.ScrollIntoView(FileList.Items[index]);
        if (!focus) return;
        if (FileList.ItemContainerGenerator.ContainerFromIndex(index) is not ListViewItem c)
        {
            FileList.UpdateLayout();
            c = (FileList.ItemContainerGenerator.ContainerFromIndex(index) as ListViewItem)!;
        }
        if (c != null) c.Focus(); else FileList.Focus();
    }

    private void MoveCursor(int delta) => SetCursor(Compat.Clamp(FileList.SelectedIndex + delta, 0, FileList.Items.Count - 1), true);

    public void FocusPathBox()
    {
        PathBox.Focus();
        PathBox.SelectAll();
    }

    /// <summary>삭제 후 커서를 둘 항목 이름 (삭제 대상이 아닌 다음 항목)</summary>
    public string? NameAfterRemoval(IReadOnlyCollection<FileItem> removed)
    {
        var set = removed.ToHashSet();
        int i = Math.Max(0, FileList.SelectedIndex);
        for (int k = i; k < _items.Count; k++) if (!set.Contains(_items[k])) return _items[k].Name;
        for (int k = i - 1; k >= 0; k--) if (!set.Contains(_items[k])) return _items[k].Name;
        return null;
    }

    // ───────────────────────── 선택(마크) ─────────────────────────

    public List<FileItem> GetSelectedOrCurrent()
    {
        var marked = _items.Where(i => i.IsMarked).ToList();
        if (marked.Count > 0) return marked;
        var c = CursorItem;
        return c == null || c.IsParent ? new List<FileItem>() : new List<FileItem> { c };
    }

    private void SetMark(FileItem it, bool value)
    {
        if (it.IsParent || it.IsMarked == value) return;
        it.IsMarked = value;
        int d = value ? 1 : -1;
        if (it.IsDirectory) _markedDirs += d;
        else { _markedFiles += d; _markedSize += d * it.Size; }
    }

    public void ClearMarks()
    {
        foreach (var it in _items) SetMark(it, false);
        UpdateStatus();
    }

    private void MarkAll(bool value)
    {
        foreach (var it in _items) SetMark(it, value);
        UpdateStatus();
    }

    private void InvertMarks()
    {
        foreach (var it in _items) if (!it.IsDirectory) SetMark(it, !it.IsMarked);
        UpdateStatus();
    }

    private void MarkByMask(bool value)
    {
        var mask = InputDialog.Show(Window.GetWindow(this), value ? "선택" : "선택 해제",
            "파일 마스크 (여러 개는 ; 로 구분):", "*.*");
        if (Compat.IsBlank(mask)) { FocusList(); return; }
        var patterns = mask.SplitTrim(';');
        foreach (var it in _items)
        {
            if (it.IsDirectory) continue;
            if (patterns.Any(p => Native.NativeMethods.PathMatchSpecW(it.Name, p)))
                SetMark(it, value);
        }
        UpdateStatus();
        FocusList();
    }

    // ───────────────────────── 상태 표시줄 ─────────────────────────

    private void UpdateStatus()
    {
        if (_flashTimer.IsEnabled) return;
        var s = $"{PathUtil.FormatSize(_markedSize)} / {PathUtil.FormatSize(_totalSize)},  파일 {_markedFiles:N0}/{_fileCount:N0},  폴더 {_markedDirs:N0}/{_dirCount:N0}";
        if (_blockedCount > 0) s += $"   │   차단 {_blockedCount:N0}";
        if (_freeText.Length > 0) s += $"   │   여유 {_freeText}";
        StatusText.Text = s;
    }

    public void FlashStatus(string text)
    {
        StatusText.Text = text;
        _flashTimer.Stop();
        _flashTimer.Start();
    }

    private async Task UpdateFreeSpaceAsync()
    {
        var path = CurrentPath;
        if (path == null) return;
        try
        {
            if (PathUtil.IsVirtual(path))
            {
                var fs = VirtualFs.For(path);
                var info = await GuardedIo.RunAsync(_ => fs.SpaceInfo(path), fs.IdleTimeoutMs, path);
                if (path == CurrentPath)
                    _freeText = info is { } i ? $"{PathUtil.FormatSize((long)i.Free)} / {PathUtil.FormatSize((long)i.Total)}" : "";
                UpdateStatus();
                return;
            }
            var (free, total) = await GuardedIo.RunAsync(_ =>
            {
                int rc = Native.NativeMethods.FmGetDiskFree(PathUtil.WithSlash(path), out var f, out var t);
                if (rc != 0) throw Win32Errors.ToIOException(rc, path);
                return (f, t);
            }, 3000, path);
            if (path == CurrentPath) _freeText = $"{PathUtil.FormatSize((long)free)} / {PathUtil.FormatSize((long)total)}";
        }
        catch
        {
            if (path == CurrentPath) _freeText = "";
        }
        UpdateStatus();
    }

    private void ShowError(string message, string? retryPath)
    {
        _failedPath = retryPath;
        ErrorText.Text = message;
        ErrorBar.Visibility = Visibility.Visible;
    }

    private void HideError()
    {
        _failedPath = null;
        ErrorBar.Visibility = Visibility.Collapsed;
    }

    private void CloseError_Click(object sender, RoutedEventArgs e) { HideError(); FocusList(); }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        NetworkHealth.InvalidateAll();
        var path = _failedPath ?? CurrentPath;
        if (path != null) await NavigateAsync(path);
        FocusList();
    }

    // ───────────────────────── 자동 새로고침 ─────────────────────────
    //  로컬 드라이브: Windows 변경 알림(FileSystemWatcher)
    //  네트워크 공유(SMB): 변경 알림(백그라운드에서 3초 제한으로 생성) + 주기 확인
    //  WebDAV 드라이브·FTP·WebDAV·휴대폰: 알림이 없으므로 주기 확인
    //  주기 확인은 목록을 다시 읽어 실제로 달라졌을 때만 화면을 바꾼다.

    private const int NetworkPollMs = 5000;
    private const int RemotePollMs = 8000;
    private const int MaxPollMs = 60000;
    private int _pollBaseMs;
    private bool _polling;

    private void SetupWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
        _pollTimer.Stop();
        var path = CurrentPath;
        if (path == null) return;

        if (PathUtil.IsVirtual(path))
        {
            StartPolling(RemotePollMs);
            return;
        }

        bool network;
        try
        {
            network = path.StartsWith(@"\\") || new DriveInfo(PathUtil.Root(path)).DriveType == DriveType.Network;
        }
        catch
        {
            return;
        }

        if (network)
        {
            StartPolling(NetworkPollMs);
            _ = TryNetworkWatcherAsync(path);
            return;
        }

        try
        {
            var type = new DriveInfo(PathUtil.Root(path)).DriveType;
            if (type is DriveType.Fixed or DriveType.Removable) _watcher = CreateWatcher(path);
        }
        catch
        {
            /* 감시 실패 시 수동 새로고침 */
        }
    }

    private FileSystemWatcher CreateWatcher(string path)
    {
        var w = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size
                         | NotifyFilters.LastWrite | NotifyFilters.Attributes,
            InternalBufferSize = 64 * 1024,
        };
        FileSystemEventHandler h = (_, _) => Dispatcher.BeginInvoke(OnWatcherEvent);
        w.Created += h;
        w.Deleted += h;
        w.Changed += h;
        w.Renamed += (_, _) => Dispatcher.BeginInvoke(OnWatcherEvent);
        w.Error += (_, _) => Dispatcher.BeginInvoke(OnWatcherEvent);
        w.EnableRaisingEvents = true;   // 네트워크에서는 여기서 멈출 수 있다 → 작업 스레드에서만
        return w;
    }

    /// <summary>SMB 공유는 변경 알림을 지원한다. 서버가 응답할 때만, 작업 스레드에서 3초 제한으로 만든다.</summary>
    private async Task TryNetworkWatcherAsync(string path)
    {
        try
        {
            var (ok, _) = await NetworkHealth.CheckPathAsync(path);
            if (!ok) return;
            var w = await GuardedIo.RunAsync(_ => CreateWatcher(path), 3000, path);
            if (PathUtil.Same(CurrentPath, path) && _watcher == null) _watcher = w;
            else w.Dispose();
        }
        catch
        {
            /* 알림을 못 쓰면 주기 확인만 */
        }
    }

    private void StartPolling(int baseMs)
    {
        _pollBaseMs = baseMs;
        _pollTimer.Interval = TimeSpan.FromMilliseconds(baseMs);
        _pollTimer.Start();
    }

    /// <summary>목록을 다시 읽어 달라졌으면 반영한다. 실패는 조용히 넘기고 다음 주기에 다시 본다.</summary>
    private async Task PollAsync()
    {
        var path = CurrentPath;
        if (_polling || path == null || IsLoading || !_shownInLayout) return;
        if (Window.GetWindow(this) is { WindowState: WindowState.Minimized }) return;

        _polling = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var items = await DirectoryLoader.LoadAsync(path, _host.ShowHidden, null, CancellationToken.None);
            // 큰 폴더·느린 서버는 확인 간격을 늘린다 (읽는 시간의 4배, 최대 1분)
            int next = Compat.Clamp((int)Math.Max(_pollBaseMs, sw.ElapsedMilliseconds * 4), _pollBaseMs, MaxPollMs);
            _pollTimer.Interval = TimeSpan.FromMilliseconds(next);

            if (!PathUtil.Same(path, CurrentPath) || IsLoading) return;
            if (Signature(items) == Signature(_items)) return;
            ApplyRefreshed(items);
        }
        catch
        {
            /* 네트워크 문제 등: 오류 표시 없이 다음 주기에 */
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>새로 읽은 목록을 표시 (커서·선택 표시 유지)</summary>
    /// <summary>
    /// 목록을 읽은 뒤 '인터넷에서 받은 파일' 표시가 있는 파일을 백그라운드에서 찾아 자물쇠로 표시한다.
    /// 로컬은 모든 파일, 네트워크 공유는 처음 5,000개까지 (목록 표시는 기다리지 않는다).
    /// </summary>
    private async Task CheckBlockedAsync()
    {
        var path = CurrentPath;
        int gen = ++_zoneGeneration;
        _blockedCount = 0;
        if (path == null || PathUtil.IsVirtual(path)) { UpdateStatus(); return; }

        var files = _items.Where(i => !i.IsDirectory).ToList();
        if (files.Count == 0) { UpdateStatus(); return; }
        bool network;
        try { network = path.StartsWith(@"\\") || new DriveInfo(PathUtil.Root(path)).DriveType == DriveType.Network; }
        catch { return; }
        if (network && files.Count > 5000) files = files.Take(5000).ToList();

        try
        {
            var names = files.Select(f => f.Name).ToList();
            var flags = await GuardedIo.RunAsync(ctx => ZoneCheck.Check(path, names, ctx), 5000, path);
            if (gen != _zoneGeneration || !PathUtil.Same(path, CurrentPath)) return;
            int n = 0;
            for (int i = 0; i < files.Count; i++)
            {
                files[i].IsBlocked = flags[i];
                if (flags[i]) n++;
            }
            _blockedCount = n;
            UpdateStatus();
        }
        catch
        {
            /* 확인 실패는 표시만 안 한다 */
        }
    }

    private void ApplyRefreshed(List<FileItem> items)
    {
        var marks = _items.Where(i => i.IsMarked).Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ApplyItems(items, CursorItem?.Name, marks, FileList.SelectedIndex);
        _ = CheckBlockedAsync();
    }

    private static string Signature(IEnumerable<FileItem> items)
        => string.Join("\n", items.Where(i => !i.IsParent)
            .Select(i => $"{i.Name}|{i.Size}|{i.LastWriteRaw}|{(i.IsDirectory ? 1 : 0)}")
            .OrderBy(s => s, StringComparer.Ordinal));

    private void OnWatcherEvent()
    {
        long now = Compat.TickCount64;
        if (_watchFirstEvent == 0) _watchFirstEvent = now;
        _watchDebounce.Stop();
        // 계속 바뀌는 중(대용량 복사 등)이라도 2초마다는 갱신
        if (now - _watchFirstEvent > 2000 && !IsLoading)
        {
            _watchFirstEvent = 0;
            _ = RefreshAsync();
            return;
        }
        _watchDebounce.Start();
    }

    // ───────────────────────── 정렬 ─────────────────────────

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } col }) return;
        SortColumn column =
            col == ExtColumn ? SortColumn.Extension :
            col == SizeColumn ? SortColumn.Size :
            col == DateColumn ? SortColumn.Date :
            col == AttrColumn ? SortColumn.Attributes : SortColumn.Name;

        if (_comparer.Column == column) _comparer.Descending = !_comparer.Descending;
        else { _comparer.Column = column; _comparer.Descending = false; }
        UpdateHeaders();

        var cur = CursorItem;
        _items.Sort(_comparer);
        FileList.ItemsSource = null;
        FileList.ItemsSource = _items;
        SetCursor(cur != null ? _items.IndexOf(cur) : 0, true);
    }

    private void UpdateHeaders()
    {
        string Arrow(SortColumn c) => _comparer.Column != c ? "" : _comparer.Descending ? " ▼" : " ▲";
        NameColumn.Header = "이름" + Arrow(SortColumn.Name);
        ExtColumn.Header = "확장자" + Arrow(SortColumn.Extension);
        SizeColumn.Header = "크기" + Arrow(SortColumn.Size);
        DateColumn.Header = "날짜" + Arrow(SortColumn.Date);
        AttrColumn.Header = "속성" + Arrow(SortColumn.Attributes);
    }

    // ───────────────────────── 드라이브 / 경로 입력 ─────────────────────────

    private void PopulateDrives()
    {
        _updatingDrives = true;
        try
        {
            _drives.Clear();
            foreach (var d in DriveInfo.GetDrives())
            {
                string kind = d.DriveType switch
                {
                    DriveType.Network => "네트워크",
                    DriveType.Removable => "이동식",
                    DriveType.CDRom => "CD",
                    _ => "",
                };
                var name = d.Name.TrimEnd('\\');
                _drives.Add(new DriveEntry(name, kind.Length > 0 ? $"{name}  {kind}" : name));
            }
            // 등록된 FTP / WebDAV 연결 (연결 관리: Ctrl+F)
            foreach (var c in RemoteConnections.All)
                _drives.Add(new DriveEntry(c.RootPath, $"🌐 {c.Name}  {c.KindText}"));
            if (DriveCombo.ItemsSource == null) DriveCombo.ItemsSource = _drives;
            UpdateDriveSelection();
        }
        finally
        {
            _updatingDrives = false;
        }
        _ = AddPhonesAsync();
    }

    /// <summary>USB 로 연결된 휴대폰(MTP)을 드라이브 목록 뒤에 붙인다. 느릴 수 있어 비동기.</summary>
    private async Task AddPhonesAsync()
    {
        var devices = await Mtp.ListDevicesAsync();
        _updatingDrives = true;
        try
        {
            foreach (var d in devices)
            {
                var root = Mtp.Prefix + d.Name;
                if (_drives.Any(x => x.Root.Equals(root, StringComparison.OrdinalIgnoreCase))) continue;
                _drives.Add(new DriveEntry(root, "📱 " + d.Name));
            }
            UpdateDriveSelection();
        }
        finally
        {
            _updatingDrives = false;
        }
    }

    private void UpdateDriveSelection()
    {
        _updatingDrives = true;
        try
        {
            var root = CurrentPath == null ? null : DriveKey(CurrentPath);
            DriveCombo.SelectedItem = _drives
                .FirstOrDefault(d => string.Equals(d.Root, root, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _updatingDrives = false;
        }
    }

    /// <summary>"C:\a\b" → "C:", "\\srv\share\x" → "\\srv\share"</summary>
    private static string DriveKey(string path) => PathUtil.Root(path).TrimEnd('\\');

    public void OpenDriveMenu()
    {
        Activated?.Invoke(this);
        PopulateDrives();
        DriveCombo.Focus();
        DriveCombo.IsDropDownOpen = true;
    }

    private void DriveCombo_DropDownOpened(object? sender, EventArgs e)
    {
        if (!_updatingDrives) PopulateDrives();
    }

    private void DriveCombo_DropDownClosed(object? sender, EventArgs e)
    {
        GoToSelectedDrive();
        FocusList();
    }

    private void DriveCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingDrives && !DriveCombo.IsDropDownOpen) GoToSelectedDrive();
    }

    private void GoToSelectedDrive()
    {
        if (DriveCombo.SelectedItem is not DriveEntry d) return;
        if (CurrentPath != null && string.Equals(DriveKey(CurrentPath), d.Root, StringComparison.OrdinalIgnoreCase)) return;
        var path = _driveLastPath.TryGetValue(d.Root, out var last) ? last : PathUtil.IsVirtual(d.Root) ? d.Root : d.Root + "\\";
        _ = NavigateAsync(path);
    }

    private async void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            var text = PathBox.Text;
            FocusList();
            if (!await NavigateAsync(text)) PathBox.Text = CurrentPath ?? text;
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            PathBox.Text = CurrentPath ?? "";
            FocusList();
        }
    }

    private void Up_Click(object sender, RoutedEventArgs e) { _ = GoUpAsync(); FocusList(); }

    private void Root_Click(object sender, RoutedEventArgs e)
    {
        _ = GoRootAsync();
        FocusList();
    }

    /// <summary>Ctrl+\ / '\' 버튼: 드라이브(공유·연결·기기) 루트로</summary>
    public Task GoRootAsync()
    {
        if (CurrentPath == null || PathUtil.IsRoot(CurrentPath)) return Task.CompletedTask;
        return NavigateAsync(PathUtil.Root(CurrentPath));
    }

    // ───────────────────────── 키보드 ─────────────────────────

    private void FileList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool handled = true;

        switch (key)
        {
            case Key.Enter when mods == ModifierKeys.None:
                if (CursorItem is { } it) OpenItem(it);
                break;
            case Key.Back when mods == ModifierKeys.None:
                // 빠른 검색을 막 입력하는 중(1.5초 안)일 때만 글자를 지우고, 아니면 상위 폴더로
                ResetQuickIfStale(force: false);
                if (_quick.Length > 0)
                {
                    _quick = _quick[..^1];
                    _quickTick = Compat.TickCount64;
                    QuickSearch();
                }
                else _ = GoUpAsync();
                break;
            case Key.PageUp when mods == ModifierKeys.Control:
                _ = GoUpAsync();
                break;
            case Key.PageDown when mods == ModifierKeys.Control:
                if (CursorItem is { IsDirectory: true } d) OpenItem(d);
                break;
            case Key.Insert when mods == ModifierKeys.None:
                if (CursorItem is { } ins) SetMark(ins, !ins.IsMarked);
                UpdateStatus();
                MoveCursor(+1);
                break;
            case Key.Space when mods == ModifierKeys.None && _quick.Length == 0:
                if (CursorItem is { } sp) SetMark(sp, !sp.IsMarked);
                UpdateStatus();
                break;
            case Key.Up or Key.Down when mods == ModifierKeys.Shift:
                if (CursorItem is { } sh) SetMark(sh, !sh.IsMarked);
                UpdateStatus();
                MoveCursor(key == Key.Down ? +1 : -1);
                break;
            case Key.Add when mods == ModifierKeys.None:
                MarkByMask(true);
                break;
            case Key.Subtract when mods == ModifierKeys.None:
                MarkByMask(false);
                break;
            case Key.Multiply when mods == ModifierKeys.None:
                InvertMarks();
                break;
            case Key.A when mods == ModifierKeys.Control:
                MarkAll(true);
                break;
            case Key.Escape when mods == ModifierKeys.None && _quick.Length > 0:
                _quick = "";
                UpdateStatus();
                break;
            default:
                handled = false;
                break;
        }
        if (handled) e.Handled = true;
        else if (key is not (Key.LeftShift or Key.RightShift)) ResetQuickIfStale(force: key is Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown);
    }

    /// <summary>빠른 검색: 글자를 치면 그 글자로 시작하는 항목으로 이동</summary>
    private void FileList_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0) return;
        if (Compat.IsEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        if (e.Text == " " && _quick.Length == 0) return;

        ResetQuickIfStale(force: false);
        _quick += e.Text;
        _quickTick = Compat.TickCount64;
        QuickSearch();
        e.Handled = true;
    }

    private void ResetQuickIfStale(bool force)
    {
        if (_quick.Length == 0) return;
        if (force || Compat.TickCount64 - _quickTick > 1500)
        {
            _quick = "";
            UpdateStatus();
        }
    }

    private void QuickSearch()
    {
        if (_quick.Length == 0) { UpdateStatus(); return; }
        int idx = _items.FindIndex(i => !i.IsParent && i.Name.StartsWith(_quick, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) idx = _items.FindIndex(i => !i.IsParent && i.Name.Contains(_quick, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) SetCursor(idx, true);
        StatusText.Text = $"빠른 검색: {_quick}" + (idx < 0 ? "  (없음)" : "");
    }

    // ───────────────────────── 마우스 / 드래그 앤 드롭 ─────────────────────────

    private static FileItem? ItemFromSource(object? source)
    {
        var d = source as DependencyObject;
        while (d != null && d is not ListViewItem)
            d = d is Visual or Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return (d as ListViewItem)?.DataContext as FileItem;
    }

    private void Item_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { DataContext: FileItem it } && e.ChangedButton == MouseButton.Left)
        {
            OpenItem(it);
            e.Handled = true;
        }
    }

    private void FileList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragItem = null;
        var item = ItemFromSource(e.OriginalSource);
        if (item == null) return;
        var mods = Keyboard.Modifiers;

        if (mods == ModifierKeys.Control)
        {
            SetMark(item, !item.IsMarked);
            UpdateStatus();
            SetCursor(_items.IndexOf(item), true);
            e.Handled = true;
        }
        else if (mods == ModifierKeys.Shift)
        {
            int from = Math.Max(0, FileList.SelectedIndex), to = _items.IndexOf(item);
            for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++) SetMark(_items[i], true);
            UpdateStatus();
            SetCursor(to, true);
            e.Handled = true;
        }
        else if (e.ClickCount == 1)
        {
            _dragStart = e.GetPosition(FileList);
            _dragItem = item;
        }
    }

    private void FileList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _dragItem = null;

    private void FileList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = ItemFromSource(e.OriginalSource);
        if (item != null) SetCursor(_items.IndexOf(item), true);
    }

    private void FileList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem == null || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(FileList);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var item = _dragItem;
        _dragItem = null;
        if (item.IsParent) return;

        var items = item.IsMarked ? _items.Where(i => i.IsMarked).ToList() : new List<FileItem> { item };
        var paths = items.Select(i => i.FullPath).ToArray();
        var data = new DataObject();
        data.SetData(PathsFormat, paths);
        if (!paths.Any(PathUtil.IsVirtual)) data.SetData(DataFormats.FileDrop, paths);
        data.SetData(DragFormat, Index.ToString());
        var effect = DragDrop.DoDragDrop(FileList, data, DragDropEffects.Copy | DragDropEffects.Move);
        if (effect != DragDropEffects.None) _ = RefreshAsync();   // 탐색기 등 외부로 이동된 경우
    }

    /// <summary>폴더 항목 위면 그 폴더, ".." 위면 상위 폴더, 아니면 현재 폴더</summary>
    private string? DropTargetDir(object? source)
    {
        if (CurrentPath == null) return null;
        var item = ItemFromSource(source);
        if (item is { IsDirectory: true }) return item.FullPath;
        return CurrentPath;
    }

    private static string[]? GetDropPaths(IDataObject data)
    {
        if (data.GetDataPresent(PathsFormat) && data.GetData(PathsFormat) is string[] own && own.Length > 0) return own;
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) return files;
        return null;
    }

    private static string[] FilterDropPaths(string[] paths, string targetDir)
        => paths.Where(p => !PathUtil.Same(p, targetDir) && !PathUtil.Same(PathUtil.Parent(p), targetDir)).ToArray();

    private void FileList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;

        var target = DropTargetDir(e.OriginalSource);
        if (target == null || GetDropPaths(e.Data) is not { } paths) return;
        if (FilterDropPaths(paths, target).Length == 0) return;

        bool move = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0;
        e.Effects = (move ? DragDropEffects.Move : DragDropEffects.Copy) & e.AllowedEffects;
        StatusText.Text = $"→ {target}   (Ctrl: 바로 복사, Shift: 바로 이동, 그냥 놓으면 메뉴)";
    }

    private void FileList_DragLeave(object sender, DragEventArgs e) => UpdateStatus();

    private void FileList_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;   // 작업은 우리가 직접 한다
        UpdateStatus();

        var target = DropTargetDir(e.OriginalSource);
        if (target == null || GetDropPaths(e.Data) is not { } raw) return;
        var paths = FilterDropPaths(raw, target);
        if (paths.Length == 0) return;

        FilePanel? source = null;
        if (e.Data.GetDataPresent(DragFormat) && int.TryParse(e.Data.GetData(DragFormat) as string, out var idx))
            source = _host.PanelAt(idx);

        bool ctrl = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
        bool shift = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0;

        void Start(OpKind kind) => _host.StartDropOperation(kind, paths, target, source);

        // 드래그 루프가 끝난 뒤 실행
        Dispatcher.BeginInvoke(() =>
        {
            if (shift) Start(OpKind.Move);
            else if (ctrl) Start(OpKind.Copy);
            else ShowDropMenu(paths.Length, target, Start);
        });
    }

    private void ShowDropMenu(int count, string target, Action<OpKind> start)
    {
        var menu = new ContextMenu { PlacementTarget = FileList, Placement = PlacementMode.MousePoint };
        menu.Items.Add(new MenuItem { Header = $"대상: {target}", IsEnabled = false });
        menu.Items.Add(new Separator());
        var copy = new MenuItem { Header = $"여기에 복사 ({count}개)", FontWeight = FontWeights.SemiBold };
        copy.Click += (_, _) => start(OpKind.Copy);
        var move = new MenuItem { Header = $"여기로 이동 ({count}개)" };
        move.Click += (_, _) => start(OpKind.Move);
        menu.Items.Add(copy);
        menu.Items.Add(move);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "취소" });
        menu.IsOpen = true;
    }

    // ───────────────────────── 컨텍스트 메뉴 ─────────────────────────

    /// <summary>오른쪽 버튼을 놓을 때 탐색기처럼 메뉴 (항목 위 = 항목 메뉴, 빈 곳 = 폴더 메뉴)</summary>
    private void FileList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var item = ItemFromSource(e.OriginalSource);
        if (item == null && e.OriginalSource is DependencyObject d && IsInScrollBarOrHeader(d)) return;
        var screen = FileList.PointToScreen(e.GetPosition(FileList));
        _host.ShowContextMenu(this, item, screen);
    }

    private static bool IsInScrollBarOrHeader(DependencyObject d)
    {
        for (var x = d; x != null; x = x is Visual or Visual3D ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x is ScrollBar or GridViewColumnHeader) return true;
        return false;
    }

    /// <summary>키보드로 메뉴를 열 때 위치: 커서 항목 아래쪽 (화면 좌표)</summary>
    public Point CursorScreenPoint()
    {
        int idx = FileList.SelectedIndex;
        if (idx >= 0 && FileList.ItemContainerGenerator.ContainerFromIndex(idx) is ListViewItem c && c.IsVisible)
            return c.PointToScreen(new Point(24, c.ActualHeight));
        return FileList.PointToScreen(new Point(24, 24));
    }
}
