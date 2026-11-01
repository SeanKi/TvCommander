using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using MPCommander.Model;

namespace MPCommander.IO;

public enum DiskType { Unknown, Ssd, Hdd, Network, Removable }

/// <summary>
/// 경로가 있는 디스크 종류. 작은 파일을 동시에 몇 개 복사할지 정하는 데 쓴다
/// (SSD·네트워크는 동시 복사가 빠르고, HDD 는 헤드가 오가 오히려 느려진다).
/// </summary>
public static class DiskKind
{
    private static readonly ConcurrentDictionary<string, DiskType> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static DiskType Of(string path)
    {
        if (PathUtil.IsVirtual(path)) return DiskType.Unknown;
        if (path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal)) return DiskType.Network;
        var root = Path.GetPathRoot(path);
        if (Compat.IsEmpty(root)) return DiskType.Unknown;
        return Cache.GetOrAdd(root!, Query);
    }

    /// <summary>자동일 때 동시 복사 개수: 둘 중 느린 쪽에 맞춘다</summary>
    public static int AutoThreads(string src, string dst) => Math.Min(Threads(Of(src)), Threads(Of(dst)));

    private static int Threads(DiskType t) => t switch
    {
        DiskType.Ssd or DiskType.Network => 8,
        DiskType.Hdd or DiskType.Removable => 1,
        _ => 4,
    };

    private static DiskType Query(string root)
    {
        try
        {
            switch (new DriveInfo(root).DriveType)
            {
                case DriveType.Network: return DiskType.Network;
                case DriveType.Removable: return DiskType.Removable;
                case DriveType.Fixed: break;
                default: return DiskType.Unknown;
            }
            using var h = CreateFileW(@"\\.\" + root.TrimEnd('\\'), 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) return DiskType.Unknown;
            var query = new StoragePropertyQuery { PropertyId = 7 /* StorageDeviceSeekPenaltyProperty */, QueryType = 0 };
            if (!DeviceIoControl(h, 0x2D1400 /* IOCTL_STORAGE_QUERY_PROPERTY */, ref query, Marshal.SizeOf<StoragePropertyQuery>(),
                    out SeekPenaltyDescriptor d, Marshal.SizeOf<SeekPenaltyDescriptor>(), out _, IntPtr.Zero))
                return DiskType.Unknown;
            return d.IncursSeekPenalty != 0 ? DiskType.Hdd : DiskType.Ssd;
        }
        catch { return DiskType.Unknown; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SeekPenaltyDescriptor
    {
        public uint Version;
        public uint Size;
        public byte IncursSeekPenalty;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref StoragePropertyQuery input, int inSize,
        out SeekPenaltyDescriptor output, int outSize, out int returned, IntPtr overlapped);
}
