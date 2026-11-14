using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ArchiveFixer.ViewModels.PasswordList;

/// <summary>
/// 「写回密码本」：把列表里**手动添加**、还没写回的值追加进用户自己的密码本 txt。
///
/// <para>为什么只能是"追加到用户那份 txt"：列表本身虽然按本机加密保存，但那份记忆
/// 换机器 / 重装系统就解不开、关掉「记住密码列表」也没有 —— 真正带得走、也永远不会被
/// 程序碰的载体，只有用户自己的密码本文件。⛔ 绝不写进 <c>appsettings.json</c>、
/// 也绝不写进程序自己的 <c>data\</c> 下任何文件。</para>
///
/// <para>整条流程：挑出待写条目 → 解析目标文件（没有就问一次）→ 确认框里逐条列出明文
/// （界面上给他看是应该的；<b>日志里绝不出现明文</b>，见 §8）→ 追加 + 备份 + 自检 →
/// 如实显示"写了几条 / 跳过几条 / 写到哪 / 备份在哪"。</para>
/// </summary>
internal sealed class PasswordWriteBackActions
{
    private readonly PasswordListViewModel _vm;
    private readonly PasswordListSummary _summary;
    private readonly PasswordBookRemembering _remembering;
    private readonly PasswordWriteBackLog _log;
    private readonly PasswordBookWriter _writer;

    internal PasswordWriteBackActions(
        PasswordListViewModel vm,
        PasswordListSummary summary,
        PasswordBookRemembering remembering,
        PasswordWriteBackLog log,
        PasswordBookWriter writer)
    {
        _vm = vm;
        _summary = summary;
        _remembering = remembering;
        _log = log;
        _writer = writer;
    }

    internal bool CanWriteBackPasswords() => _summary.UnwrittenManualCount > 0;

    internal void WriteBackPasswords()
    {
        try
        {
            IReadOnlyList<string> pending = _summary.CollectUnwrittenManualValues();

            if (pending.Count == 0)
            {
                _vm.Message = StatusText.PasswordWriteBackStatusNoOp;
                _vm.SetNotice(PasswordNoticeKind.Info, StatusText.PasswordWriteBackNothingToWrite);
                return;
            }

            /*
             * 2026-10-25：生产入口使用内置 passwords.txt 作为主存储。
             * 列表每一次增删/改值/上下移动已经即时写 txt；这里不再弹外部文件选择框，
             * 只做一次显式落盘确认，避免用户以为"写回"还指向外部密码本。
             */
            if (_vm.Service.UseTextStore)
            {
                PasswordListSaveResult saved = _vm.Service.SaveRememberedList();

                if (!saved.Success)
                {
                    _vm.Message = $"写入内置密码清单失败：{saved.FailureReason}";
                    _vm.SetNotice(PasswordNoticeKind.Error, _vm.Message);
                    return;
                }

                _vm.Message = "已写入程序内置密码清单（passwords.txt）。";
                _vm.SetNotice(PasswordNoticeKind.Success, _vm.Message);
                _log.Write("已写入内置密码清单 passwords.txt。");
                _vm.Service.RefreshRememberedBookValues();
                _vm.RefreshUnwrittenMarkers();
                _vm.RefreshStatistics();
                return;
            }

            string targetPath = ResolveWriteBackTargetPath();

            if (targetPath.Length == 0)
            {
                _vm.Message = StatusText.PasswordWriteBackStatusCancelled;
                return;
            }

            string confirmMessage = BuildWriteBackConfirmMessage(pending.Count, targetPath);

            bool confirmed = _vm.Dialog.ShowConfirm(
                confirmMessage,
                optionText: string.Empty,
                optionCheckedByDefault: false,
                detail: BuildWriteBackConfirmDetail(pending),
                out _);

            if (!confirmed)
            {
                // 取消则什么都不做：不备份、不打开文件、一个字节都不动。
                _vm.Message = StatusText.PasswordWriteBackStatusCancelled;
                _log.Write(StatusText.PasswordWriteBackLogCancelled);
                return;
            }

            PasswordBookWriteBackResult result = _writer.WriteBack(targetPath, pending);

            ReportWriteBackResult(result, pending);
        }
        catch (Exception ex)
        {
            // 兜底：真正会出事的地方（文件、对话框）各自都有明确的失败分支，
            // 走到这里说明是没预料到的异常。如实显示，绝不假装成功。
            string reason = ex.GetType().Name + "：" + ex.Message;

            _vm.Message = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.PasswordWriteBackFailedFormat,
                reason);

            _vm.SetNotice(PasswordNoticeKind.Error, _vm.Message);
            _vm.Dialog.ShowError(_vm.Message);
        }
    }

    /// <summary>
    /// 定下这次写哪个文件：优先设置里的 <c>PasswordBookPath</c>；
    /// 没有（或那个文件已经不在了）就问一次 —— 用既有的文件对话框服务，不另造一个。
    /// 选完沿「导入 txt」同一条路记住路径，下次启动不必再问。
    /// </summary>
    private string ResolveWriteBackTargetPath()
    {
        AppSettings settings = _remembering.LoadSettings();
        string configured = settings.PasswordBookPath ?? string.Empty;

        if (configured.Length > 0 && File.Exists(configured))
        {
            return configured;
        }

        if (configured.Length > 0)
        {
            // 路径记着、文件没了（用户搬走/改名了）。说清这件事再问他选哪个，
            // 否则他会以为自己上次根本没选成功。
            _vm.SetNotice(
                PasswordNoticeKind.Warning,
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.PasswordWriteBackTargetMissingFormat,
                    Path.GetFileName(configured)));
        }

        string chosen = _vm.Dialog.ShowOpenSingleFileDialog(
            "选择要写回的密码本文件",
            "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*");

        if (string.IsNullOrWhiteSpace(chosen))
        {
            return string.Empty;
        }

        // 与「导入 txt」同一条路：写进 appsettings.json 的 PasswordBookPath + 侧车文件。
        _remembering.RememberPasswordBookPath(chosen);

        return chosen;
    }

    /// <summary>
    /// 确认框正文：**只有数量与文件名**（正文会被无界面宿主的降级日志记下来）。
    /// 明文清单走 <see cref="BuildWriteBackConfirmDetail"/> 的 Detail 区。
    /// </summary>
    private string BuildWriteBackConfirmMessage(int count, string targetPath)
    {
        var builder = new StringBuilder();

        builder.Append(string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            StatusText.PasswordWriteBackConfirmFormat,
            count,
            Path.GetFileName(targetPath)));

        builder.AppendLine();
        builder.Append(StatusText.PasswordWriteBackConfirmNote);

        builder.AppendLine();
        builder.Append(string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            StatusText.PasswordWriteBackAttemptLimitHintFormat,
            _remembering.LoadSettings().MaxPasswordAttemptsPerLayer));

        return builder.ToString();
    }

    /// <summary>
    /// 确认框 Detail 区：**逐条列出将要写入的密码明文**（用户明确要求，界面上给他看是应该的）。
    /// 它只显示、不进日志；对话框里那块是等宽、可滚动、可复制的。
    /// </summary>
    private static string BuildWriteBackConfirmDetail(IReadOnlyList<string> pending)
    {
        var builder = new StringBuilder();

        builder.AppendLine(StatusText.PasswordWriteBackConfirmDetailHeader);

        for (int i = 0; i < pending.Count; i++)
        {
            builder.AppendLine(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.PasswordWriteBackConfirmDetailItemFormat,
                i + 1,
                pending[i]));
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// 把写回结论**如实**显示出来：写了几条 / 跳过几条 / 写到哪个文件 / 备份在哪。
    /// 日志里只有数量与文件名 —— <b>绝不出现明文密码</b>（§8 隐私红线）。
    /// </summary>
    private void ReportWriteBackResult(PasswordBookWriteBackResult result, IReadOnlyList<string> pending)
    {
        string fileName = Path.GetFileName(result.TargetPath);

        if (!result.Success)
        {
            _vm.Message = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.PasswordWriteBackFailedFormat,
                result.FailureReason);

            _vm.SetNotice(PasswordNoticeKind.Error, _vm.Message);
            _log.Write(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.PasswordWriteBackLogFailedFormat,
                result.FailureReason));

            _vm.Dialog.ShowError(_vm.Message);
            return;
        }

        if (result.AppendedCount == 0)
        {
            _vm.Message = StatusText.PasswordWriteBackStatusNoOp;

            _vm.SetNotice(
                PasswordNoticeKind.Info,
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.PasswordWriteBackNothingWrittenFormat,
                    result.SkippedCount,
                    fileName));

            _log.Write(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.PasswordWriteBackLogNoOpFormat,
                fileName,
                result.SkippedCount));

            return;
        }

        // 写回成功：目标书里现在确实有这些值 —— 重新读文件刷新值集合，标记随之消失
        //（用户一眼能看出"哪些条重启会没"；重启后再查一次，结论不变）。
        _vm.MarkAsWrittenBack(result.TargetPath, pending);

        var detail = new StringBuilder();

        detail.Append(string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            StatusText.PasswordWriteBackBackupLineFormat,
            result.BackupPath));

        string skipped = BuildSkippedSummary(result);

        if (skipped.Length > 0)
        {
            detail.Append(' ').Append(skipped);
        }

        _vm.Message = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            StatusText.PasswordWriteBackStatusFormat,
            result.AppendedCount,
            fileName);

        _vm.SetNotice(
            result.SkippedCount > 0 ? PasswordNoticeKind.Warning : PasswordNoticeKind.Success,
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.PasswordWriteBackSucceededFormat,
                result.AppendedCount,
                fileName,
                detail.ToString()));

        _log.Write(string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            StatusText.PasswordWriteBackLogFormat,
            result.AppendedCount,
            fileName));

        _vm.RefreshStatistics();
    }

    /// <summary>把"跳过了几条、为什么跳"拼成一句（逐项只在真的有数时才出现）。</summary>
    private static string BuildSkippedSummary(PasswordBookWriteBackResult result)
    {
        if (result.SkippedCount == 0)
        {
            return string.Empty;
        }

        var reasons = new List<string>();

        if (result.SkippedEmptyCount > 0)
        {
            reasons.Add(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.PasswordWriteBackSkippedEmptyText,
                result.SkippedEmptyCount));
        }

        if (result.SkippedBlankCount > 0)
        {
            reasons.Add(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.PasswordWriteBackSkippedBlankText,
                result.SkippedBlankCount));
        }

        if (result.AlreadyPresentCount > 0)
        {
            reasons.Add(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.PasswordWriteBackSkippedExistingText,
                result.AlreadyPresentCount));
        }

        if (result.SkippedDuplicateCount > 0)
        {
            reasons.Add(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.PasswordWriteBackSkippedDuplicateText,
                result.SkippedDuplicateCount));
        }

        return string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            StatusText.PasswordWriteBackSkippedFormat,
            result.SkippedCount,
            string.Join("、", reasons));
    }
}
