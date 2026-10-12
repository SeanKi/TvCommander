using System.ComponentModel;
using System.Windows.Media;

namespace MPCommander.Model;

public sealed class FileItem : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs MarkedArgs = new(nameof(IsMarked));
    private static readonly PropertyChangedEventArgs BlockedArgs = new(nameof(IsBlocked));
    private bool _blocked;

    /// <summary>인터넷에서 받은 파일 표시(차단)가 있음. 목록을 읽은 뒤 백그라운드에서 채워진다.</summary>
    public bool IsBlocked
    {
        get => _blocked;
        set
        {
            if (_blocked == value) return;
            _blocked = value;
            PropertyChanged?.Invoke(this, BlockedArgs);
        }
    }

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

    public string SizeText => _sizeText ??= IsParent ? "" : !IsDirectory ? Size.ToString("N0")
        // 폴더 크기는 칸에 들어가도록 단위로 (정확한 바이트 수는 계산이 끝나면 상태 줄에)
        : _dirSize < 0 ? "<DIR>" : _dirSizeBusy ? PathUtil.FormatSize(_dirSize) + " …" : PathUtil.FormatSize(_dirSize);

    private static readonly PropertyChangedEventArgs SizeTextArgs = new(nameof(SizeText));
    private long _dirSize = -1;
    private bool _dirSizeBusy;

    /// <summary>계산한 폴더 크기 (하위 포함, Space 로 계산). 모르면 -1</summary>
    public long DirSize => _dirSize;

    /// <summary>폴더 크기 표시 (busy = 계산 중이라 중간 합계)</summary>
    public void SetDirSize(long size, bool busy)
    {
        if (_dirSize == size && _dirSizeBusy == busy) return;
        _dirSize = size;
        _dirSizeBusy = busy;
        _sizeText = null;
        PropertyChanged?.Invoke(this, SizeTextArgs);
    }

    /// <summary>합계에 쓸 크기: 파일은 크기, 폴더는 계산했으면 그 크기</summary>
    public long EffectiveSize => IsParent ? 0 : IsDirectory ? Math.Max(0, _dirSize) : Size;
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
