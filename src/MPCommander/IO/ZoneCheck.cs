using MPCommander.Native;

namespace MPCommander.IO;

/// <summary>목록의 파일 중 '인터넷에서 받은 파일' 표시(Zone.Identifier)가 있는 것 찾기 (C++ 에서 일괄 확인)</summary>
public static class ZoneCheck
{
    private const int Batch = 256;

    /// <summary>names[i] 가 차단된 파일이면 결과[i] = true. 작업 스레드(GuardedIo)에서 호출.</summary>
    public static unsafe bool[] Check(string dir, IReadOnlyList<string> names, IoContext ctx)
    {
        var result = new bool[names.Count];
        var flags = new byte[Batch];
        for (int start = 0; start < names.Count; start += Batch)
        {
            if (ctx.IsCancelled) throw new OperationCanceledException();
            int n = Math.Min(Batch, names.Count - start);
            var buffer = (string.Join("\0", names.Skip(start).Take(n)) + "\0\0").ToCharArray();
            fixed (char* p = buffer)
            fixed (byte* r = flags)
            {
                int rc = NativeMethods.FmCheckZone(dir, p, r, n, ctx.CancelFlag, ctx.ProgressCounter);
                if (rc != 0) throw Win32Errors.ToIOException(rc, dir);
            }
            for (int i = 0; i < n; i++) result[start + i] = flags[i] != 0;
        }
        return result;
    }
}
