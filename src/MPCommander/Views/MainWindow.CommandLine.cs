using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Input;
using MPCommander.IO;
using MPCommander.Model;

namespace MPCommander.Views;

/// <summary>
/// 하단 명령줄. 활성 패널의 폴더를 작업 폴더로 실행한다 (Total/Double Commander 와 같은 방식).
///  - Enter: 프로그램/문서는 바로 실행, 내부 명령(dir, copy...)·배치 파일은 cmd 창에서 실행 (창 유지)
///  - Shift+Enter: 항상 cmd 창에서 실행하고 창을 남긴다
///  - "cd 경로", "cd..", "D:" 는 실행하지 않고 활성 패널을 이동한다
/// </summary>
public partial class MainWindow
{
    private const int MaxCommandHistory = 50;
    private static readonly Regex CdRegex = new(@"^(?:cd|chdir)(?:\s+/d)?(?=$|[\s\\./])\s*(?<path>.*)$", RegexOptions.IgnoreCase);
    private static readonly Regex DriveRegex = new(@"^[a-zA-Z]:\\?$");

    private void InitCommandLine()
    {
        CommandBox.ItemsSource = _settings.CommandHistory;
        UpdateCommandPrompt();
    }

    /// <summary>프롬프트에 활성 패널 경로 표시 (길면 앞쪽을 줄인다)</summary>
    private void UpdateCommandPrompt()
    {
        var path = P.CurrentPath ?? "";
        CommandPrompt.ToolTip = path;
        CommandPrompt.Text = (path.Length > 60 ? "…" + path[^59..] : path) + ">";
    }

    private void OnPanelPathChanged(FilePanel p)
    {
        if (p == _active) UpdateCommandPrompt();
    }

    public void FocusCommandLine()
    {
        CommandBox.Focus();
        if (CommandBox.Template.FindName("PART_EditableTextBox", CommandBox) is System.Windows.Controls.TextBox tb)
            tb.CaretIndex = tb.Text.Length;
    }

    /// <summary>목록에서 Ctrl+Enter 등: 명령줄 끝에 텍스트를 덧붙이고 포커스</summary>
    private void AppendToCommandLine(string text)
    {
        if (text.Contains(' ')) text = "\"" + text + "\"";
        var cur = CommandBox.Text;
        CommandBox.Text = cur.Length == 0 || cur.EndsWith(' ') ? cur + text + " " : cur + " " + text + " ";
        FocusCommandLine();
    }

    private void CommandBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when !CommandBox.IsDropDownOpen:
                e.Handled = true;
                var text = CommandBox.Text.Trim();
                if (text.Length == 0) { P.FocusList(); return; }
                RunCommandLine(text, keepOpen: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                break;
            case Key.Escape when !CommandBox.IsDropDownOpen:
                e.Handled = true;
                CommandBox.Text = "";
                P.FocusList();
                break;
            case Key.Tab:
                e.Handled = true;
                P.FocusList();
                break;
        }
    }

    private async void RunCommandLine(string command, bool keepOpen)
    {
        AddCommandHistory(command);
        CommandBox.Text = "";
        var panel = P;
        var dir = panel.CurrentPath;

        // 패널 이동 명령
        if (DriveRegex.IsMatch(command))
        {
            await panel.NavigateAsync(command[..2] + "\\");
            panel.FocusList();
            return;
        }
        var cd = CdRegex.Match(command);
        if (cd.Success)
        {
            var target = cd.Groups["path"].Value.Trim().Trim('"');
            if (target.Length > 0) await panel.NavigateAsync(target);
            panel.FocusList();
            return;
        }

        if (dir == null) return;
        if (PathUtil.IsVirtual(dir))
        {
            panel.FlashStatus("원격(휴대폰·FTP·WebDAV) 폴더에서는 명령을 실행할 수 없습니다.");
            return;
        }
        var (ok, host) = await NetworkHealth.CheckPathAsync(dir);
        if (!ok) { ShowError(new HostUnreachableException(host!).Message); return; }

        panel.FlashStatus($"실행: {command}");
        panel.FocusList();

        // 실행 파일 찾기와 시작은 네트워크 경로에서 느릴 수 있으므로 작업 스레드에서
        var error = await Task.Run(() =>
        {
            try
            {
                StartCommand(command, dir, keepOpen);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        });
        if (error != null) ShowError($"실행하지 못했습니다: {command}\n{error}");

        // 명령이 파일을 바꿨을 수 있다 (로컬은 감시기가, 네트워크는 여기서 새로고침)
        await Task.Delay(1500);
        if (PathUtil.Same(panel.CurrentPath, dir)) await panel.RefreshAsync();
    }

    private static void StartCommand(string command, string dir, bool keepOpen)
    {
        var (program, args) = SplitCommand(command);
        var resolved = keepOpen ? null : ResolveProgram(program, dir);
        var ext = resolved == null ? "" : Path.GetExtension(resolved).ToLowerInvariant();
        bool unc = dir.StartsWith(@"\\");

        if (resolved != null && ext is ".exe" or ".com")
        {
            // 콘솔 프로그램은 새 콘솔 창에서, GUI 프로그램은 그대로 뜬다
            Process.Start(new ProcessStartInfo(resolved, args) { UseShellExecute = false, WorkingDirectory = dir });
            return;
        }
        if (resolved != null && ext is not (".bat" or ".cmd"))
        {
            // 문서, 바로 가기 등은 연결된 프로그램으로
            Process.Start(new ProcessStartInfo(resolved, args) { UseShellExecute = true, WorkingDirectory = unc ? "" : dir });
            return;
        }

        // 내부 명령(dir, copy, echo...)·배치 파일·Shift+Enter: cmd 창에서 실행하고 창을 남긴다.
        // cmd 는 UNC 를 작업 폴더로 못 쓰므로 pushd 로 임시 드라이브를 잡는다.
        var inner = unc ? $"pushd \"{dir}\" && {command}" : command;
        Process.Start(new ProcessStartInfo("cmd.exe", $"/s /k \"{inner}\"")
        {
            UseShellExecute = false,
            WorkingDirectory = unc ? Environment.SystemDirectory : dir,
        });
    }

    /// <summary>첫 단어(따옴표 가능)와 나머지 인수로 나눈다</summary>
    private static (string Program, string Args) SplitCommand(string command)
    {
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            if (end > 0) return (command[1..end], command[(end + 1)..].TrimStart());
        }
        int sp = command.IndexOfAny([' ', '\t']);
        return sp < 0 ? (command, "") : (command[..sp], command[(sp + 1)..].TrimStart());
    }

    /// <summary>작업 폴더 → PATH 순으로 프로그램/파일을 찾는다 (PATHEXT 확장자 포함). 없으면 null (내부 명령으로 간주).</summary>
    private static string? ResolveProgram(string program, string dir)
    {
        var exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        string? Probe(string basePath)
        {
            if (Path.HasExtension(basePath) && File.Exists(basePath)) return basePath;
            foreach (var e in exts)
                if (File.Exists(basePath + e)) return basePath + e;
            return null;
        }

        try
        {
            if (program.IndexOfAny(['\\', '/', ':']) >= 0)
                return Probe(Path.GetFullPath(Path.Combine(dir, program)));

            if (Probe(Path.Combine(dir, program)) is { } local) return local;
            foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                if (p.StartsWith(@"\\")) continue;   // 네트워크 PATH 는 건너뛴다 (먹통 방지)
                if (Probe(Path.Combine(p.Trim('"'), program)) is { } found) return found;
            }
        }
        catch
        {
            /* 잘못된 경로 문자 등 → 내부 명령으로 처리 */
        }
        return null;
    }

    private void AddCommandHistory(string command)
    {
        var list = _settings.CommandHistory;
        list.RemoveAll(c => c == command);
        list.Insert(0, command);
        if (list.Count > MaxCommandHistory) list.RemoveRange(MaxCommandHistory, list.Count - MaxCommandHistory);
        CommandBox.ItemsSource = null;
        CommandBox.ItemsSource = list;
    }
}
