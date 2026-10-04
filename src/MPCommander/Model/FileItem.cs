using System.ComponentModel;
using System.Windows.Media;

namespace MPCommander.Model;

public sealed class FileItem : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs MarkedArgs = new(nameof(IsMarked));

    private bool _marked;
    private string? _sizeText, _dateText;

    private FileItem(string directoryPath, string name, long size, long lastWriteRaw, FileAttributes attributes, bool isParent)
    {
        DirectoryPath = directoryPath;
        Name = name;
        Size = size;
        LastWriteRaw = lastWriteRaw;
        Attributes = attributes;
        IsParent = isParent;
        IsDirectory = isParent || attributes.HasFlag(FileAttributes.Directory);

        int dot = name.LastIndexOf('.');
        if (!IsDirectory && dot > 0 && dot < name.Length - 1)
        {
            DisplayName = name[..dot];
            Extension = name[(dot + 1)..];
        }
        else
        {
            DisplayName = name;
            Extension = "";
        }
    }

    public static FileItem FromNative(string dir, string name, ulong size, ulong lastWrite, uint attributes)
        => new(dir, name, (long)size, (long)lastWrite, (FileAttributes)attributes, false);

    public static FileItem CreateParent(string dir)
        => new(dir, "..", 0, 0, FileAttributes.Directory, true);

    public string DirectoryPath { get; }
    public string Name { get; }
    public string DisplayName { get; }
    public string Extension { get; }
    public long Size { get; }
    public long LastWriteRaw { get; }
    public FileAttributes Attributes { get; }
    public bool IsDirectory { get; }
    public bool IsParent { get; }

    public string FullPath => IsParent
        ? PathUtil.Parent(DirectoryPath) ?? DirectoryPath
        : PathUtil.Combine(DirectoryPath, Name);

    public DateTime LastWrite => LastWriteRaw <= 0 ? DateTime.MinValue : DateTime.FromFileTimeUtc(LastWriteRaw).ToLocalTime();

    public string SizeText => _sizeText ??= IsParent ? "" : IsDirectory ? "<DIR>" : Size.ToString("N0");
    public string DateText => _dateText ??= IsParent || LastWriteRaw <= 0 ? "" : LastWrite.ToString("yyyy-MM-dd HH:mm");

    public string AttrText
    {
        get
        {
            if (IsParent) return "";
            var a = Attributes;
            return string.Concat(
                a.HasFlag(FileAttributes.ReadOnly) ? "r" : "-",
                a.HasFlag(FileAttributes.Archive) ? "a" : "-",
                a.HasFlag(FileAttributes.Hidden) ? "h" : "-",
                a.HasFlag(FileAttributes.System) ? "s" : "-");
        }
    }

    public ImageSource? Icon => IconCache.Get(this);

    public bool IsHidden => Attributes.HasFlag(FileAttributes.Hidden);

    public bool IsMarked
    {
        get => _marked;
        set
        {
            if (_marked == value) return;
            _marked = value;
            PropertyChanged?.Invoke(this, MarkedArgs);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => Name;
}
