using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TvCommander.Native;

namespace TvCommander.Model;

/// <summary>확장자별 셸 아이콘 캐시 (UI 스레드 전용). 디스크에 접근하지 않는다.</summary>
public static class IconCache
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(FileItem item)
    {
        string key = item.IsDirectory ? "\\dir" : "." + item.Extension;
        if (Cache.TryGetValue(key, out var img)) return img;

        string name = item.IsDirectory ? "folder" : item.Extension.Length > 0 ? "file." + item.Extension : "file";
        img = Load(name, item.IsDirectory);
        Cache[key] = img;
        return img;
    }

    private static ImageSource? Load(string name, bool isDirectory)
    {
        IntPtr h = NativeMethods.FmGetShellIcon(name, isDirectory ? 1 : 0, 0);
        if (h == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(h, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch
        {
            return null;
        }
        finally
        {
            NativeMethods.DestroyIcon(h);
        }
    }
}
