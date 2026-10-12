using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace MPCommander.IO;

/// <summary>Everything 결과 한 항목</summary>
public readonly record struct EverythingItem(string FullPath, long Size, long LastWrite, bool IsFolder);

/// <summary>
/// Everything(voidtools) 검색 — SDK DLL 없이 Everything 창에 WM_COPYDATA 로 직접 묻는다 (Everything_IPC.h 의 QUERY2).
/// Everything 프로그램(트레이)이 떠 있어야 하고, 결과는 그 창이 우리 메시지 창으로 WM_COPYDATA 로 돌려준다.
/// UI 스레드(메시지 펌프)에서 불러야 한다.
/// </summary>
public static class Everything
{
    private const int WM_USER = 0x0400;
    private const int WM_COPYDATA = 0x004A;
    private const int IPC_GET_MAJOR_VERSION = 0, IPC_GET_MINOR_VERSION = 1, IPC_GET_REVISION = 2;
    private const int IPC_IS_DB_LOADED = 401;
    private const int COPYDATA_QUERY2W = 18;

    // 속성(0x100)은 묻지 않는다: Everything 기본 설정에서 색인되지 않아 결과마다 디스크를 읽는다 (16만 개 28초 vs 1초)
    private const uint REQUEST_FULL_PATH_AND_NAME = 0x4, REQUEST_SIZE = 0x10, REQUEST_DATE_MODIFIED = 0x40;
    private const uint ITEM_FOLDER = 0x1;
    private const uint SORT_NAME_ASCENDING = 1;   // 이름 정렬만 빠르다 (경로 정렬은 큰 결과에서 수 초) — 경로순은 받은 뒤 정렬

    // 1.4 는 앞의 것, 1.5 알파는 인스턴스 이름이 붙는다
    private static readonly string[] WindowClasses = ["EVERYTHING_TASKBAR_NOTIFICATION", "EVERYTHING_TASKBAR_NOTIFICATION_(1.5a)"];

    private static HwndSource? _receiver;
    private static uint _nextReply = 0x4D50_0000;
    private static readonly Dictionary<uint, TaskCompletionSource<List<EverythingItem>?>> Waiting = new();

    /// <summary>Everything 이 떠 있고 데이터베이스를 다 읽었으면 버전 ("1.4.1"), 아니면 null</summary>
    public static string? Version()
    {
        var hwnd = FindEverything();
        if (hwnd == IntPtr.Zero) return null;
        if (Ask(hwnd, IPC_IS_DB_LOADED) == 0) return null;
        long major = Ask(hwnd, IPC_GET_MAJOR_VERSION), minor = Ask(hwnd, IPC_GET_MINOR_VERSION);
        if (major < 1 || (major == 1 && minor < 4)) return null;   // QUERY2 는 1.4 부터
        return $"{major}.{minor}.{Ask(hwnd, IPC_GET_REVISION)}";
    }

    public static bool IsAvailable => Version() != null;

    /// <summary>Everything 검색 문법 그대로 찾는다. 응답이 없거나 Everything 이 없으면 null</summary>
    public static async Task<List<EverythingItem>?> QueryAsync(string search, int maxResults, int timeoutMs = 15000)
    {
        var hwnd = FindEverything();
        if (hwnd == IntPtr.Zero) return null;
        var receiver = Receiver();
        uint reply = ++_nextReply;
        var tcs = new TaskCompletionSource<List<EverythingItem>?>();
        Waiting[reply] = tcs;

        // EVERYTHING_IPC_QUERY2: reply_hwnd, reply_copydata_message, search_flags, offset, max_results, request_flags, sort_type, 검색어\0
        var text = Encoding.Unicode.GetBytes(search + "\0");
        var data = new byte[7 * 4 + text.Length];
        void Put(int i, uint v) => BitConverter.GetBytes(v).CopyTo(data, i * 4);
        Put(0, (uint)receiver.Handle.ToInt64());
        Put(1, reply);
        Put(2, 0);
        Put(3, 0);
        Put(4, (uint)maxResults);
        Put(5, REQUEST_FULL_PATH_AND_NAME | REQUEST_SIZE | REQUEST_DATE_MODIFIED);
        Put(6, SORT_NAME_ASCENDING);
        text.CopyTo(data, 7 * 4);

        var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var cds = new COPYDATASTRUCT { dwData = (IntPtr)COPYDATA_QUERY2W, cbData = data.Length, lpData = pin.AddrOfPinnedObject() };
            // 답은 Everything 스레드가 보내는 WM_COPYDATA 로 온다 (SMTO_NORMAL: 기다리는 동안 들어오는 메시지도 처리)
            if (SendMessageTimeout(hwnd, WM_COPYDATA, receiver.Handle, ref cds, 0x0002 /*SMTO_ABORTIFHUNG*/, 3000, out var ok) == IntPtr.Zero || ok == IntPtr.Zero)
            {
                Waiting.Remove(reply);
                return null;
            }
        }
        finally { pin.Free(); }

        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        Waiting.Remove(reply);
        if (done != tcs.Task) return null;
        var items = tcs.Task.Result;
        // 폴더별로 모이게 경로순 (빈 칸 대신 \ 가 먼저 오도록 폴더·이름을 나눠 비교)
        // (OrderBy 는 열쇠를 한 번씩만 만든다)
        return items?.OrderBy(i => i.FullPath.Substring(0, Math.Max(0, i.FullPath.LastIndexOf('\\'))), StringComparer.OrdinalIgnoreCase)
                     .ThenBy(i => i.FullPath, StringComparer.OrdinalIgnoreCase)
                     .ToList();
    }

    /// <summary>검색어 안의 경로·이름을 따옴표로 감싼다 (공백 대비)</summary>
    public static string Quote(string s) => "\"" + s.Replace("\"", "") + "\"";

    /// <summary>
    /// 파일 찾기 조건을 Everything 검색어로. root 가 null 이면 모든 드라이브.
    /// 경로(\ 가 든 말)는 전체 경로에, 와일드카드 이름은 이름 전체에 맞춰진다.
    /// </summary>
    public static string BuildQuery(string? root, bool recursive, string mask, bool filesOnly)
    {
        var sb = new StringBuilder();
        if (root != null)
        {
            var dir = root.Length > 3 ? root.TrimEnd('\\') : root;
            if (recursive) sb.Append(Quote(dir.EndsWith("\\") ? dir : dir + "\\"));
            else sb.Append("parent:").Append(Quote(dir));
        }
        var masks = mask.SplitTrim(';').ToList();
        if (masks.Count > 0 && !masks.Any(m => m is "*" or "*.*"))
        {
            sb.Append(' ');
            if (masks.Count == 1) sb.Append(Quote(masks[0]));
            else sb.Append('<').Append(string.Join("|", masks.Select(Quote))).Append('>');
        }
        if (filesOnly) sb.Append(" file:");
        // 휴지통 안(삭제한 파일의 $I/$R 사본)은 찾기 결과에 쓸모가 없다
        sb.Append(" !").Append(Quote(@"\$Recycle.Bin\"));
        return sb.ToString().Trim();
    }

    private static IntPtr FindEverything()
    {
        foreach (var c in WindowClasses)
        {
            var h = FindWindow(c, null);
            if (h != IntPtr.Zero) return h;
        }
        return IntPtr.Zero;
    }

    private static long Ask(IntPtr hwnd, int command)
        => SendMessageTimeout(hwnd, WM_USER, (IntPtr)command, IntPtr.Zero, 0x0002, 1000, out var r) == IntPtr.Zero ? 0 : r.ToInt64();

    private static HwndSource Receiver()
    {
        if (_receiver != null) return _receiver;
        // 보이지 않는 메시지 전용 창 (HWND_MESSAGE)
        var p = new HwndSourceParameters("MP-Commander Everything") { ParentWindow = (IntPtr)(-3), WindowStyle = 0 };
        _receiver = new HwndSource(p);
        _receiver.AddHook(Hook);
        return _receiver;
    }

    private static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_COPYDATA) return IntPtr.Zero;
        var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
        uint id = (uint)cds.dwData.ToInt64();
        if (!Waiting.TryGetValue(id, out var tcs)) return IntPtr.Zero;
        handled = true;
        List<EverythingItem>? items = null;
        try { items = Parse(cds.lpData, cds.cbData); }
        catch { }
        tcs.TrySetResult(items);
        return (IntPtr)1;
    }

    /// <summary>EVERYTHING_IPC_LIST2 풀기. 자료는 이 메시지 동안만 살아 있으므로 바로 복사한다</summary>
    private static List<EverythingItem> Parse(IntPtr p, int length)
    {
        uint U(int off) => (uint)Marshal.ReadInt32(p, off);
        uint numItems = U(4), flags = U(12);
        var list = new List<EverythingItem>((int)Math.Min(numItems, 1_000_000));
        const int head = 5 * 4;
        for (int i = 0; i < numItems; i++)
        {
            uint itemFlags = U(head + i * 8);
            int off = (int)U(head + i * 8 + 4);
            if (off <= 0 || off >= length) continue;
            string path = "";
            long size = 0, write = 0;
            // 항목 자료는 요청한 플래그 순서대로: 글자는 길이(DWORD) + 글자 + \0
            if ((flags & REQUEST_FULL_PATH_AND_NAME) != 0)
            {
                int len = (int)U(off);
                path = Marshal.PtrToStringUni(p + off + 4, len);
                off += 4 + (len + 1) * 2;
            }
            if ((flags & REQUEST_SIZE) != 0) { size = Marshal.ReadInt64(p, off); off += 8; }
            if ((flags & REQUEST_DATE_MODIFIED) != 0) { write = Marshal.ReadInt64(p, off); off += 8; }
            bool folder = (itemFlags & ITEM_FOLDER) != 0;
            list.Add(new EverythingItem(path, folder || size < 0 ? 0 : size, write < 0 ? 0 : write, folder));
        }
        return list;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string cls, string? title);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, int flags, int timeout, out IntPtr result);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr wParam, ref COPYDATASTRUCT lParam, int flags, int timeout, out IntPtr result);
}
