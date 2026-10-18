using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using MPCommander.IO;
using MPCommander.Model;

namespace MPCommander.Views;

/// <summary>
/// 간단한 CSV 편집기 (F4 로 .csv / .tsv). 표(DataGrid)로 칸을 고치고, 줄·열을 더하거나 지우고, Excel 에서 복사한 범위를 붙여넣는다.
/// 읽은 인코딩(UTF-8 / UTF-8 BOM / CP949)·구분자·줄바꿈·따옴표 모양을 지켜 저장한다.
/// </summary>
public partial class CsvEditorWindow : Window
{
    private readonly string _path;
    private CsvDocument? _doc;
    private ObservableCollection<CsvRow> _rows = new();
    private int _columns;
    private bool _dirty;
    private bool _ready;

    public CsvEditorWindow(string path)
    {
        InitializeComponent();
        LocUi.Apply(this);   // XAML 글자를 지금 언어로
        _path = path;
        Title = $"{Path.GetFileName(path)} — {Loc.T("CSV 편집")}";
        Loaded += async (_, _) => await LoadAsync();
        Closing += OnClosing;
        PreviewKeyDown += OnWindowKeyDown;
    }

    // ───────────────────────── 읽기 ─────────────────────────

    private async Task LoadAsync()
    {
        try
        {
            // 큰 파일도 화면이 멈추지 않도록 읽기·나누기는 작업 스레드에서
            var doc = await Task.Run(() => CsvDocument.Load(_path));
            _doc = doc;
            _rows = new ObservableCollection<CsvRow>(doc.Rows);
            foreach (var r in _rows) r.Changed += OnRowChanged;
            _columns = Math.Max(doc.ColumnCount, 1);
            RebuildColumns();
            Grid.ItemsSource = _rows;
            EncodingCombo.SelectedIndex = (int)doc.Encoding;
            DelimiterCombo.SelectedIndex = doc.Delimiter switch { ';' => 1, '\t' => 2, _ => 0 };
            LoadingText.Visibility = Visibility.Collapsed;
            _ready = true;
            UpdateStatus();
            Grid.Focus();
        }
        catch (Exception ex)
        {
            LoadingText.Text = Loc.T("열 수 없습니다: ") + ex.Message;
        }
    }

    private static string ColumnName(int i)
    {
        var s = "";
        for (i++; i > 0; i = (i - 1) / 26) s = (char)('A' + (i - 1) % 26) + s;
        return s;
    }

    /// <summary>열 수가 바뀌면 다시 만든다. 칸은 줄 객체의 [i] 에 묶인다</summary>
    private void RebuildColumns()
    {
        Grid.Columns.Clear();
        var display = new Style(typeof(TextBlock));
        display.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        display.Setters.Add(new Setter(TextBlock.MaxHeightProperty, 60.0));
        var editing = new Style(typeof(TextBox));
        editing.Setters.Add(new EventSetter(PreviewKeyDownEvent, new KeyEventHandler(EditingBox_PreviewKeyDown)));
        for (int i = 0; i < _columns; i++)
        {
            Grid.Columns.Add(new DataGridTextColumn
            {
                Header = ColumnName(i),
                Binding = new Binding($"[{i}]") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus },
                ElementStyle = display,
                EditingElementStyle = editing,
                MinWidth = 60,
                MaxWidth = 600,
            });
        }
    }

    /// <summary>칸을 고치는 중 Alt+Enter = 칸 안 줄바꿈 (Excel 처럼). Enter 는 확정</summary>
    private void EditingBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Alt && sender is TextBox tb)
        {
            int at = tb.CaretIndex;
            tb.Text = tb.Text.Insert(at, "\n");
            tb.CaretIndex = at + 1;
            e.Handled = true;
        }
    }

    private void Grid_LoadingRow(object sender, DataGridRowEventArgs e) => e.Row.Header = (e.Row.GetIndex() + 1).ToString("N0");

    private void OnRowChanged(CsvRow _) => MarkDirty();

    /// <summary>줄을 넣거나 지우면 이미 그려진 줄의 번호를 다시 매긴다 (가상화로 새로 그려지는 줄은 LoadingRow 가 한다)</summary>
    private void RefreshRowHeaders()
    {
        for (int i = 0; i < _rows.Count; i++)
            if (Grid.ItemContainerGenerator.ContainerFromIndex(i) is DataGridRow row) row.Header = (i + 1).ToString("N0");
    }

    private void MarkDirty()
    {
        if (_dirty) return;
        _dirty = true;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_doc == null) return;
        string enc = _doc.Encoding switch { CsvEncoding.Utf8Bom => "UTF-8 (BOM)", CsvEncoding.Cp949 => "CP949", _ => "UTF-8" };
        string delim = _doc.Delimiter switch { '\t' => Loc.T("탭"), ';' => ";", _ => "," };
        string nl = _doc.NewLine == "\n" ? "LF" : "CRLF";
        StatusText.Text = Loc.F($"{_rows.Count:N0}줄 × {_columns:N0}열   │   {enc}   │   구분자 {delim}   │   {nl}") +
                          (_dirty ? Loc.T("   │   고침 (Ctrl+S 저장)") : "") +
                          Loc.T("   │   Alt+Enter: 칸 안 줄바꿈, Ctrl+V: 붙여넣기, Delete: 지우기");
        Title = (_dirty ? "* " : "") + $"{Path.GetFileName(_path)} — {Loc.T("CSV 편집")}";
    }

    // ───────────────────────── 편집 명령 ─────────────────────────

    private bool IsEditingCell => Keyboard.FocusedElement is TextBox;

    /// <summary>지금 칸 (줄 번호, 열 번호). 없으면 (-1, -1)</summary>
    private (int Row, int Col) Current()
    {
        var cell = Grid.CurrentCell;
        int r = cell.Item is CsvRow row ? _rows.IndexOf(row) : -1;
        int c = cell.Column?.DisplayIndex ?? -1;
        if (r < 0 && Grid.SelectedCells.Count > 0)
        {
            var sc = Grid.SelectedCells[0];
            r = sc.Item is CsvRow sr ? _rows.IndexOf(sr) : -1;
            c = sc.Column?.DisplayIndex ?? -1;
        }
        return (r, c);
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            _ = SaveAsync();
        }
    }

    private void Grid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsEditingCell || !_ready) return;
        var mods = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.V when mods == ModifierKeys.Control:
                e.Handled = true;
                Paste();
                break;
            case Key.Delete when mods == ModifierKeys.None:
                e.Handled = true;
                ClearSelected();
                break;
            case Key.Delete when mods == ModifierKeys.Control:
                e.Handled = true;
                DeleteRows();
                break;
            case Key.Enter when mods == ModifierKeys.Control:
                e.Handled = true;
                InsertRow();
                break;
        }
    }

    private void InsertRow_Click(object sender, RoutedEventArgs e) => InsertRow();
    private void DeleteRows_Click(object sender, RoutedEventArgs e) => DeleteRows();
    private void InsertColumn_Click(object sender, RoutedEventArgs e) => InsertColumn();
    private void DeleteColumn_Click(object sender, RoutedEventArgs e) => DeleteColumn();

    private CsvRow NewRow()
    {
        var row = new CsvRow(Enumerable.Repeat("", _columns).ToList());
        row.Changed += OnRowChanged;
        return row;
    }

    private void InsertRow()
    {
        if (!_ready) return;
        Grid.CommitEdit(DataGridEditingUnit.Row, true);
        var (r, c) = Current();
        int at = r < 0 ? _rows.Count : r + 1;
        var row = NewRow();
        _rows.Insert(at, row);
        RefreshRowHeaders();
        MarkDirty();
        UpdateStatus();
        Select(at, Math.Max(c, 0));
    }

    private void DeleteRows()
    {
        if (!_ready) return;
        Grid.CommitEdit(DataGridEditingUnit.Row, true);
        var rows = Grid.SelectedCells.Select(sc => sc.Item).OfType<CsvRow>().Distinct().ToList();
        if (rows.Count == 0 && Grid.CurrentCell.Item is CsvRow cur) rows.Add(cur);
        if (rows.Count == 0) return;
        if (rows.Count > 1 && MessageBox.Show(this, Loc.F($"{rows.Count:N0}줄을 삭제할까요?"), Loc.T("CSV 편집"),
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        int first = rows.Min(x => _rows.IndexOf(x));
        foreach (var x in rows) _rows.Remove(x);
        Grid.UpdateLayout();
        RefreshRowHeaders();
        MarkDirty();
        UpdateStatus();
        if (_rows.Count > 0) Select(Math.Min(first, _rows.Count - 1), Math.Max(Current().Col, 0));
    }

    private void InsertColumn()
    {
        if (!_ready) return;
        Grid.CommitEdit(DataGridEditingUnit.Row, true);
        int at = Current().Col + 1;
        if (at <= 0) at = _columns;
        foreach (var row in _rows) row.InsertCell(at);
        _columns++;
        RebuildColumns();
        MarkDirty();
        UpdateStatus();
    }

    private void DeleteColumn()
    {
        if (!_ready || _columns <= 1) return;
        Grid.CommitEdit(DataGridEditingUnit.Row, true);
        int col = Current().Col;
        if (col < 0) return;
        if (MessageBox.Show(this, Loc.F($"{ColumnName(col)} 열을 삭제할까요?"), Loc.T("CSV 편집"),
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        foreach (var row in _rows) row.RemoveCell(col);
        _columns--;
        RebuildColumns();
        MarkDirty();
        UpdateStatus();
    }

    /// <summary>Delete: 고른 칸들을 비운다</summary>
    private void ClearSelected()
    {
        foreach (var sc in Grid.SelectedCells.ToList())
            if (sc.Item is CsvRow row && sc.Column != null && row[sc.Column.DisplayIndex].Length > 0)
                row[sc.Column.DisplayIndex] = "";
    }

    /// <summary>Ctrl+V: 클립보드의 표(탭·줄바꿈, Excel 복사)를 지금 칸부터 채운다. 모자라면 줄·열을 늘린다</summary>
    private void Paste()
    {
        string text;
        try { text = Clipboard.GetText(); }
        catch { return; }
        if (string.IsNullOrEmpty(text)) return;
        var clip = new CsvDocument { Delimiter = '\t' };
        clip.Parse(text);
        if (clip.Rows.Count == 0) return;

        var (r0, c0) = Current();
        if (r0 < 0) r0 = 0;
        if (c0 < 0) c0 = 0;
        int needCols = c0 + clip.ColumnCount;
        if (needCols > _columns) { _columns = needCols; RebuildColumns(); }
        for (int i = 0; i < clip.Rows.Count; i++)
        {
            while (r0 + i >= _rows.Count) _rows.Add(NewRow());
            var src = clip.Rows[i];
            var dst = _rows[r0 + i];
            for (int j = 0; j < src.Count; j++) dst[c0 + j] = src[j];
        }
        MarkDirty();
        UpdateStatus();
    }

    private void Select(int row, int col)
    {
        if (row < 0 || row >= _rows.Count || col < 0 || col >= Grid.Columns.Count) return;
        Grid.SelectedCells.Clear();
        var info = new DataGridCellInfo(_rows[row], Grid.Columns[col]);
        Grid.CurrentCell = info;
        Grid.SelectedCells.Add(info);
        Grid.ScrollIntoView(_rows[row], Grid.Columns[col]);
        Grid.Focus();
    }

    private void Encoding_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _doc == null) return;
        var enc = (CsvEncoding)EncodingCombo.SelectedIndex;
        if (enc == _doc.Encoding) return;
        _doc.Encoding = enc;
        MarkDirty();
        UpdateStatus();
    }

    private void Delimiter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _doc == null) return;
        var d = DelimiterCombo.SelectedIndex switch { 1 => ';', 2 => '\t', _ => ',' };
        if (d == _doc.Delimiter) return;
        _doc.Delimiter = d;
        MarkDirty();
        UpdateStatus();
    }

    // ───────────────────────── 저장 ─────────────────────────

    private void Save_Click(object sender, RoutedEventArgs e) => _ = SaveAsync();

    private async Task<bool> SaveAsync()
    {
        if (!_ready || _doc == null) return false;
        Grid.CommitEdit(DataGridEditingUnit.Cell, true);
        Grid.CommitEdit(DataGridEditingUnit.Row, true);
        _doc.Rows.Clear();
        _doc.Rows.AddRange(_rows);
        var doc = _doc;
        SaveButton.IsEnabled = false;
        try
        {
            await Task.Run(() => doc.Save(_path));
            _dirty = false;
            UpdateStatus();
            StatusText.Text = Loc.F($"저장했습니다: {_path}   │   ") + StatusText.Text;
            return true;
        }
        catch (EncoderFallbackException)
        {
            MessageBox.Show(this, Loc.T("CP949 로 나타낼 수 없는 글자가 있어 저장하지 않았습니다. 인코딩을 UTF-8 로 바꿔 저장하세요."),
                Loc.T("CSV 편집"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Loc.T("저장하지 못했습니다: ") + ex.Message, Loc.T("CSV 편집"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally { SaveButton.IsEnabled = true; }
    }

    private bool _closingAfterSave;

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingAfterSave) return;
        Grid.CommitEdit(DataGridEditingUnit.Cell, true);
        Grid.CommitEdit(DataGridEditingUnit.Row, true);
        if (!_dirty) return;
        var r = MessageBox.Show(this, Loc.F($"{Path.GetFileName(_path)} 의 고친 내용을 저장할까요?"), Loc.T("CSV 편집"),
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (r == MessageBoxResult.No) return;
        e.Cancel = true;
        if (r == MessageBoxResult.Cancel) return;
        if (await SaveAsync())
        {
            _closingAfterSave = true;
            Close();
        }
    }
}
