using System.Runtime.InteropServices;

namespace MPCommander.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct FmEntry
{
    public IntPtr Name;
    public ulong Size;
    public ulong LastWrite;
    public uint Attributes;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct FmSearchParams
{
    [MarshalAs(UnmanagedType.LPWStr)] public string Root;
    [MarshalAs(UnmanagedType.LPWStr)] public string Mask;
    [MarshalAs(UnmanagedType.LPWStr)] public string? Text;
    public int CaseSensitive;
    public int Recursive;
    public int IncludeHidden;
    public int ThreadCount;
    public int IoTimeoutMs;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FmSearchStatus
{
    public int Running;
    public int Reserved;
    public long DirsScanned;
    public long FilesScanned;
    public long Found;
    public long Errors;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FmMtpEntry
{
    public IntPtr ObjectId;
    public IntPtr Name;
    public ulong Size;
    public ulong LastWrite;
    public uint IsFolder;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FmMenuItem
{
    public int Id;
    public int Flags;     // 1 = 구분선, 2 = 비활성
    public IntPtr Text;   // LPWSTR
}

[UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
internal delegate int FmMtpDeviceCallback(string deviceId, string friendlyName, IntPtr user);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal unsafe delegate int FmMtpEntryCallback(FmMtpEntry* entries, int count, IntPtr user);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal unsafe delegate int FmEntryBatchCallback(FmEntry* entries, int count, IntPtr user);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int FmProgressCallback(ulong transferred, ulong total, IntPtr user);

internal static unsafe class NativeMethods
{
    private const string Dll = "FmCore.dll";

    [DllImport(Dll)] public static extern int FmGetVersion();
    [DllImport(Dll)] public static extern IntPtr FmOpenCurrentThread();
    [DllImport(Dll)] public static extern int FmCancelThreadIo(IntPtr thread);
    [DllImport(Dll)] public static extern void FmCloseHandle(IntPtr handle);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern int FmCompareNatural(string a, string b);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern int FmEnumDirectory(string path, FmEntryBatchCallback cb, IntPtr user, int* cancelFlag, int* progress);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern int FmCopyFile(string src, string dst, int overwrite, FmProgressCallback? cb, IntPtr user, int* cancelFlag);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern int FmMoveFile(string src, string dst, int overwrite, FmProgressCallback? cb, IntPtr user, int* cancelFlag);

    [DllImport(Dll)]
    public static extern int FmShellDelete(char* pathsDoubleNull, int permanent, IntPtr hwnd);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern IntPtr FmGetShellIcon(string name, int isDirectory, int large);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern int FmGetUncForDrive(string drive, char* buffer, int bufferLength);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern int FmGetDiskFree(string path, out ulong freeBytes, out ulong totalBytes);

    [DllImport(Dll)]
    public static extern IntPtr FmSearchStart(ref FmSearchParams p, FmEntryBatchCallback cb, IntPtr user);
    [DllImport(Dll)] public static extern void FmSearchCancel(IntPtr handle);
    [DllImport(Dll)] public static extern int FmSearchGetStatus(IntPtr handle, out FmSearchStatus status, char* currentDir, int length);
    [DllImport(Dll)] public static extern void FmSearchFree(IntPtr handle);

    public const int FM_MENU_RENAME = -2;

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern int FmShellContextMenu(IntPtr hwnd, string? folder, char* namesDoubleNull, int screenX, int screenY,
                                                FmMenuItem[] custom, int customCount, int extendedVerbs, out int shellInvoked);

    // ── MTP (반환값 HRESULT) ──
    [DllImport(Dll)] public static extern int FmMtpListDevices(FmMtpDeviceCallback cb, IntPtr user);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern IntPtr FmMtpOpen(string deviceId, out int hr);
    [DllImport(Dll)] public static extern void FmMtpClose(IntPtr device);
    [DllImport(Dll)] public static extern void FmMtpCancel(IntPtr device);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern int FmMtpEnumChildren(IntPtr device, string parentId, FmMtpEntryCallback cb, IntPtr user, int* cancelFlag, int* progress);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern int FmMtpDownload(IntPtr device, string objectId, string localPath, int overwrite, FmProgressCallback? cb, IntPtr user, int* cancelFlag);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern int FmMtpUpload(IntPtr device, string parentId, string localPath, string name, FmProgressCallback? cb, IntPtr user,
                                         int* cancelFlag, char* newObjectId, int newObjectIdLength);

    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern int FmMtpDelete(IntPtr device, string objectId, int recursive);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern int FmMtpCreateFolder(IntPtr device, string parentId, string name, char* newObjectId, int newObjectIdLength);

    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern int FmMtpRename(IntPtr device, string objectId, string newName);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern int FmMtpGetStorageInfo(IntPtr device, string storageId, out ulong freeBytes, out ulong totalBytes);

    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>Windows 와일드카드 비교 ("*.*" 는 점 없는 이름도 포함). 대소문자 무시.</summary>
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PathMatchSpecW(string file, string spec);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern bool SHObjectProperties(IntPtr hwnd, uint shopObjectType, string objectName, string? propertyPage);
    public const uint SHOP_FILEPATH = 2;
}
