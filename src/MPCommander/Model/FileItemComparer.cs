using MPCommander.Native;

namespace MPCommander.Model;

public enum SortColumn { Name, Extension, Size, Date, Attributes }

/// <summary>".." → 폴더 → 파일 순. 이름은 자연 정렬(file2 &lt; file10, StrCmpLogicalW).</summary>
public sealed class FileItemComparer : IComparer<FileItem>
{
    public SortColumn Column { get; set; } = SortColumn.Name;
    public bool Descending { get; set; }

    public int Compare(FileItem? a, FileItem? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        if (a.IsParent) return -1;
        if (b.IsParent) return 1;
        if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;

        int r = Column switch
        {
            SortColumn.Extension => a.IsDirectory ? 0 : Natural(a.Extension, b.Extension),
            SortColumn.Size => a.IsDirectory ? 0 : a.Size.CompareTo(b.Size),
            SortColumn.Date => a.LastWriteRaw.CompareTo(b.LastWriteRaw),
            SortColumn.Attributes => ((int)a.Attributes).CompareTo((int)b.Attributes),
            _ => 0,
        };
        if (r == 0) r = Natural(a.Name, b.Name);
        return Descending ? -r : r;
    }

    private static int Natural(string a, string b) => NativeMethods.FmCompareNatural(a, b);
}
