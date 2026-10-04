using System.Diagnostics;
using System.IO.Pipes;
using TvCommander.IO;

// 사용법: IoProbe <경로>...   각 경로를 DirectoryLoader 로 열어 결과와 걸린 시간을 출력한다.
//        IoProbe --hang      응답 없는 동기 I/O(파이프 읽기)를 GuardedIo 가 끊어내는지 확인한다.
if (args is ["--hang"])
{
    using var server = new NamedPipeServerStream("tvc-hang", PipeDirection.Out);
    var sw = Stopwatch.StartNew();
    try
    {
        await GuardedIo.RunAsync(_ =>
        {
            using var fs = new FileStream(@"\\.\pipe\tvc-hang", FileMode.Open, FileAccess.Read);
            return fs.Read(new byte[16], 0, 16);   // 서버가 아무것도 안 보내므로 영원히 대기
        }, 3000, "pipe");
        Console.WriteLine("UNEXPECTED: read returned");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL  {sw.ElapsedMilliseconds,6} ms  {ex.GetType().Name}: {ex.Message}");
    }
    return;
}

foreach (var path in args)
{
    var sw = Stopwatch.StartNew();
    try
    {
        var items = await DirectoryLoader.LoadAsync(path, showHidden: true, null, CancellationToken.None);
        Console.WriteLine($"OK    {sw.ElapsedMilliseconds,6} ms  {items.Count,8:N0} items  {path}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL  {sw.ElapsedMilliseconds,6} ms  {ex.GetType().Name}: {ex.Message}");
    }
}
