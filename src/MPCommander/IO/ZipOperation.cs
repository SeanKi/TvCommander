using System.Globalization;
using System.IO.Compression;
using System.Text;
using MPCommander.Model;

namespace MPCommander.IO;

/// <summary>
/// 내장 ZIP 압축 / 압축 풀기 (Double Commander 의 Alt+F5 / Alt+F9).
/// 압축은 임시 파일에 쓴 뒤 완성되면 이름을 바꾸므로, 취소해도 깨진 ZIP 이 남지 않는다.
/// </summary>
public sealed class ZipOperation : ProgressOperation
{
    private const int BufferSize = 1024 * 1024;


    private readonly bool _pack;
    private readonly IReadOnlyList<string> _sources;
    private readonly string _target;
    private readonly CompressionLevel _level;
    private ConflictChoice? _conflictAll;

    private ZipOperation(bool pack, IReadOnlyList<string> sources, string target, CompressionLevel level = CompressionLevel.Optimal)
    {
        _pack = pack;
        _sources = sources;
        _target = target;
        _level = level;
    }

    /// <summary>level: 0 저장, 1 빠르게, 2·3 보통 (내장 ZIP 은 '최고' 단계가 따로 없다)</summary>
    public static ZipOperation Pack(IReadOnlyList<string> sources, string zipPath, int level = 2)
        => new(true, sources, zipPath, level switch { 0 => CompressionLevel.NoCompression, 1 => CompressionLevel.Fastest, _ => CompressionLevel.Optimal });
    public static ZipOperation Extract(string zipPath, string destDir) => new(false, new[] { zipPath }, destDir);

    public override string Title => _pack ? "압축" : "압축 풀기";
    /// <summary>결과물이 생긴 폴더 (패널 새로고침용)</summary>
    public string TargetDirectory => _pack ? PathUtil.Parent(_target) ?? _target : _target;

    protected override void Execute()
    {
        if (_pack) DoPack();
        else DoExtract();
    }

    // ───────────────────────── 압축 ─────────────────────────

    private void DoPack()
    {
        Phase = "목록 수집 중";
        var entries = new List<(string FullPath, string EntryName, bool IsDir, long Size)>();
        foreach (var raw in _sources)
        {
            ThrowIfCancelled();
            var src = PathUtil.TrimEnd(raw);
            var baseDir = PathUtil.Parent(src) ?? src;
            if (Directory.Exists(src))
            {
                entries.Add((src, Relative(baseDir, src) + "/", true, 0));
                Attempt(src, () => Collect(src, baseDir, entries));
            }
            else
            {
                Attempt(src, () => entries.Add((src, Relative(baseDir, src), false, new FileInfo(src).Length)));
            }
        }
        foreach (var e in entries)
        {
            if (e.IsDir) continue;
            TotalFiles++;
            TotalBytes += e.Size;
        }

        Phase = "압축 중";
        var temp = _target + ".tvc-tmp";
        try
        {
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, BufferSize))
            // 인코딩 미지정 = 한글 이름을 UTF-8 + UTF-8 표시로 저장 (어느 압축 프로그램에서도 안 깨짐)
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
            {
                long done = 0;
                foreach (var e in entries)
                {
                    ThrowIfCancelled();
                    if (e.IsDir)
                    {
                        zip.CreateEntry(e.EntryName);
                        continue;
                    }
                    CurrentFile = e.FullPath;
                    CurrentSize = e.Size;
                    CurrentDone = 0;
                    Touch();
                    bool ok = Attempt(e.FullPath, () =>
                    {
                        using var input = new FileStream(e.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, FileOptions.SequentialScan);
                        var entry = zip.CreateEntry(e.EntryName, _level);
                        entry.LastWriteTime = File.GetLastWriteTime(e.FullPath);
                        using var output = entry.Open();
                        Pump(input, output, done);
                    });
                    done += e.Size;
                    DoneBytes = done;
                    if (ok) DoneFiles++;
                }
            }
            ThrowIfCancelled();
            Compat.MoveReplace(temp, _target);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private void Collect(string dir, string baseDir, List<(string, string, bool, long)> entries)
    {
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            ThrowIfCancelled();
            var d = stack.Pop();
            CurrentFile = d;
            Touch();
            foreach (var info in new DirectoryInfo(d).EnumerateFileSystemInfos())
            {
                if (info is DirectoryInfo sub)
                {
                    entries.Add((sub.FullName, Relative(baseDir, sub.FullName) + "/", true, 0));
                    if (!sub.Attributes.HasFlag(FileAttributes.ReparsePoint)) stack.Push(sub.FullName);
                }
                else if (info is FileInfo f)
                {
                    entries.Add((f.FullName, Relative(baseDir, f.FullName), false, f.Length));
                }
            }
        }
    }

    private static string Relative(string baseDir, string path) => path.Substring(PathUtil.WithSlash(baseDir).Length).Replace('\\', '/');

    // ───────────────────────── 압축 풀기 ─────────────────────────

    private void DoExtract()
    {
        var zipPath = _sources[0];
        var dest = Path.GetFullPath(_target);
        var destPrefix = PathUtil.WithSlash(dest);
        Directory.CreateDirectory(dest);

        // UTF-8 표시가 없는 이름: 올바른 UTF-8 이면 UTF-8(맥/리눅스 압축), 아니면 시스템 ANSI(한국어 Windows = CP949)
        var legacy = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Read, new Utf8OrLegacyEncoding(legacy));

        foreach (var e in zip.Entries)
        {
            if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) continue;
            TotalFiles++;
            TotalBytes += e.Length;
        }

        Phase = "압축 푸는 중";
        long done = 0;
        foreach (var e in zip.Entries)
        {
            ThrowIfCancelled();
            var outPath = Path.GetFullPath(Path.Combine(dest, e.FullName.Replace('/', '\\')));
            if (!outPath.StartsWith(destPrefix, StringComparison.OrdinalIgnoreCase) && !PathUtil.Same(outPath, dest))
            {
                ReportError(e.FullName, "대상 폴더 밖을 가리키는 항목이라 건너뜁니다.");   // zip slip 방지
                continue;
            }
            if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\'))
            {
                Attempt(outPath, () => Directory.CreateDirectory(outPath));
                continue;
            }

            CurrentFile = e.FullName;
            CurrentSize = e.Length;
            CurrentDone = 0;
            Touch();

            if (File.Exists(outPath) && !ResolveConflict(e, outPath))
            {
                Skipped++;
                done += e.Length;
                DoneBytes = done;
                continue;
            }

            bool ok = Attempt(outPath, () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                var a = File.Exists(outPath) ? File.GetAttributes(outPath) : 0;
                if (a.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(outPath, a & ~FileAttributes.ReadOnly);
                using (var input = e.Open())
                using (var output = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
                    Pump(input, output, done);
                try { File.SetLastWriteTime(outPath, e.LastWriteTime.DateTime); } catch { }
            });
            done += e.Length;
            DoneBytes = done;
            if (ok) DoneFiles++;
        }
    }

    private bool ResolveConflict(ZipArchiveEntry e, string outPath)
    {
        var dst = new FileInfo(outPath);
        var choice = _conflictAll ?? AskConflict(new ConflictInfo(e.FullName, outPath, e.Length, e.LastWriteTime.DateTime, dst.Length, dst.LastWriteTime));
        Touch();
        switch (choice)
        {
            case ConflictChoice.OverwriteAll:
            case ConflictChoice.SkipAll:
            case ConflictChoice.OverwriteOlderAll:
                _conflictAll = choice;
                break;
            case ConflictChoice.Cancel:
                Cancel();
                throw new OperationCanceledException();
        }
        return choice switch
        {
            ConflictChoice.Overwrite or ConflictChoice.OverwriteAll => true,
            ConflictChoice.OverwriteOlderAll => e.LastWriteTime.DateTime > dst.LastWriteTime,
            _ => false,
        };
    }

    // ───────────────────────── 공통 ─────────────────────────

    private void Pump(Stream input, Stream output, long doneBefore)
    {
        var buf = new byte[BufferSize];
        long cur = 0;
        int n;
        while ((n = input.Read(buf, 0, buf.Length)) > 0)
        {
            ThrowIfCancelled();
            output.Write(buf, 0, n);
            cur += n;
            CurrentDone = cur;
            DoneBytes = doneBefore + cur;
            Touch();
        }
    }
}

/// <summary>이름 바이트가 올바른 UTF-8 이면 UTF-8, 아니면 레거시 코드 페이지로 읽는 인코딩 (ZIP 항목 이름 전용)</summary>
internal sealed class Utf8OrLegacyEncoding : Encoding
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
    private readonly Encoding _legacy;

    public Utf8OrLegacyEncoding(Encoding legacy) => _legacy = legacy;

    private Encoding Pick(byte[] bytes, int index, int count)
    {
        try
        {
            StrictUtf8.GetCharCount(bytes, index, count);
            return UTF8;
        }
        catch (DecoderFallbackException)
        {
            return _legacy;
        }
    }

    public override int GetByteCount(char[] chars, int index, int count) => UTF8.GetByteCount(chars, index, count);
    public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
        => UTF8.GetBytes(chars, charIndex, charCount, bytes, byteIndex);
    public override int GetCharCount(byte[] bytes, int index, int count) => Pick(bytes, index, count).GetCharCount(bytes, index, count);
    public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
        => Pick(bytes, byteIndex, byteCount).GetChars(bytes, byteIndex, byteCount, chars, charIndex);
    public override string GetString(byte[] bytes, int index, int count) => Pick(bytes, index, count).GetString(bytes, index, count);
    public override int GetMaxByteCount(int charCount) => UTF8.GetMaxByteCount(charCount);
    public override int GetMaxCharCount(int byteCount) => Math.Max(UTF8.GetMaxCharCount(byteCount), _legacy.GetMaxCharCount(byteCount));
}