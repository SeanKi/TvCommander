using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MPCommander.IO;

namespace MPCommander.Views;

/// <summary>
/// 뷰어 인코딩 직접 고르기 (아래 상태 줄 오른쪽). 자동 = BOM / UTF-8 검사 / 아니면 한글 ANSI(949).
///  - 줄 색인은 '\n' 바이트로 나누므로 ASCII 와 겹치는 인코딩(UTF-8, 각 나라 ANSI·DBCS, ISO-8859, KOI8 ...)은 모두 된다.
///    (Shift-JIS · GBK · Big5 · EUC 의 두 번째 바이트도 0x0A 는 아니다.) UTF-16 은 C++ 가 2바이트 단위로.
///  - UTF-32 · EBCDIC 은 줄 바꿈 단위·바이트가 달라서 넣지 않았다.
///  - 고른 인코딩은 이 프로그램을 끌 때까지 그 파일에 기억된다 (다시 열어도 그대로).
/// </summary>
public partial class ViewerWindow
{
    private static readonly Dictionary<string, int> RememberedEncoding = new(StringComparer.OrdinalIgnoreCase);
    private int? _forcedCodePage;
    private ComboBoxItem? _autoItem;

    private static readonly (string Group, (string Name, int CodePage)[] Items)[] EncodingTable =
    {
        ("유니코드", new[] { ("UTF-8", 65001), ("UTF-16 LE", 1200), ("UTF-16 BE", 1201) }),
        ("한국어", new[] { ("EUC-KR / CP949", 949), ("Johab (조합형)", 1361) }),
        ("일본어", new[] { ("Shift-JIS", 932), ("EUC-JP", 51932), ("ISO-2022-JP (JIS)", 50220) }),
        ("중국어 간체", new[] { ("GBK / GB2312", 936), ("GB18030", 54936) }),
        ("중국어 번체", new[] { ("Big5", 950) }),
        ("서유럽", new[] { ("Windows-1252", 1252), ("ISO-8859-1", 28591), ("ISO-8859-15", 28605), ("Mac Roman", 10000) }),
        ("중앙·동유럽", new[] { ("Windows-1250", 1250), ("ISO-8859-2", 28592) }),
        ("키릴 (러시아어 등)", new[] { ("Windows-1251", 1251), ("KOI8-R", 20866), ("KOI8-U", 21866), ("ISO-8859-5", 28595), ("DOS 866", 866) }),
        ("그리스어", new[] { ("Windows-1253", 1253), ("ISO-8859-7", 28597) }),
        ("터키어", new[] { ("Windows-1254", 1254), ("ISO-8859-9", 28599) }),
        ("히브리어", new[] { ("Windows-1255", 1255), ("ISO-8859-8", 28598) }),
        ("아랍어", new[] { ("Windows-1256", 1256), ("ISO-8859-6", 28596), ("DOS 720", 720) }),
        ("발트어", new[] { ("Windows-1257", 1257), ("ISO-8859-13", 28603), ("ISO-8859-4", 28594) }),
        ("베트남어", new[] { ("Windows-1258", 1258) }),
        ("태국어", new[] { ("Windows-874 (TIS-620)", 874) }),
        ("DOS", new[] { ("DOS 437 (미국)", 437), ("DOS 850 (서유럽)", 850) }),
    };

    private Encoding? ForcedEncoding => _forcedCodePage is int cp ? Encoding.GetEncoding(cp) : null;

    private static bool Available(int codePage)
    {
        try { Encoding.GetEncoding(codePage); return true; }
        catch { return false; }
    }

    /// <summary>상태 줄에 보일 짧은 이름</summary>
    private static string EncodingLabel(Encoding enc)
    {
        foreach (var (_, items) in EncodingTable)
            foreach (var (name, cp) in items)
                if (cp == enc.CodePage) return name;
        return enc.WebName.ToUpperInvariant();
    }

    private void InitEncodingBox()
    {
        if (RememberedEncoding.TryGetValue(_path, out var remembered)) _forcedCodePage = remembered;
        _syncing = true;
        _autoItem = new ComboBoxItem { Content = Loc.T("자동"), Tag = 0 };
        EncodingBox.Items.Add(_autoItem);
        var selected = _autoItem;
        foreach (var (group, items) in EncodingTable)
        {
            var available = items.Where(i => Available(i.CodePage)).ToList();
            if (available.Count == 0) continue;
            EncodingBox.Items.Add(new ComboBoxItem
            {
                Content = Loc.T(group), IsEnabled = false, FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.DimGray, Padding = new Thickness(4, 4, 4, 1),
            });
            foreach (var (name, cp) in available)
            {
                var item = new ComboBoxItem { Content = Loc.T(name), Tag = cp, Padding = new Thickness(18, 1, 6, 1) };
                EncodingBox.Items.Add(item);
                if (cp == _forcedCodePage) selected = item;
            }
        }
        EncodingBox.SelectedItem = selected;
        _syncing = false;
    }

    /// <summary>원본을 연 뒤: 상태 줄 이름, 자동이면 "자동 (판별한 것)"</summary>
    private void ShowEncoding(Encoding enc)
    {
        _encodingName = EncodingLabel(enc);
        if (_autoItem == null || _forcedCodePage != null) return;
        _syncing = true;
        _autoItem.Content = Loc.F($"자동 ({_encodingName})");
        EncodingBox.SelectedItem = null;      // 고른 칸의 글자를 새로 그리게
        EncodingBox.SelectedItem = _autoItem;
        _syncing = false;
    }

    private async void EncodingBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || EncodingBox.SelectedItem is not ComboBoxItem { Tag: int cp }) return;
        _forcedCodePage = cp == 0 ? null : cp;
        if (cp == 0) RememberedEncoding.Remove(_path); else RememberedEncoding[_path] = cp;
        if (cp != 0) _autoItem!.Content = Loc.T("자동");

        // 이진으로 보여 HEX 였어도, 인코딩을 고르면 텍스트로 (BOM 없는 UTF-16 등)
        bool wasHex = _hex;
        if (wasHex)
        {
            _hex = false;
            HexBox.Visibility = Visibility.Collapsed;
        }
        if (_source == null) await LoadAsync();
        else
        {
            await ReloadAsync();
            if (wasHex) ShowText();
        }
        TextView.Focus();
    }
}
