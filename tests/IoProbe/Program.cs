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

if (args is ["--history", var iniPath])
{
    // 히스토리 저장/불러오기, 스마트 순위, 최대 개수 점검
    File.Delete(iniPath);
    var h = TvCommander.Model.DirectoryHistory.Load(iniPath);
    for (int i = 0; i < 40; i++) { h.RecordVisit($@"C:\tmp\d{i}"); h.AddRecent($@"C:\tmp\d{i}"); }
    for (int i = 0; i < 3; i++) h.RecordVisit(@"C:\work\proj_a");
    h.AddDwell(@"C:\work\proj_a", TimeSpan.FromMinutes(12));
    h.RecordVisit(@"D:\data"); h.AddDwell(@"D:\data", TimeSpan.FromHours(5));   // 1회 최대 30분으로 잘림
    h.RecordVisit(@"\\nas\share"); h.RecordVisit(@"\\nas\share");
    h.AddRecent(@"C:\work\proj_a");
    h.Save();

    var r = TvCommander.Model.DirectoryHistory.Load(iniPath);
    Console.WriteLine($"MaxHistory={r.MaxHistory} SmartCount={r.SmartCount}");
    Console.WriteLine("-- smart");
    foreach (var e in r.GetSmart()) Console.WriteLine($"  {e.Path}  visits={e.Visits} dwell={e.DwellSeconds:0}s");
    var recent = r.GetRecent(r.GetSmart().Select(e => e.Path));
    Console.WriteLine($"-- recent (smart 제외) count={recent.Count}, first={recent[0]}, last={recent[^1]}");
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
