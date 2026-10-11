using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace MPCommander.IO;

/// <summary>파일을 쓰고 있는(잠근) 프로그램 한 개</summary>
public sealed class LockingProcess
{
    public int Pid { get; init; }
    public DateTime StartTime { get; init; }
    public string Name { get; init; } = "";
    public string? ExePath { get; init; }
    public string? CommandLine { get; set; }
    public bool IsService { get; init; }
    public bool IsSelf { get; init; }

    public string Display => IsSelf ? $"{Name} (이 프로그램)" : IsService ? $"{Name} (서비스)" : Name;

    /// <summary>같은 프로세스인지 (PID 는 재사용되므로 시작 시각까지 비교)</summary>
    public bool IsAlive
    {
        get
        {
            try
            {
                using var p = Process.GetProcessById(Pid);
                return !p.HasExited && Math.Abs((p.StartTime - StartTime).TotalSeconds) < 2;
            }
            catch { return false; }
        }
    }

    /// <summary>다시 실행할 때 쓸 인자 (명령줄에서 실행 파일 부분을 뗀 것)</summary>
    public string Arguments
    {
        get
        {
            var cl = CommandLine?.Trim() ?? "";
            if (cl.Length == 0) return "";
            int end = cl[0] == '"' ? cl.IndexOf('"', 1) : cl.IndexOf(' ');
            return end < 0 ? "" : cl.Substring(end + 1).Trim();
        }
    }
}

/// <summary>
/// 파일을 쓰고 있는 프로그램 찾기 · 닫기 · 다시 실행.
///  - Windows Restart Manager: 실행 파일(이미지), DLL, 열린 파일까지 잡는 프로세스를 알려 준다.
///  - 덧붙여 실행 파일 경로가 대상 .exe 와 같은 프로세스도 찾는다 (Restart Manager 가 놓치는 경우 대비).
/// UI 와 무관해서 나중에 스크립트·명령줄 자동화에서도 그대로 쓸 수 있다.
/// </summary>
public static class RunningProcesses
{
    private const int RmService = 3;
    private const int ErrorMoreData = 234;
    private const int ChunkSize = 1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint handle, int flags, StringBuilder key);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint handle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint handle, uint nFiles, string[] files, uint nApps, RM_UNIQUE_PROCESS[]? apps, uint nServices, string[]? services);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint handle, out uint needed, ref uint count, [In, Out] RM_PROCESS_INFO[]? info, ref uint rebootReasons);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private static readonly int SelfPid = Process.GetCurrentProcess().Id;

    /// <summary>files 중 하나라도 쓰고 있는 프로세스 (이 프로그램 자신도 IsSelf 로 포함)</summary>
    public static List<LockingProcess> FindUsing(IReadOnlyCollection<string> files, Action? onProgress = null)
    {
        var result = new Dictionary<int, LockingProcess>();
        var existing = files.Where(File.Exists).ToList();

        // 1) Restart Manager (한 번에 너무 많이 넘기지 않도록 나눠서)
        for (int i = 0; i < existing.Count; i += ChunkSize)
        {
            foreach (var info in RestartManagerList(existing.Skip(i).Take(ChunkSize).ToArray()))
            {
                int pid = info.Process.dwProcessId;
                if (result.ContainsKey(pid)) continue;
                long ft = ((long)info.Process.ProcessStartTime.dwHighDateTime << 32) | (uint)info.Process.ProcessStartTime.dwLowDateTime;
                result[pid] = new LockingProcess
                {
                    Pid = pid,
                    StartTime = ft > 0 ? DateTime.FromFileTime(ft) : DateTime.MinValue,
                    // 표시 이름: 실행 파일 이름이 알아보기 쉽다 (서비스는 서비스 이름)
                    Name = info.ApplicationType == RmService || ImagePath(pid) is not { } ip ? info.strAppName : Path.GetFileName(ip),
                    ExePath = ImagePath(pid),
                    IsService = info.ApplicationType == RmService,
                    IsSelf = pid == SelfPid,
                };
            }
            onProgress?.Invoke();
        }

        // 2) 실행 파일 경로가 대상 .exe 와 같은 프로세스
        var exes = new HashSet<string>(existing.Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase);
        if (exes.Count > 0)
        {
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    if (result.ContainsKey(p.Id)) continue;
                    var path = ImagePath(p.Id);
                    if (path == null || !exes.Contains(path)) continue;
                    DateTime start;
                    try { start = p.StartTime; } catch { start = DateTime.MinValue; }
                    result[p.Id] = new LockingProcess
                    {
                        Pid = p.Id, StartTime = start, Name = Path.GetFileName(path), ExePath = path,
                        IsSelf = p.Id == SelfPid,
                    };
                }
            }
            onProgress?.Invoke();
        }

        // 3) 다시 실행할 때 쓸 명령줄 (서비스·자신 제외)
        foreach (var lp in result.Values.Where(x => !x.IsService && !x.IsSelf)) lp.CommandLine = CommandLineOf(lp.Pid);
        return result.Values.OrderBy(x => x.IsSelf).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static RM_PROCESS_INFO[] RestartManagerList(string[] files)
    {
        var key = new StringBuilder(64);
        if (RmStartSession(out uint h, 0, key) != 0) return [];
        try
        {
            if (RmRegisterResources(h, (uint)files.Length, files, 0, null, 0, null) != 0) return [];
            uint count = 0, reasons = 0;
            int rc = RmGetList(h, out uint needed, ref count, null, ref reasons);
            if (rc == 0 || (rc != ErrorMoreData) || needed == 0) return [];
            var arr = new RM_PROCESS_INFO[needed];
            count = needed;
            rc = RmGetList(h, out needed, ref count, arr, ref reasons);
            return rc == 0 ? arr.Take((int)count).ToArray() : [];
        }
        finally
        {
            RmEndSession(h);
        }
    }

    public static string? ImagePath(int pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private static string? CommandLineOf(int pid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            searcher.Options.Timeout = TimeSpan.FromSeconds(3);
            foreach (var o in searcher.Get()) using (o) return o["CommandLine"] as string;
        }
        catch { }
        return null;
    }

    /// <summary>창에 닫기를 요청하고 끝날 때까지 기다린다. 창이 없거나 시간 안에 안 끝나면 false.</summary>
    public static bool CloseGracefully(LockingProcess lp, int waitMs = 8000)
    {
        try
        {
            using var p = Process.GetProcessById(lp.Pid);
            if (!p.CloseMainWindow()) return false;
            return p.WaitForExit(waitMs);
        }
        catch (ArgumentException) { return true; }   // 이미 끝남
        catch { return false; }
    }

    /// <summary>강제 종료. 저장하지 않은 내용은 사라진다.</summary>
    public static bool Kill(LockingProcess lp, int waitMs = 5000)
    {
        try
        {
            using var p = Process.GetProcessById(lp.Pid);
            p.Kill();
            return p.WaitForExit(waitMs);
        }
        catch (ArgumentException) { return true; }
        catch { return false; }
    }

    /// <summary>같은 실행 파일·인자로 다시 실행. 실패하면 오류 메시지.</summary>
    public static string? Restart(LockingProcess lp)
    {
        if (lp.ExePath == null) return $"{lp.Name}: 실행 파일 경로를 알 수 없습니다.";
        try
        {
            Process.Start(new ProcessStartInfo(lp.ExePath, lp.Arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(lp.ExePath) ?? "",
            })?.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            return $"{lp.Name}: {ex.Message}";
        }
    }
}
