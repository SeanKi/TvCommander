using System.Windows;
using MPCommander.IO;
using MPCommander.Model;
using MPCommander.Native;

namespace MPCommander.Views;

/// <summary>백업 후 복사 (Backup-Copy) 와 복원: 프로그램 업데이트처럼 대상의 옛 파일을 백업해 두고 새 파일로 바꾸기, 그리고 되돌리기</summary>
public partial class MainWindow
{
    /// <summary>
    /// Ctrl+B / 툴바: 원본 패널에서 표시한 항목(없으면 커서 항목)을 대상 패널의 '폴더'로 백업 후 복사.
    ///  1) 대상 파일을 쓰는 프로그램 확인 → 닫기 안내  2) 덮어쓸 파일을 backup\yyyyMMdd_HHmm 에 백업
    ///  3) 복사 (사용 중이면 닫기/이름 바꿔 교체 등 선택)  4) 닫았던 프로그램 다시 실행 (옵션)
    /// 원본·대상: 보통 활성 패널 → 다른 패널. 활성 패널에는 표시한 것이 없고 다른 패널에만 있으면 그쪽이 원본.
    /// 대상은 늘 대상 패널의 현재 폴더 (그 패널의 커서 파일·폴더가 아니다). 준비 창의 [방향 바꾸기] 로 뒤집을 수 있다.
    /// </summary>
    public async void CmdBackupCopy()
    {
        var src = P;
        var dst = _target;
        if (dst != null && dst != src && !src.HasMarks && dst.HasMarks)
        {
            (src, dst) = (dst, src);   // 표시한 항목이 있는 패널이 원본
        }
        try { await BackupCopyAsync(src, dst); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async Task BackupCopyAsync(FilePanel src, FilePanel? dst)
    {
        var items = src.GetSelectedOrCurrent();
        if (src.CurrentPath == null) return;
        if (items.Count == 0)
        {
            src.FlashStatus(Loc.T("백업 복사할 파일·폴더를 고르세요 (원본 패널에서 표시하거나 커서를 두세요)."));
            return;
        }
        if (PathUtil.IsVirtual(src.CurrentPath)) { src.FlashStatus(Loc.T("백업 복사는 PC 의 파일만 됩니다. 원격 파일은 먼저 PC 로 복사하세요.")); return; }

        var targetDir = dst?.CurrentPath;
        if (dst == null || targetDir == null || dst == src || PathUtil.IsVirtual(targetDir))
        {
            src.FlashStatus(Loc.T("백업 복사는 대상 패널(PC 폴더)로 복사합니다. 대상 패널을 PC 폴더로 여세요."));
            return;
        }
        if (PathUtil.Same(targetDir, src.CurrentPath)) { src.FlashStatus(Loc.T("원본과 대상 폴더가 같습니다.")); return; }
        // 대상 폴더 안의 것을 그 폴더로 복사하는 실수 (예: 대상 패널이 원본의 하위 폴더)
        if (items.Any(i => i.IsDirectory && (PathUtil.Same(i.FullPath, targetDir) || PathUtil.IsUnder(targetDir, i.FullPath))))
        {
            src.FlashStatus(Loc.T("대상 폴더가 원본 폴더 안에 있습니다."));
            return;
        }

        var sources = items.Select(i => i.FullPath).ToList();
        foreach (var path in new[] { src.CurrentPath, targetDir })
        {
            var (ok, host) = await NetworkHealth.CheckPathAsync(path);
            if (!ok) { ShowError(new HostUnreachableException(host!).Message); return; }
        }

        // 계획: 원본을 훑고 대상에 같은 이름이 있는지 확인 (느린 네트워크에서 멈추지 않게 시간 제한)
        BackupCopyPlan plan;
        try
        {
            src.FlashStatus(Loc.T("백업 복사 준비 중..."));
            plan = await GuardedIo.RunAsync(ctx => BackupCopyPlan.Build(sources, targetDir, ctx), 10000, targetDir);
        }
        catch (Exception ex) { ShowError(ex.Message); return; }
        if (plan.Items.Count == 0) { src.FlashStatus(Loc.T("복사할 파일이 없습니다 (빈 폴더).")); return; }

        string what = items.Count == 1 ? $"'{items[0].Name}'" : Loc.F($"{items.Count}개 항목");
        var dlg = BackupCopyDialog.Show(this, _settings, plan, $"{what}  ({src.CurrentPath})");
        if (dlg == null) { src.FocusList(); return; }
        if (dlg.SwapRequested)
        {
            // 방향 바꾸기: 다른 패널의 항목을 이 패널 폴더로
            if (dst.GetSelectedOrCurrent().Count == 0)
            {
                dst.FlashStatus(Loc.T("방향을 바꾸려면 이 패널에서 복사할 파일·폴더를 표시하거나 커서를 두세요."));
                return;
            }
            ActivatePanel(dst);
            await BackupCopyAsync(dst, src);
            return;
        }
        SaveSettings();

        var op = await RunBackupCopyAsync(src, plan, dlg);
        if (!op.IsCancelled) src.ClearMarks();
    }

    /// <summary>
    /// 복원 (Ctrl+Shift+B / 툴바): backup 아래의 날짜 폴더(backup-copy.log 가 있는)를 고르면 그 기록을 보고 대상 폴더를 백업 전으로 되돌린다.
    /// 백업해 둔 파일은 제자리로, 그때 새로 생긴 파일은 치운다. 지금 대상에 있는 파일(덮어쓰거나 치우는 것)은 backup\last_one 으로 옮겨 둔다.
    /// </summary>
    public async void CmdRestore()
    {
        var p = P;
        if (p.CurrentPath == null || PathUtil.IsVirtual(p.CurrentPath)) { p.FlashStatus(Loc.T("복원할 백업 폴더(PC)를 고르세요.")); return; }

        // 커서(또는 표시한 하나)의 폴더, 아니면 지금 폴더가 백업 폴더인지
        var picked = p.GetSelectedOrCurrent();
        string? backupDir = null;
        if (picked.Count == 1 && picked[0].IsDirectory && BackupCopyPlan.IsBackupFolder(picked[0].FullPath)) backupDir = picked[0].FullPath;
        else if (BackupCopyPlan.IsBackupFolder(p.CurrentPath)) backupDir = p.CurrentPath;
        if (backupDir == null)
        {
            p.FlashStatus(Loc.T("복원: backup 아래의 날짜 폴더(backup-copy.log 가 있는 폴더)에 커서를 두고 누르세요."));
            return;
        }

        var (ok, host) = await NetworkHealth.CheckPathAsync(backupDir);
        if (!ok) { ShowError(new HostUnreachableException(host!).Message); return; }

        BackupCopyPlan plan;
        try
        {
            p.FlashStatus(Loc.T("복원 준비 중..."));
            plan = await GuardedIo.RunAsync(ctx => BackupCopyPlan.BuildRestore(backupDir, ctx), 10000, backupDir);
        }
        catch (Exception ex) { ShowError(Loc.T("복원할 수 없습니다: ") + ex.Message); return; }
        if (plan.Items.Count == 0 && plan.Removals.Count == 0) { p.FlashStatus(Loc.T("되돌릴 것이 없습니다.")); return; }

        // 지금 파일 보관: 백업 폴더들 옆의 last_one (last_one 에서 복원할 때는 시각을 붙인 새 폴더)
        var root = PathUtil.Parent(PathUtil.TrimEnd(backupDir)) ?? backupDir;
        var keep = Path.Combine(root, "last_one");
        if (PathUtil.Same(keep, backupDir)) keep = Path.Combine(root, $"last_one_{DateTime.Now:yyyyMMdd_HHmmss}");

        var dlg = BackupCopyDialog.ShowRestore(this, _settings, plan, keep);
        if (dlg == null) { p.FocusList(); return; }
        SaveSettings();

        // 이전 last_one 은 휴지통으로 (지금 것으로 바꾼다)
        if (Directory.Exists(keep))
        {
            int rc = await Task.Run(() => RecycleFolder(keep));
            if (rc != 0 || Directory.Exists(keep))
            {
                ShowError(Loc.F($"이전 보관 폴더를 휴지통으로 옮기지 못했습니다: {keep}"));
                return;
            }
        }

        await RunBackupCopyAsync(p, plan, dlg);
    }

    private static unsafe int RecycleFolder(string path)
    {
        var list = path + "\0\0";
        fixed (char* ptr = list) return NativeMethods.FmShellDelete(ptr, 0, IntPtr.Zero);
    }

    /// <summary>백업 복사·복원 공통: 진행 창으로 실행하고, 닫았던 프로그램을 다시 실행하고, 결과를 알린다</summary>
    private async Task<BackupCopyOperation> RunBackupCopyAsync(FilePanel p, BackupCopyPlan plan, BackupCopyDialog dlg)
    {
        var op = new BackupCopyOperation(plan, dlg.BackupPath);
        await RunWithProgressAsync(op);

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

        await RefreshPanelsShowingAsync(plan.TargetDirectory, op.BackupCreated ? PathUtil.Parent(op.BackupPath) : null);

        bool restore = plan.RestoreFrom != null;
        var summary = (restore
                          ? Loc.F($"복원: 되돌림 {op.Copied:N0}개, 치움 {op.Removed:N0}개, 보관 {op.BackedUp:N0}개")
                          : Loc.F($"백업 복사: 복사 {op.Copied:N0}개, 백업 {op.BackedUp:N0}개")) +
                      (op.Renamed > 0 ? Loc.F($", 이름 바꿔 교체 {op.Renamed}개") : "") +
                      (op.Errors > 0 ? Loc.F($", 실패 {op.Errors}개") : "") +
                      (restarted > 0 ? Loc.F($", 다시 실행 {restarted}개") : "") +
                      (op.IsCancelled ? Loc.T(" — 중간에 취소됨") : "") +
                      (op.BackupCreated ? (restore ? Loc.F($"  │ 지금 파일 보관: {op.BackupPath}") : Loc.F($"  │ 백업: {op.BackupPath}")) : "");
        p.FlashStatus(summary);
        if (op.Errors > 0 || restartErrors.Count > 0)
        {
            var detail = string.Join("\n", op.Failures.Concat(restartErrors));
            MessageBox.Show(this, summary + "\n\n" + detail, restore ? Loc.T("복원") : Loc.T("백업 후 복사"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        p.FocusList();
        return op;
    }
}
