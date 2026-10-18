using System.ComponentModel;
using System.Text;

namespace MPCommander.IO;

/// <summary>CSV 파일의 인코딩 (읽은 그대로 저장한다)</summary>
public enum CsvEncoding { Utf8, Utf8Bom, Cp949 }

/// <summary>CSV 한 줄. 칸마다 글자와 '따옴표로 감싸 있었는지' 를 가진다 (저장할 때 원래 모양을 지키려고)</summary>
public sealed class CsvRow : INotifyPropertyChanged
{
    private readonly List<string> _cells;
    private readonly List<bool> _quoted;

    public CsvRow(List<string> cells, List<bool>? quoted = null)
    {
        _cells = cells;
        _quoted = quoted ?? cells.Select(_ => false).ToList();
    }

    public int Count => _cells.Count;

    /// <summary>DataGrid 가 Binding "[i]" 로 읽고 쓴다. 없는 칸은 빈 글자, 쓰면 늘어난다</summary>
    public string this[int index]
    {
        get => index < _cells.Count ? _cells[index] : "";
        set
        {
            value ??= "";
            if (index < _cells.Count && _cells[index] == value) return;
            while (_cells.Count <= index) { _cells.Add(""); _quoted.Add(false); }
            _cells[index] = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            Changed?.Invoke(this);
        }
    }

    public bool WasQuoted(int index) => index < _quoted.Count && _quoted[index];

    public void InsertCell(int index, string value = "")
    {
        if (index > _cells.Count) return;   // 짧은 줄: 그 칸이 없으면 그대로
        _cells.Insert(index, value);
        _quoted.Insert(index, false);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    public void RemoveCell(int index)
    {
        if (index >= _cells.Count) return;
        _cells.RemoveAt(index);
        _quoted.RemoveAt(index);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    public IEnumerable<string> Cells => _cells;

    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>편집기가 '고침' 표시를 하려고 듣는다</summary>
    public event Action<CsvRow>? Changed;
}

/// <summary>
/// CSV 읽기·쓰기 (RFC 4180): 따옴표로 감싼 칸, 칸 안의 "" 와 줄바꿈, 구분자 , ; 탭.
/// 인코딩은 UTF-8 / UTF-8(BOM) / CP949 를 알아내고 그대로 저장한다. 줄바꿈(CRLF/LF)과 끝 줄바꿈 여부도 지킨다.
/// </summary>
public sealed class CsvDocument
{
    public List<CsvRow> Rows { get; } = new();
    public char Delimiter { get; set; } = ',';
    public CsvEncoding Encoding { get; set; } = CsvEncoding.Utf8;
    public string NewLine { get; set; } = "\r\n";
    public bool EndsWithNewLine { get; set; } = true;

    public int ColumnCount => Rows.Count == 0 ? 0 : Rows.Max(r => r.Count);

    public static System.Text.Encoding GetEncoding(CsvEncoding e) => e switch
    {
        CsvEncoding.Utf8Bom => new UTF8Encoding(true),
        CsvEncoding.Cp949 => System.Text.Encoding.GetEncoding(949),
        _ => new UTF8Encoding(false),
    };

    // ───────────────────────── 읽기 ─────────────────────────

    public static CsvDocument Load(string path, CancellationToken ct = default)
    {
        var bytes = File.ReadAllBytes(path);
        var doc = new CsvDocument();
        string text;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            doc.Encoding = CsvEncoding.Utf8Bom;
            text = new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
        }
        else
        {
            try
            {
                text = new UTF8Encoding(false, true).GetString(bytes);   // 올바른 UTF-8 이 아니면 예외
                doc.Encoding = CsvEncoding.Utf8;
            }
            catch (DecoderFallbackException)
            {
                text = System.Text.Encoding.GetEncoding(949).GetString(bytes);
                doc.Encoding = CsvEncoding.Cp949;
            }
        }
        doc.Delimiter = string.Equals(Path.GetExtension(path), ".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : DetectDelimiter(text);
        doc.Parse(text, ct);
        return doc;
    }

    /// <summary>첫 줄(따옴표 밖)에서 , ; 탭 중 가장 많은 것</summary>
    public static char DetectDelimiter(string text)
    {
        int comma = 0, semi = 0, tab = 0;
        bool q = false;
        foreach (var c in text)
        {
            if (c == '"') q = !q;
            else if (!q && (c == '\n' || c == '\r')) break;
            else if (!q && c == ',') comma++;
            else if (!q && c == ';') semi++;
            else if (!q && c == '\t') tab++;
        }
        if (tab > comma && tab >= semi) return '\t';
        if (semi > comma) return ';';
        return ',';
    }

    public void Parse(string text, CancellationToken ct = default)
    {
        Rows.Clear();
        int crlf = 0, lf = 0;
        var cells = new List<string>();
        var quoted = new List<bool>();
        var sb = new StringBuilder();
        bool inQuotes = false, wasQuoted = false, rowHasData = false;
        int n = text.Length;

        void EndCell()
        {
            cells.Add(sb.ToString());
            quoted.Add(wasQuoted);
            sb.Clear();
            wasQuoted = false;
        }
        void EndRow()
        {
            EndCell();
            Rows.Add(new CsvRow(cells, quoted));
            cells = new List<string>();
            quoted = new List<bool>();
            rowHasData = false;
        }

        for (int i = 0; i < n; i++)
        {
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < n && text[i + 1] == '"') { sb.Append('"'); i++; }   // "" → "
                    else inQuotes = false;
                }
                else sb.Append(c);
                continue;
            }
            if (c == '"' && sb.Length == 0 && !wasQuoted) { inQuotes = true; wasQuoted = true; rowHasData = true; }
            else if (c == Delimiter) { EndCell(); rowHasData = true; }
            else if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < n && text[i + 1] == '\n') { i++; crlf++; }
                else lf++;
                EndRow();
            }
            else { sb.Append(c); rowHasData = true; }   // 따옴표 뒤에 붙은 글자(잘못된 CSV)도 버리지 않는다
        }

        EndsWithNewLine = n > 0 && (text[n - 1] == '\n' || text[n - 1] == '\r');
        if (rowHasData || sb.Length > 0 || cells.Count > 0 || inQuotes) EndRow();
        NewLine = lf > crlf ? "\n" : "\r\n";
    }

    // ───────────────────────── 쓰기 ─────────────────────────

    /// <summary>줄마다 그 줄의 칸 수만큼 쓴다 (짧은 줄에 빈 칸을 덧붙이지 않는다 — 고친 칸·더한 열은 그 줄이 늘어나 있다)</summary>
    public string Serialize()
    {
        var sb = new StringBuilder();
        for (int r = 0; r < Rows.Count; r++)
        {
            var row = Rows[r];
            for (int c = 0; c < row.Count; c++)
            {
                if (c > 0) sb.Append(Delimiter);
                var v = row[c];
                bool needQuote = row.WasQuoted(c) || v.IndexOf(Delimiter) >= 0 || v.IndexOf('"') >= 0
                                 || v.IndexOf('\n') >= 0 || v.IndexOf('\r') >= 0;
                if (needQuote) sb.Append('"').Append(v.Replace("\"", "\"\"")).Append('"');
                else sb.Append(v);
            }
            if (r < Rows.Count - 1 || EndsWithNewLine) sb.Append(NewLine);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 저장: 같은 폴더의 임시 파일에 다 쓴 뒤 바꿔 넣는다 (중간에 실패해도 원래 파일이 깨지지 않는다).
    /// CP949 로 나타낼 수 없는 글자가 있으면 EncoderFallbackException.
    /// </summary>
    public void Save(string path)
    {
        var text = Serialize();
        var enc = Encoding == CsvEncoding.Cp949
            ? System.Text.Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback)
            : GetEncoding(Encoding);
        var bytes = enc.GetPreamble().Concat(enc.GetBytes(text)).ToArray();
        var tmp = path + ".mpc-tmp";
        File.WriteAllBytes(tmp, bytes);
        if (File.Exists(path)) File.Replace(tmp, path, null, true);
        else File.Move(tmp, path);
    }
}
