using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Search;
using MPCommander.IO;
using MPCommander.Model;

namespace MPCommander.Views;

/// <summary>
/// 가벼운 텍스트 편집기 (F4). AvalonEdit(MIT) — 줄 번호, 확장자별 문법 색, 찾기(Ctrl+F)·바꾸기(Ctrl+H)·줄 이동(Ctrl+G), 자동 줄 바꿈.
/// 읽은 인코딩(UTF-8 / BOM / CP949 / UTF-16)과 줄바꿈을 그대로 지켜 저장한다. 저장은 임시 파일에 쓴 뒤 바꿔 넣는다.
/// </summary>
public partial class TextEditorWindow : Window
{
    private readonly string _path;
    private readonly Action<string>? _openExternal;
    private TextEncodingKind _encoding;
    private bool _encodingChanged;
    private bool _ready;
    private SearchPanel? _search;

    public TextEditorWindow(string path, Action<string>? openExternal)
    {
        InitializeComponent();
        LocUi.Apply(this);   // XAML 글자를 지금 언어로
        _path = path;
        _openExternal = openExternal;
        Title = $"{Path.GetFileName(path)} — {Loc.T("편집")}";
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.Options.ConvertTabsToSpaces = false;
        Editor.Options.HighlightCurrentLine = true;
        Editor.TextArea.Caret.PositionChanged += (_, _) => UpdateStatus();
        Editor.TextChanged += (_, _) => UpdateStatus();
        Loaded += async (_, _) => await LoadAsync();
        Closing += OnClosing;
        PreviewKeyDown += OnWindowKeyDown;
    }

    private async Task LoadAsync()
    {
        try
        {
            var (text, kind) = await Task.Run(() => TextFileCodec.Decode(File.ReadAllBytes(_path)));
            _encoding = kind;
            Editor.Document.Text = text;
            Editor.Document.UndoStack.ClearAll();
            Editor.Document.UndoStack.MarkAsOriginalFile();
            Editor.TextArea.Caret.Offset = 0;   // 처음부터
            Editor.ScrollToHome();
            Editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(_path));
            _search = SearchPanel.Install(Editor);   // Ctrl+F 찾기 (정규식·대소문자·단어)
            EncodingCombo.SelectedIndex = (int)kind;
            if (new FileInfo(_path).IsReadOnly) Editor.IsReadOnly = true;
            _ready = true;
            UpdateStatus();
            Editor.TextArea.Focus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Loc.T("열 수 없습니다: ") + ex.Message, Loc.T("편집"), MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
        }
    }

    private bool Dirty => _ready && (!Editor.Document.UndoStack.IsOriginalFile || _encodingChanged);

    private string NewLineName()
    {
        var doc = Editor.Document;
        if (doc.LineCount < 2) return "";
        var d = doc.GetLineByNumber(1).DelimiterLength;
        var t = doc.GetText(doc.GetLineByNumber(1).EndOffset, d);
        return t == "\r\n" ? "CRLF" : t == "\n" ? "LF" : t == "\r" ? "CR" : "";
    }

    private void UpdateStatus()
    {
        if (!_ready) return;
        var c = Editor.TextArea.Caret;
        var nl = NewLineName();
        StatusText.Text = Loc.F($"줄 {c.Line:N0}, 칸 {c.Column:N0}   │   {Editor.Document.LineCount:N0}줄   │   {TextFileCodec.Name(_encoding)}") +
                          (nl.Length > 0 ? $"   │   {nl}" : "") +
                          (Editor.SyntaxHighlighting != null ? $"   │   {Editor.SyntaxHighlighting.Name}" : "") +
                          (Editor.IsReadOnly ? Loc.T("   │   읽기 전용") : "") +
                          (Dirty ? Loc.T("   │   고침 (Ctrl+S 저장)") : "");
        Title = (Dirty ? "* " : "") + $"{Path.GetFileName(_path)} — {Loc.T("편집")}";
    }

    // ───────────────────────── 키 ─────────────────────────

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (e.Key == Key.S && mods == ModifierKeys.Control) { e.Handled = true; _ = SaveAsync(); }
        else if (e.Key == Key.H && mods == ModifierKeys.Control) { e.Handled = true; ShowReplace(); }
        else if (e.Key == Key.G && mods == ModifierKeys.Control) { e.Handled = true; ShowGoTo(); }
        else if (e.Key == Key.Escape && (ReplaceBar.Visibility == Visibility.Visible || GoToBar.Visibility == Visibility.Visible))
        {
            e.Handled = true;
            CloseBars();
        }
        else if (e.Key == Key.F3 && _search != null)
        {
            e.Handled = true;
            if (mods == ModifierKeys.Shift) _search.FindPrevious(); else _search.FindNext();
        }
    }

    private void Find_Click(object sender, RoutedEventArgs e) => _search?.Open();
    private void Replace_Click(object sender, RoutedEventArgs e) => ShowReplace();
    private void GoTo_Click(object sender, RoutedEventArgs e) => ShowGoTo();
    private void CloseBars_Click(object sender, RoutedEventArgs e) => CloseBars();

    private void CloseBars()
    {
        ReplaceBar.Visibility = Visibility.Collapsed;
        GoToBar.Visibility = Visibility.Collapsed;
        Editor.TextArea.Focus();
    }

    private void ShowReplace()
    {
        GoToBar.Visibility = Visibility.Collapsed;
        ReplaceBar.Visibility = Visibility.Visible;
        if (Editor.SelectionLength > 0 && !Editor.SelectedText.Contains('\n')) FindBox.Text = Editor.SelectedText;
        FindBox.Focus();
        FindBox.SelectAll();
    }

    private void ShowGoTo()
    {
        ReplaceBar.Visibility = Visibility.Collapsed;
        GoToBar.Visibility = Visibility.Visible;
        GoToLabel.Text = Loc.F($"줄 번호 (1 – {Editor.Document.LineCount:N0})");
        GoToBox.Text = Editor.TextArea.Caret.Line.ToString();
        GoToBox.Focus();
        GoToBox.SelectAll();
    }

    private void GoToBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (!int.TryParse(GoToBox.Text.Trim().Replace(",", ""), out var line)) return;
        line = Compat.Clamp(line, 1, Editor.Document.LineCount);
        Editor.TextArea.Caret.Line = line;
        Editor.TextArea.Caret.Column = 1;
        Editor.ScrollToLine(line);
        CloseBars();
    }

    private StringComparison ReplaceComparison => ReplaceCase.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>커서 다음의 찾을 말을 골라 두고, 이미 골라져 있으면 바꾼 뒤 다음 것을 고른다</summary>
    private void ReplaceNext_Click(object sender, RoutedEventArgs e)
    {
        var find = FindBox.Text;
        if (find.Length == 0 || Editor.IsReadOnly) return;
        if (string.Equals(Editor.SelectedText, find, ReplaceComparison))
            Editor.Document.Replace(Editor.SelectionStart, Editor.SelectionLength, ReplaceBox.Text);
        var text = Editor.Document.Text;
        int from = Editor.SelectionStart + Editor.SelectionLength;
        int at = text.IndexOf(find, Math.Min(from, text.Length), ReplaceComparison);
        if (at < 0) at = text.IndexOf(find, 0, ReplaceComparison);   // 끝까지 갔으면 처음부터
        if (at < 0) { StatusText.Text = Loc.T("찾는 말이 없습니다."); return; }
        Editor.Select(at, find.Length);
        Editor.ScrollTo(Editor.Document.GetLineByOffset(at).LineNumber, 1);
    }

    /// <summary>모두 바꾸기 — 한 번에 되돌릴 수 있게 (Ctrl+Z 한 번)</summary>
    private void ReplaceAll_Click(object sender, RoutedEventArgs e)
    {
        var find = FindBox.Text;
        if (find.Length == 0 || Editor.IsReadOnly) return;
        var text = Editor.Document.Text;
        var sb = new StringBuilder(text.Length);
        int count = 0, pos = 0;
        for (int at = text.IndexOf(find, 0, ReplaceComparison); at >= 0; at = text.IndexOf(find, pos, ReplaceComparison))
        {
            sb.Append(text, pos, at - pos).Append(ReplaceBox.Text);
            pos = at + find.Length;
            count++;
            if (pos >= text.Length) break;
        }
        if (count == 0) { StatusText.Text = Loc.T("찾는 말이 없습니다."); return; }
        sb.Append(text, pos, text.Length - pos);
        Editor.Document.BeginUpdate();
        Editor.Document.Replace(0, Editor.Document.TextLength, sb.ToString());
        Editor.Document.EndUpdate();
        StatusText.Text = Loc.F($"{count:N0}곳을 바꿨습니다.");
    }

    private void Wrap_Click(object sender, RoutedEventArgs e) => Editor.WordWrap = WrapToggle.IsChecked == true;

    private void Encoding_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        var k = (TextEncodingKind)EncodingCombo.SelectedIndex;
        if (k == _encoding) return;
        _encoding = k;
        _encodingChanged = true;
        UpdateStatus();
    }

    private void External_Click(object sender, RoutedEventArgs e)
    {
        if (Dirty && MessageBox.Show(this, Loc.T("고친 내용이 저장되지 않았습니다. 외부 편집기는 저장된 파일을 엽니다. 계속할까요?"), Loc.T("편집"),
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _openExternal?.Invoke(_path);
    }

    // ───────────────────────── 저장 ─────────────────────────

    private void Save_Click(object sender, RoutedEventArgs e) => _ = SaveAsync();

    private async Task<bool> SaveAsync()
    {
        if (!_ready) return false;
        if (Editor.IsReadOnly)
        {
            MessageBox.Show(this, Loc.T("읽기 전용 파일이라 저장할 수 없습니다."), Loc.T("편집"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        var text = Editor.Document.Text;
        var kind = _encoding;
        SaveButton.IsEnabled = false;
        try
        {
            await Task.Run(() => TextFileCodec.SaveAtomic(_path, TextFileCodec.Encode(text, kind)));
            Editor.Document.UndoStack.MarkAsOriginalFile();
            _encodingChanged = false;
            UpdateStatus();
            return true;
        }
        catch (EncoderFallbackException)
        {
            MessageBox.Show(this, Loc.T("CP949 로 나타낼 수 없는 글자가 있어 저장하지 않았습니다. 인코딩을 UTF-8 로 바꿔 저장하세요."),
                Loc.T("편집"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Loc.T("저장하지 못했습니다: ") + ex.Message, Loc.T("편집"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally { SaveButton.IsEnabled = true; }
    }

    private bool _closingAfterSave;

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingAfterSave || !Dirty) return;
        var r = MessageBox.Show(this, Loc.F($"{Path.GetFileName(_path)} 의 고친 내용을 저장할까요?"), Loc.T("편집"),
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
