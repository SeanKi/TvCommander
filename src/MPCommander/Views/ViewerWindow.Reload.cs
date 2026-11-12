using System.Windows;
using System.Windows.Threading;
using MPCommander.IO;

namespace MPCommander.Views;

/// <summary>
/// 뷰어 새로 고침 (버튼 / F5) 과 Auto (파일이 바뀌면 자동).
///  - 1초마다 크기·수정 시각만 본다 (네트워크 파일도 가볍다).
///  - 바뀌었으면 앞 4 KB 와 '지난번 끝' 앞 4 KB 를 비교해서
///      같으면 = 끝에 덧붙기만 했다 → C++ 색인을 덧붙은 부분만 이어 하고, 찾기도 덧붙은 줄만 (앞 줄·캐시·찾은 목록은 그대로).
///      다르면 = 앞부분이 바뀌었다 (덮어쓰기·로그 교체) → 다시 연다. 보던 줄 위치·선택은 그대로 두고 찾기만 새로.
///  - 화면은 원래 보이는 줄만 그리므로, 덧붙은 줄이 화면 밖이면 스크롤 막대만 바뀐다.
///    끝을 보고 있었으면 끝을 따라간다 (tail -f).
/// </summary>
public partial class ViewerWindow
{
    private const int FingerprintBytes = 4096;
    private const long MaxTailSearchBytes = 64L << 20;   // 이보다 많이 덧붙으면 찾기는 처음부터 (C++ 가 더 빠르다)

    /// <summary>파일 모양 기억: 크기·시각 + 앞부분 + 끝부분 바이트</summary>
    private sealed record FileSnap(long Length, DateTime WriteTime, byte[] Head, byte[] Tail);

    private DispatcherTimer? _watch;
    private FileSnap? _snap;
    private bool _checking;
    private bool _follow;              // 덧붙은 줄 색인 중: 끝을 따라간다
    private long _tailFromLine = -1;   // 덧붙은 부분 색인이 끝나면 이 줄부터 찾는다
    private long _tailFromBytes;       // 덧붙기 전 크기
    private string _reloadNote = "";   // 아래 상태 줄에 붙이는 말 (마지막 새로 고침 시각 / 문제)

    private void InitReload()
    {
        _watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _watch.Tick += async (_, _) => await CheckChangesAsync();
        TextView.UserInteracted += () => _follow = false;   // 사용자가 움직이면 끝 따라가기 그만
        _syncing = true;
        AutoCheck.IsChecked = ViewZoom.Settings?.ViewerAutoReload == true;
        _syncing = false;
    }

    private void StartWatch()
    {
        if (_watch == null) return;
        if (AutoCheck.IsChecked == true && !_isImage) _watch.Start(); else _watch.Stop();
    }

    private void AutoCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        if (ViewZoom.Settings != null) ViewZoom.Settings.ViewerAutoReload = AutoCheck.IsChecked == true;
        StartWatch();
        if (AutoCheck.IsChecked == true) _ = CheckChangesAsync();   // 켜는 순간 한 번
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    // ───────── 파일 모양 ─────────

    /// <summary>지금 파일의 크기·시각과, 앞 / (tailEnd 앞) 바이트. 파일이 없으면 null</summary>
    private static FileSnap? TakeSnap(string path, long tailEnd)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096);
            long len = fs.Length;
            var time = File.GetLastWriteTimeUtc(path);
            var head = ReadAt(fs, 0, (int)Math.Min(FingerprintBytes, len));
            long end = tailEnd < 0 ? len : Math.Min(tailEnd, len);
            long from = Math.Max(0, end - FingerprintBytes);
            var tail = ReadAt(fs, from, (int)(end - from));
            return new FileSnap(len, time, head, tail);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static byte[] ReadAt(FileStream fs, long offset, int count)
    {
        var buf = new byte[count];
        fs.Position = offset;
        int read = 0;
        while (read < count) { int n = fs.Read(buf, read, count - read); if (n == 0) break; read += n; }
        if (read < count) Array.Resize(ref buf, read);
        return buf;
    }

    /// <summary>b 가 a 로 시작하는가</summary>
    private static bool StartsWith(byte[] b, byte[] a)
    {
        if (b.Length < a.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static bool SameBytes(byte[] a, byte[] b) => a.Length == b.Length && StartsWith(b, a);

    private async Task RememberSnapAsync()
    {
        try { _snap = await Task.Run(() => TakeSnap(_path, -1)); }
        catch { _snap = null; }
    }

    // ───────── 바뀜 확인 (Auto) ─────────

    private async Task CheckChangesAsync()
    {
        var source = _source;
        if (_checking || source == null || _hex || _snap is not { } old) return;
        // 색인·찾기가 아직 돌고 있으면 다음 번에 (덧붙이기는 색인이 끝난 다음에만 이어 할 수 있다)
        if (source.Status.Done == 0 || !_searchDone || _tailFromLine >= 0) return;
        _checking = true;
        try
        {
            FileSnap? now;
            try { now = await Task.Run(() => TakeSnap(_path, old.Length)); }
            catch (Exception ex) { _reloadNote = Loc.T("새로 고침 확인 실패: ") + ex.Message; return; }
            if (source != _source) return;
            if (now == null) { _reloadNote = Loc.T("파일이 없습니다 (지워졌거나 이름이 바뀜)"); return; }
            if (now.Length == old.Length && now.WriteTime == old.WriteTime) return;   // 그대로

            // 앞 4 KB 가 그대로이고, 지난번 끝 앞 4 KB 도 그대로면 '덧붙기만 했다'
            bool sameFront = now.Length >= old.Length && StartsWith(now.Head, old.Head) && SameBytes(now.Tail, old.Tail);
            if (sameFront && now.Length == old.Length) { _snap = now; return; }   // 시각만 바뀌었다 (내용 그대로)
            if (sameFront && now.Length > old.Length && await AppendAsync(source, old.Length)) return;
            await ReloadAsync();
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>끝에 덧붙은 부분만: C++ 색인을 이어 하고, 색인이 끝나면 (Poll) 덧붙은 줄만 찾는다</summary>
    private async Task<bool> AppendAsync(TextFileSource source, long oldLength)
    {
        long fromLine = source.LineOfOffset(oldLength);   // 지난번 끝이 걸친 줄 (줄 바꿈으로 끝났으면 새 줄)
        if (fromLine < 0) return false;
        bool follow = TextView.ShowsEnd;
        if (source.Append() != 1) return false;   // 늘지 않았다 = 연 파일과 경로의 파일이 다르다 (교체) → 다시 열기
        _follow = follow;
        _tailFromLine = _find.Length > 0 ? fromLine : -1;
        _tailFromBytes = oldLength;
        _snap = await Task.Run(() => TakeSnap(_path, -1));
        _reloadNote = Loc.F($"↻ {DateTime.Now:HH:mm:ss} 덧붙은 부분");
        Poll();
        return true;
    }

    /// <summary>Poll 에서: 덧붙은 부분 색인이 끝나면 찾기 · 끝 따라가기</summary>
    private void PollAppend()
    {
        if (_source == null) return;
        bool indexing = _source.Status.Done == 0;
        if (_follow)
        {
            TextView.ScrollToEnd();
            if (!indexing) _follow = false;
        }
        if (_tailFromLine >= 0 && !indexing)
        {
            long from = _tailFromLine;
            _tailFromLine = -1;
            StartTailSearch(from);
        }
    }

    /// <summary>
    /// 덧붙은 줄만 찾기: from 줄부터 끝까지. 지난번 마지막 줄이 길어졌을 수 있으니 from 줄 이후의 찾은 것은 빼고 다시 센다.
    /// 목록·선택·스크롤은 그대로 두고 끝에 붙인다.
    /// </summary>
    private void StartTailSearch(long from)
    {
        var source = _source;
        if (source == null || _find.Length == 0 || _findError != null) return;
        long lineCount = source.Status.LineCount;
        long bytes = source.Status.FileSize;
        // 너무 많이 덧붙었으면 처음부터 (C++ 바이트 찾기가 줄 단위 확인보다 빠르다)
        if (bytes - _tailFromBytes > MaxTailSearchBytes) { RestartSearchKeepView(); return; }

        // from 줄 이후에 있던 찾은 것 빼기 (보통 0~1 줄)
        int keep = LowerBound(from);
        for (int i = _hits.Count - 1; i >= keep; i--)
        {
            Interlocked.Add(ref _totalMatches, -_hits[i].Count);
            Interlocked.Decrement(ref _totalLines);
            _nextFirstMatch -= _hits[i].Count;
            _hits.RemoveAt(i);
        }
        if (_curHit >= _hits.Count) { _curHit = _curMatch = -1; }
        HitList.Refresh();

        int gen = _gen;
        string find = _find;
        bool matchCase = _matchCase;
        var re = _regex;
        long listed = _hits.Count;
        _userMoved = true;   // 새로 찾은 것으로 화면을 옮기지 않는다
        _searchDone = false;
        UpdateFindCount();
        StartBackground("ViewerTail", () =>
        {
            try
            {
                for (long line = from; line < lineCount && gen == _gen; line++)
                {
                    var text = source.GetLineSync(line);
                    var hit = MakeHit(line, text, find, matchCase, re);
                    if (hit == null) continue;
                    Interlocked.Add(ref _totalMatches, hit.Count);
                    Interlocked.Increment(ref _totalLines);
                    if (listed >= MaxListLines) continue;   // 목록은 그만, 개수만
                    listed++;
                    _pending.Enqueue(hit);
                }
            }
            catch (Exception ex) { if (gen == _gen) _findError = Loc.T("찾기 실패: ") + ex.Message; }
            if (gen == _gen) _searchDone = true;
        });
    }

    /// <summary>찾기를 처음부터 다시 하되, 화면은 지금 위치 그대로 (찾은 첫 줄로 옮기지 않는다)</summary>
    private void RestartSearchKeepView()
    {
        if (_find.Length == 0 && FindBox.Text.Length == 0) return;
        StartSearch();
        _userMoved = true;
    }

    // ───────── 다시 열기 ─────────

    /// <summary>새로 고침: 파일을 다시 연다. 보던 줄 위치·가로 위치·선택은 그대로, 끝을 보고 있었으면 끝을 따라간다</summary>
    private async Task ReloadAsync()
    {
        if (_isImage && _source == null && !_hex) { await LoadAsync(); return; }   // 그림
        if (_hex) { await ShowHexAsync(); await RememberSnapAsync(); return; }
        var old = _source;
        if (old == null) { await LoadAsync(); return; }
        bool follow = TextView.ShowsEnd && TextView.TopLine > 0;
        TextFileSource src;
        try { src = await GuardedIo.RunAsync(_ => TextFileSource.Open(_path, ForcedEncoding), 5000, _path); }
        catch (Exception ex) { _reloadNote = Loc.T("새로 고침 실패: ") + ex.Message; Poll(); return; }
        if (_source != old) { src.Dispose(); return; }   // 그 사이 창이 닫혔거나 다시 열었다

        StopSearch();
        _tailFromLine = -1;
        _source = src;
        ShowEncoding(src.Encoding);
        TextView.ReplaceSource(src);
        old.Dispose();
        _follow = follow;
        _reloadNote = Loc.F($"↻ {DateTime.Now:HH:mm:ss} 다시 읽음");
        await RememberSnapAsync();
        if (_find.Length > 0 || FindBox.Text.Length > 0) RestartSearchKeepView();
        Poll();
    }
}
