using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using MPCommander.IO;
using MPCommander.Model;

namespace MPCommander.Views;

/// <summary>Ctrl+C / Ctrl+X / Ctrl+V (탐색기와 호환) 와 차단 해제</summary>
public partial class MainWindow
{
    private const string ClipPathsFormat = "MPCommander.Paths";   // 휴대폰·FTP·WebDAV 경로도 담는 앱 전용 형식
    private const string ClipCutFormat = "MPCommander.Cut";
    private const string DropEffectFormat = "Preferred DropEffect"; // 탐색기: 5 = 복사, 2 = 잘라내기

    /// <summary>Ctrl+C / Ctrl+X: 표시한 항목(없으면 커서 항목)을 클립보드에. 탐색기에서도 붙여넣을 수 있다.</summary>
    public void CmdClipboardCopy(bool cut)
    {
        var p = P;
        var items = p.GetSelectedOrCurrent();
        if (items.Count == 0) return;
        var paths = items.Select(i => i.FullPath).ToArray();

        var data = new DataObject();
        data.SetData(ClipPathsFormat, paths);
        data.SetData(ClipCutFormat, cut ? "1" : "0");
        if (!paths.Any(PathUtil.IsVirtual))
        {
            var list = new StringCollection();
            list.AddRange(paths);
            data.SetFileDropList(list);
            data.SetData(DropEffectFormat, new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
        }

        if (!TryClipboard(() => Clipboard.SetDataObject(data, true)))
        {
            ShowError("클립보드를 사용할 수 없습니다. 다른 프로그램이 클립보드를 쓰고 있는지 확인하세요.");
            return;
        }
        string what = items.Count == 1 ? $"'{items[0].Name}'" : $"{items.Count}개 항목";
        p.FlashStatus($"{(cut ? "잘라내기" : "복사")}: {what} — 붙여넣을 곳에서 Ctrl+V");
    }

    /// <summary>Ctrl+V: 클립보드의 파일을 활성 패널 폴더로 복사(잘라내기였으면 이동)</summary>
    public async void CmdPaste()
    {
        var p = P;
        var dir = p.CurrentPath;
        if (dir == null) return;

        string[]? paths = null;
        bool cut = false;
        TryClipboard(() =>
        {
            var data = Clipboard.GetDataObject();
            if (data == null) return;
            if (data.GetDataPresent(ClipPathsFormat))
            {
                paths = data.GetData(ClipPathsFormat) as string[];
                cut = data.GetData(ClipCutFormat) as string == "1";
            }
            else if (data.GetDataPresent(DataFormats.FileDrop))
            {
                paths = data.GetData(DataFormats.FileDrop) as string[];
                if (data.GetDataPresent(DropEffectFormat) && data.GetData(DropEffectFormat) is MemoryStream ms && ms.Length >= 4)
                {
                    var b = new byte[4];
                    ms.Read(b, 0, 4);
                    cut = (BitConverter.ToInt32(b, 0) & 2) != 0;   // DROPEFFECT_MOVE
                }
            }
        });

        if (paths == null || paths.Length == 0)
        {
            p.FlashStatus("붙여넣을 파일이 클립보드에 없습니다.");
            return;
        }
        // 잘라낸 것을 같은 폴더에 붙여넣으면 할 일이 없다
        if (cut) paths = paths.Where(x => !PathUtil.Same(PathUtil.Parent(x), dir)).ToArray();
        if (paths.Length == 0) return;

        await RunFileOperationAsync(cut ? OpKind.Move : OpKind.Copy, paths, PathUtil.WithSlash(dir), null, renameCopiesInSameFolder: true);
        if (cut) TryClipboard(Clipboard.Clear);   // 탐색기처럼 이동한 뒤에는 비운다
    }

    /// <summary>클립보드는 다른 프로그램이 잠깐 잡고 있을 수 있어 몇 번 다시 시도한다.</summary>
    private static bool TryClipboard(Action action)
    {
        for (int i = 0; i < 6; i++)
        {
            try
            {
                action();
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(50);
            }
            catch (ExternalException)
            {
                Thread.Sleep(50);
            }
        }
        return false;
    }

    // ───────────────────────── 차단 해제 ─────────────────────────

    /// <summary>
    /// 표시한 항목(없으면 커서 항목, 그것도 없으면 현재 폴더)의 '인터넷에서 받은 파일' 차단 표시를 지운다.
    /// 설정 창에서 하위 폴더 포함 여부와 대상 형식을 고른다.
    /// </summary>
    public async void CmdUnblock()
    {
        var p = P;
        var dir = p.CurrentPath;
        if (dir == null) return;
        if (PathUtil.IsVirtual(dir))
        {
            p.FlashStatus("휴대폰·FTP·WebDAV 파일에는 차단 표시가 없습니다. PC 로 복사한 뒤 차단 해제하세요.");
            return;
        }

        var items = p.GetSelectedOrCurrent();
        var targets = items.Count > 0 ? items.Select(i => i.FullPath).ToList() : new List<string> { dir };
        int folders = items.Count(i => i.IsDirectory), files = items.Count - folders;
        string desc = items.Count switch
        {
            0 => $"현재 폴더: {dir}",
            1 => $"{(items[0].IsDirectory ? "폴더" : "파일")} '{items[0].Name}'  ({dir})",
            _ => $"{items.Count}개 항목 — 폴더 {folders}개, 파일 {files}개  ({dir})",
        };
        if (!UnblockDialog.Show(this, _settings, desc))
        {
            p.FocusList();
            return;
        }
        SaveSettings();

        var (ok, host) = await NetworkHealth.CheckPathAsync(dir);
        if (!ok) { ShowError(new HostUnreachableException(host!).Message); return; }

        var op = new UnblockOperation(targets, _settings.UnblockRecursive, _settings.UnblockTypes.SplitTrim(';'), _settings.UnblockAllFiles);
        await RunWithProgressAsync(op);

        // 자물쇠 표시를 다시 확인하도록 이 폴더(와 그 아래)를 보고 있는 패널을 새로고침
        await Task.WhenAll(VisiblePanels
            .Where(x => x.CurrentPath != null && (PathUtil.Same(x.CurrentPath, dir) || PathUtil.IsUnder(x.CurrentPath, dir)))
            .Select(x => x.RefreshAsync()));

        var summary = $"차단 해제: {op.Unblocked:N0}개 파일 (검사 {op.TotalFiles:N0}개, 원래 차단 없음 {op.NotBlocked:N0}개" +
                      (op.Errors > 0 ? $", 실패 {op.Errors:N0}개)" : ")") + (op.IsCancelled ? " — 중간에 취소됨" : "");
        p.FlashStatus(summary);
        if (op.Errors > 0)
            MessageBox.Show(this, summary + "\n\n" + string.Join("\n", op.Failures) + (op.Errors > op.Failures.Count ? "\n..." : ""),
                "차단 해제", MessageBoxButton.OK, MessageBoxImage.Warning);
        p.FocusList();
    }
}
