using MPCommander.Model;
using MPCommander.Native;

namespace MPCommander.IO;

/// <summary>원격 파일 항목. LastWrite 는 FILETIME(UTC), 모르면 0.</summary>
public sealed record VEntry(string Name, long Size, long LastWrite, bool IsFolder);

/// <summary>
/// 원격 파일 시스템 (휴대폰 MTP, FTP, WebDAV) 공통 인터페이스.
/// 모든 메서드는 동기 호출이다. UI 에서는 GuardedIo 등 작업 스레드에서 부르고,
/// 응답이 없으면 Abandon 으로 진행 중인 요청을 끊는다.
/// </summary>
public interface IVirtualFs
{
    /// <summary>"mtp", "ftp", "dav"</summary>
    string Scheme { get; }
    /// <summary>"휴대폰", "FTP", "WebDAV" (메시지용)</summary>
    string DisplayName { get; }
    /// <summary>이 시간 동안 진행이 없으면 타임아웃</summary>
    int IdleTimeoutMs { get; }

    /// <summary>폴더 목록. ctx 가 있으면 항목마다 진행을 알리고 취소를 확인한다.</summary>
    List<VEntry> List(string path, IoContext? ctx);
    /// <summary>항목 정보. 없으면 null. 루트는 폴더.</summary>
    VEntry? GetEntry(string path);
    /// <summary>progress(받은 바이트, 전체) 가 true 를 돌려주면 취소</summary>
    void Download(string path, string localPath, bool overwrite, Func<long, long, bool>? progress);
    /// <summary>remotePath 는 파일 이름까지 포함. 상위 폴더가 없으면 만든다.</summary>
    void Upload(string localPath, string remotePath, bool overwrite, Func<long, long, bool>? progress);
    void Delete(string path, bool recursive);
    void EnsureFolder(string path);
    void Rename(string path, string newName);
    /// <summary>여유/전체 용량. 모르면 null.</summary>
    (ulong Free, ulong Total)? SpaceInfo(string path);
    /// <summary>응답 없음: 해당 연결의 진행 중 요청을 끊고 다음 접근 때 새로 연결한다.</summary>
    void Abandon(string path);
    /// <summary>모든 진행 중 요청 취소 (복사 취소 버튼)</summary>
    void CancelAll();
}

public static class VirtualFs
{
    private static readonly Dictionary<string, IVirtualFs> Providers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mtp"] = new MtpFs(),
        ["ftp"] = new FtpFs(),
        ["dav"] = new WebDavFs(),
    };

    public static IVirtualFs For(string path)
        => Providers.TryGetValue(PathUtil.Scheme(path), out var fs) ? fs : throw new IOException("지원하지 않는 경로입니다: " + path);

    public static void CancelAll()
    {
        foreach (var fs in Providers.Values)
        {
            try { fs.CancelAll(); } catch { }
        }
    }

    /// <summary>패널 목록용 FileItem 으로 변환 (같은 이름이 둘이면 첫 번째만)</summary>
    public static List<FileItem> ToFileItems(string dir, IEnumerable<VEntry> entries)
    {
        dir = PathUtil.TrimEnd(dir);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<FileItem>();
        foreach (var e in entries)
        {
            if (!seen.Add(e.Name)) continue;
            var attr = e.IsFolder ? FileAttributes.Directory : FileAttributes.Normal;
            items.Add(FileItem.FromNative(dir, e.Name, (ulong)Math.Max(0, e.Size), (ulong)Math.Max(0, e.LastWrite), (uint)attr));
        }
        return items;
    }
}

/// <summary>기존 MTP 구현(Mtp 클래스)을 공통 인터페이스로 감싼 것</summary>
internal sealed class MtpFs : IVirtualFs
{
    public string Scheme => "mtp";
    public string DisplayName => "휴대폰";
    public int IdleTimeoutMs => Mtp.IdleTimeoutMs;

    public unsafe List<VEntry> List(string path, IoContext? ctx)
    {
        var list = ctx == null ? Mtp.ListChildren(path) : Mtp.ListChildren(path, ctx.CancelFlag, ctx.ProgressCounter);
        return list.Select(e => new VEntry(e.Name, e.Size, e.LastWrite, e.IsFolder)).ToList();
    }

    public VEntry? GetEntry(string path)
        => Mtp.GetEntry(path) is { } e ? new VEntry(e.Name, e.Size, e.LastWrite, e.IsFolder) : null;

    private static FmProgressCallback? Callback(Func<long, long, bool>? progress)
        => progress == null ? null : (done, total, _) => progress((long)done, (long)total) ? 1 : 0;

    public unsafe void Download(string path, string localPath, bool overwrite, Func<long, long, bool>? progress)
    {
        var cb = Callback(progress);
        Mtp.Download(path, localPath, overwrite, cb, null);
        GC.KeepAlive(cb);
    }

    public unsafe void Upload(string localPath, string remotePath, bool overwrite, Func<long, long, bool>? progress)
    {
        if (overwrite && Mtp.GetEntry(remotePath) != null) Mtp.Delete(remotePath, recursive: false);   // MTP 는 덮어쓰기가 없다
        var cb = Callback(progress);
        Mtp.Upload(localPath, remotePath, cb, null);
        GC.KeepAlive(cb);
    }

    public void Delete(string path, bool recursive) => Mtp.Delete(path, recursive);
    public void EnsureFolder(string path) => Mtp.EnsureFolder(path);
    public void Rename(string path, string newName) => Mtp.Rename(path, newName);
    public (ulong Free, ulong Total)? SpaceInfo(string path) => Mtp.StorageInfo(path);
    public void Abandon(string path) => Mtp.Abandon(path);
    public void CancelAll() => Mtp.CancelAll();
}
