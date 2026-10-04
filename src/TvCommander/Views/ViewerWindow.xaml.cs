using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using TvCommander.IO;
using TvCommander.Model;

namespace TvCommander.Views;

/// <summary>F3 내장 뷰어. 텍스트(인코딩 자동 판별) / 16진수 / 이미지. 큰 파일은 앞부분만 읽는다.</summary>
public partial class ViewerWindow : Window
{
    private const int TextLimit = 8 * 1024 * 1024;
    private const int HexLimit = 512 * 1024;
    private const int ImageLimit = 64 * 1024 * 1024;
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".webp" };

    private readonly string _path;
    private byte[] _data = [];
    private long _fileLength;
    private bool _hex, _isImage;
    private string _encodingName = "";

    public ViewerWindow(string path)
    {
        InitializeComponent();
        _path = path;
        Title = $"보기 - {Path.GetFileName(path)}";
        _isImage = ImageExtensions.Contains(Path.GetExtension(path));
        Loaded += async (_, _) => await LoadAsync();
        PreviewKeyDown += OnKey;
    }

    private async Task LoadAsync()
    {
        try
        {
            int limit = _isImage ? ImageLimit : TextLimit;
            (_data, _fileLength) = await GuardedIo.RunAsync(ctx => ReadHead(_path, limit, ctx), 5000, _path);
        }
        catch (Exception ex)
        {
            StateText.Text = ex.Message;
            return;
        }

        if (_isImage && TryShowImage()) return;
        _hex = LooksBinary(_data);
        ShowText();
    }

    private static (byte[] Data, long Length) ReadHead(string path, int limit, IoContext ctx)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        long length = fs.Length;
        var buf = new byte[(int)Math.Min(length, limit)];
        int read = 0;
        while (read < buf.Length)
        {
            int n = fs.Read(buf, read, Math.Min(256 * 1024, buf.Length - read));
            if (n == 0) break;
            read += n;
            ctx.Report();
            if (ctx.IsCancelled) break;
        }
        if (read < buf.Length) Array.Resize(ref buf, read);
        return (buf, length);
    }

    private bool TryShowImage()
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(_data);
            bmp.EndInit();
            bmp.Freeze();
            ImageView.Source = bmp;
            ImageScroll.Visibility = Visibility.Visible;
            StateText.Visibility = Visibility.Collapsed;
            InfoText.Text = $"{_path}   │   {bmp.PixelWidth} × {bmp.PixelHeight}   │   {PathUtil.FormatSize(_fileLength)}   │   Esc 닫기";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ShowText()
    {
        string text;
        if (_hex)
        {
            text = HexDump(_data, Math.Min(_data.Length, HexLimit));
            _encodingName = "HEX";
        }
        else
        {
            var enc = DetectEncoding(_data, out int bomLength);
            _encodingName = enc.WebName.ToUpperInvariant();
            text = enc.GetString(_data, bomLength, _data.Length - bomLength);
        }

        long shown = _hex ? Math.Min(_data.Length, HexLimit) : _data.Length;
        if (shown < _fileLength) text += $"\n\n───── 파일이 커서 앞부분 {PathUtil.FormatSize(shown)}만 표시합니다 ─────";

        TextView.Text = text;
        TextView.Visibility = Visibility.Visible;
        StateText.Visibility = Visibility.Collapsed;
        TextView.Focus();
        TextView.CaretIndex = 0;
        TextView.ScrollToHome();
        InfoText.Text = $"{_path}   │   {PathUtil.FormatSize(_fileLength)}   │   {_encodingName}   │   H: 텍스트/HEX   W: 줄 바꿈   Esc: 닫기";
    }

    private static Encoding DetectEncoding(byte[] d, out int bomLength)
    {
        bomLength = 0;
        if (d.Length >= 3 && d[0] == 0xEF && d[1] == 0xBB && d[2] == 0xBF) { bomLength = 3; return Encoding.UTF8; }
        if (d.Length >= 2 && d[0] == 0xFF && d[1] == 0xFE) { bomLength = 2; return Encoding.Unicode; }
        if (d.Length >= 2 && d[0] == 0xFE && d[1] == 0xFF) { bomLength = 2; return Encoding.BigEndianUnicode; }

        try
        {
            // 잘린 끝부분의 불완전한 문자는 무시하고 검사
            int len = d.Length;
            int back = 0;
            while (back < 3 && len - back - 1 >= 0 && (d[len - back - 1] & 0xC0) == 0x80) back++;
            if (back < 3 && len - back - 1 >= 0 && d[len - back - 1] >= 0xC0) len = len - back - 1;
            new UTF8Encoding(false, true).GetCharCount(d, 0, len);
            return Encoding.UTF8;
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(949);   // 한글 ANSI
        }
    }

    private static bool LooksBinary(byte[] d)
    {
        if (d.Length >= 2 && ((d[0] == 0xFF && d[1] == 0xFE) || (d[0] == 0xFE && d[1] == 0xFF))) return false;
        int n = Math.Min(d.Length, 8192), zeros = 0;
        for (int i = 0; i < n; i++) if (d[i] == 0) zeros++;
        return zeros > 0 && zeros * 100 / Math.Max(1, n) >= 1;
    }

    private static string HexDump(byte[] d, int length)
    {
        var sb = new StringBuilder(length * 4 + length / 16 * 14);
        for (int off = 0; off < length; off += 16)
        {
            sb.Append(off.ToString("X8")).Append("  ");
            for (int i = 0; i < 16; i++)
            {
                if (off + i < length) sb.Append(d[off + i].ToString("X2")).Append(' ');
                else sb.Append("   ");
                if (i == 7) sb.Append(' ');
            }
            sb.Append(" │ ");
            for (int i = 0; i < 16 && off + i < length; i++)
            {
                byte b = d[off + i];
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Escape or Key.F3 or Key.F10:
                Close();
                e.Handled = true;
                break;
            case Key.H when !_isImage || TextView.Visibility == Visibility.Visible:
                if (_data.Length == 0) break;
                _hex = !_hex;
                ShowText();
                e.Handled = true;
                break;
            case Key.W when TextView.Visibility == Visibility.Visible:
                TextView.TextWrapping = TextView.TextWrapping == TextWrapping.Wrap ? TextWrapping.NoWrap : TextWrapping.Wrap;
                e.Handled = true;
                break;
        }
    }
}
