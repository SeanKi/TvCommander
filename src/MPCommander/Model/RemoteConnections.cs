using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MPCommander.Model;

public enum RemoteKind { Ftp, Ftps, WebDav }

/// <summary>등록된 FTP / WebDAV 연결</summary>
public sealed class RemoteConnection
{
    /// <summary>경로에 쓰이는 이름: ftp://이름/..., dav://이름/...</summary>
    public string Name { get; set; } = "";
    public RemoteKind Kind { get; set; } = RemoteKind.Ftp;
    /// <summary>FTP: 호스트 이름 또는 IP. WebDAV: 전체 URL (https://서버/경로)</summary>
    public string Address { get; set; } = "";
    /// <summary>FTP 포트 (0 = 기본 21)</summary>
    public int Port { get; set; }
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>FTP 패시브 모드 (방화벽·공유기 뒤에서는 거의 항상 필요)</summary>
    public bool Passive { get; set; } = true;
    /// <summary>서버 인증서 오류 무시 (자체 서명 인증서를 쓰는 NAS 등)</summary>
    public bool IgnoreCertErrors { get; set; }
    /// <summary>FTP 시작 폴더 (로그인 폴더 기준, 비우면 로그인 폴더)</summary>
    public string StartPath { get; set; } = "";

    public string Scheme => Kind == RemoteKind.WebDav ? "dav" : "ftp";
    public string RootPath => $"{Scheme}://{Name}";
    public int EffectivePort => Port > 0 ? Port : 21;

    public string KindText => Kind switch
    {
        RemoteKind.Ftps => "FTPS",
        RemoteKind.WebDav => "WebDAV",
        _ => "FTP",
    };

    public string AddressText => Kind == RemoteKind.WebDav ? Address : $"{Address}:{EffectivePort}{(StartPath.Length > 0 ? "/" + StartPath.Trim('/') : "")}";

    /// <summary>TCP 사전 체크용 호스트·포트</summary>
    public (string Host, int Port) Endpoint
    {
        get
        {
            if (Kind != RemoteKind.WebDav) return (Address, EffectivePort);
            var uri = new Uri(Address);
            return (uri.Host, uri.Port);
        }
    }

    public RemoteConnection Clone() => (RemoteConnection)MemberwiseClone();
}

/// <summary>
/// 연결 목록. %APPDATA%\MP-Commander\connections.ini 에 저장하고,
/// 비밀번호는 Windows DPAPI(현재 사용자)로 암호화한다 → 다른 계정·다른 PC 에서는 풀 수 없다.
/// </summary>
public static class RemoteConnections
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MP-Commander/connections");
    private static readonly object Sync = new();
    private static List<RemoteConnection>? _list;

    private static string FilePath => Path.Combine(AppPaths.DataFolder, "connections.ini");

    public static event Action? Changed;

    public static IReadOnlyList<RemoteConnection> All
    {
        get
        {
            lock (Sync) return (_list ??= Load()).ToList();
        }
    }

    public static RemoteConnection? Find(string scheme, string name)
    {
        lock (Sync)
        {
            var temp = Temporaries.FirstOrDefault(c =>
                c.Scheme.Equals(scheme, StringComparison.OrdinalIgnoreCase) && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (temp != null) return temp;
            return (_list ??= Load()).FirstOrDefault(c =>
                c.Scheme.Equals(scheme, StringComparison.OrdinalIgnoreCase) && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static readonly List<RemoteConnection> Temporaries = new();

    /// <summary>저장하지 않은 연결을 잠시 등록 ('연결 시험'용). Dispose 하면 사라진다.</summary>
    public static IDisposable Temporary(RemoteConnection conn)
    {
        lock (Sync) Temporaries.Add(conn);
        return new Releaser(() => { lock (Sync) Temporaries.Remove(conn); });
    }

    private sealed class Releaser(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    /// <summary>원격 경로("ftp://이름/...")의 연결</summary>
    public static RemoteConnection ForPath(string path)
    {
        var name = PathUtil.RootName(path);
        return Find(PathUtil.Scheme(path), name)
               ?? throw new IOException($"'{name}' 연결이 등록되어 있지 않습니다. 연결 관리(Ctrl+F)에서 확인하세요.");
    }

    /// <summary>추가 또는 수정 (oldName 이 있으면 그 연결을 바꾼다)</summary>
    public static void Save(RemoteConnection conn, string? oldName)
    {
        lock (Sync)
        {
            var list = _list ??= Load();
            if (oldName != null) list.RemoveAll(c => c.Name.Equals(oldName, StringComparison.OrdinalIgnoreCase));
            list.Add(conn);
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            Write(list);
        }
        Changed?.Invoke();
    }

    public static void Delete(string name)
    {
        lock (Sync)
        {
            var list = _list ??= Load();
            list.RemoveAll(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            Write(list);
        }
        Changed?.Invoke();
    }

    /// <summary>이름 규칙: 비어 있지 않고, 경로 구분자·콜론이 없고, 다른 연결과 겹치지 않아야 한다.</summary>
    public static string? ValidateName(string name, string? oldName)
    {
        if (string.IsNullOrWhiteSpace(name)) return "이름을 입력하세요.";
        if (name.IndexOfAny(['/', '\\', ':', '|', '?', '*', '"', '<', '>']) >= 0) return "이름에 / \\ : | ? * \" < > 는 쓸 수 없습니다.";
        bool dup = All.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                                !c.Name.Equals(oldName ?? "", StringComparison.OrdinalIgnoreCase));
        return dup ? "같은 이름의 연결이 이미 있습니다." : null;
    }

    private static List<RemoteConnection> Load()
    {
        var list = new List<RemoteConnection>();
        try
        {
            if (!File.Exists(FilePath)) return list;
            var ini = IniFile.Load(FilePath);
            for (int i = 1; ; i++)
            {
                var sec = $"Connection{i}";
                var name = ini.Get(sec, "Name");
                if (name == null) break;
                list.Add(new RemoteConnection
                {
                    Name = name,
                    Kind = Enum.TryParse<RemoteKind>(ini.Get(sec, "Kind"), out var k) ? k : RemoteKind.Ftp,
                    Address = ini.Get(sec, "Address") ?? "",
                    Port = int.TryParse(ini.Get(sec, "Port"), out var port) ? port : 0,
                    User = ini.Get(sec, "User") ?? "",
                    Password = Unprotect(ini.Get(sec, "Password")),
                    Passive = !bool.TryParse(ini.Get(sec, "Passive"), out var pas) || pas,
                    IgnoreCertErrors = bool.TryParse(ini.Get(sec, "IgnoreCertErrors"), out var ign) && ign,
                    StartPath = ini.Get(sec, "StartPath") ?? "",
                });
            }
        }
        catch
        {
            /* 손상된 파일은 무시 */
        }
        return list;
    }

    private static void Write(List<RemoteConnection> list)
    {
        var ini = new IniFile();
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            var s = ini.Section($"Connection{i + 1}");
            s.Add(new("Name", c.Name));
            s.Add(new("Kind", c.Kind.ToString()));
            s.Add(new("Address", c.Address));
            s.Add(new("Port", c.Port.ToString(CultureInfo.InvariantCulture)));
            s.Add(new("User", c.User));
            s.Add(new("Password", Protect(c.Password)));
            s.Add(new("Passive", c.Passive.ToString()));
            s.Add(new("IgnoreCertErrors", c.IgnoreCertErrors.ToString()));
            s.Add(new("StartPath", c.StartPath));
        }
        ini.Save(FilePath, "MP-Commander 원격 연결 (비밀번호는 Windows 사용자 계정으로 암호화됨)");
    }

    private static string Protect(string plain)
    {
        if (plain.Length == 0) return "";
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    private static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch
        {
            return "";   // 다른 계정·PC 에서 만든 값
        }
    }
}
