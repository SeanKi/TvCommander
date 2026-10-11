using System.Windows;
using MPCommander.IO;
using MPCommander.Model;

namespace MPCommander.Views;

/// <summary>백업 후 복사 (Backup-Copy): 프로그램 업데이트처럼 대상의 옛 파일을 백업해 두고 새 파일로 바꾸기</summary>
public partial class MainWindow
{
    /// <summary>
    /// Ctrl+B / 툴바: 표시한 항목(없으면 커서 항목)을 대상 패널 폴더로 백업 후 복사.
    ///  1) 대상 파일을 쓰는 프로그램 확인 → 닫기 안내  2) 덮어쓸 파일을 backup\yyyyMMdd_HHmm 에 백업
    ///  3) 복사 (사용 중이면 닫기/이름 바꿔 교체 등 선택)  4) 닫았던 프로그램 다시 실행 (옵션)
    /// </summary>
    public async void CmdBackupCopy()
    {
        var p = P;
        var items = p.GetSelectedOrCurrent();
        if (items.Count == 0 || p.CurrentPath == null) return;
        if (PathUtil.IsVirtual(p.CurrentPath)) { p.FlashStatus("백업 복사는 PC 의 파일만 됩니다. 원격 파일은 먼저 PC 로 복사하세요."); return; }

        var targetDir = _target?.CurrentPath;
        if (targetDir == null || _target == p || PathUtil.IsVirtual(targetDir))
        {
            p.FlashStatus("백업 복사는 대상 패널(PC 폴더)로 복사합니다. 대상 패널을 PC 폴더로 여세요.");
            return;
        }
        if (PathUtil.Same(targetDir, p.CurrentPath)) { p.FlashStatus("원본과 대상 폴더가 같습니다."); return; }

        var sources = items.Select(i => i.FullPath).ToList();
        foreach (var path in new[] { p.CurrentPath, targetDir })
        {
            var (ok, host) = await NetworkHealth.CheckPathAsync(path);
            if (!ok) { ShowError(new HostUnreachableException(host!).Message); return; }
        }

        // 계획: 원본을 훑고 대상에 같은 이름이 있는지 확인 (느린 네트워크에서 멈추지 않게 시간 제한)
        BackupCopyPlan plan;
        try
        {
            p.FlashStatus("백업 복사 준비 중...");
            plan = await GuardedIo.RunAsync(ctx => BackupCopyPlan.Build(sources, targetDir, ctx), 10000, targetDir);
        }
        catch (Exception ex) { ShowError(ex.Message); return; }
        if (plan.Items.Count == 0) { p.FlashStatus("복사할 파일이 없습니다 (빈 폴더)."); return; }

        string what = items.Count == 1 ? $"'{items[0].Name}'" : $"{items.Count}개 항목";
        var dlg = BackupCopyDialog.Show(this, _settings, plan, $"{what}  ({p.CurrentPath})");
        if (dlg == null) { p.FocusList(); return; }
        SaveSettings();

        var op = new BackupCopyOperation(plan, dlg.BackupPath);
        await RunWithProgressAsync(op);
        if (!op.IsCancelled) p.ClearMarks();

        // 다시 실행: 준비 창에서 보였거나 복사 중 닫은 프로그램 중 지금은 꺼져 있는 것 (서비스·자신 제외, 같은 실행 파일·인자는 한 번)
        var restartErrors = new List<string>();
        int restarted = 0;
        if (dlg.Restart && !op.IsCancelled)
        {
            var candidates = dlg.Seen.Concat(op.Closed)
                .Where(x => !x.IsSelf && !x.IsService && x.ExePath != null)
                .GroupBy(x => (x.ExePath!.ToLowerInvariant(), x.Arguments))
                .Select(g => g.First())
                .ToList();
            foreach (var lp in candidates)
            {
                if (lp.IsAlive) continue;   // 사용자가 닫지 않고 계속한 프로그램
                if (RunningProcesses.Restart(lp) is { } err) restartErrors.Add(err);
                else restarted++;
            }
        }

        await RefreshPanelsShowingAsync(targetDir, op.BackupCreated ? PathUtil.Parent(op.BackupPath) : null);

        var summary = $"백업 복사: 복사 {op.Copied:N0}개, 백업 {op.BackedUp:N0}개" +
                      (op.Renamed > 0 ? $", 이름 바꿔 교체 {op.Renamed}개" : "") +
                      (op.Errors > 0 ? $", 실패 {op.Errors}개" : "") +
                      (restarted > 0 ? $", 다시 실행 {restarted}개" : "") +
                      (op.IsCancelled ? " — 중간에 취소됨" : "") +
                      (op.BackupCreated ? $"  │ 백업: {op.BackupPath}" : "");
        p.FlashStatus(summary);
        if (op.Errors > 0 || restartErrors.Count > 0)
        {
            var detail = string.Join("\n", op.Failures.Concat(restartErrors));
            MessageBox.Show(this, summary + "\n\n" + detail, "백업 후 복사", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        p.FocusList();
    }
}
