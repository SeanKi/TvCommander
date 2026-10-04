using System.Runtime.InteropServices;

namespace TvCommander.Native;

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

    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern bool SHObjectProperties(IntPtr hwnd, uint shopObjectType, string objectName, string? propertyPage);
    public const uint SHOP_FILEPATH = 2;
}
