using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.IO;
using System.Linq;

namespace ArchiveFixer.ViewModels.PasswordList;

/// <summary>
/// 列表的导入 / 导出 / 显式保存：
/// 导入 txt 密码本、导出明文副本、把当前列表显式落盘（内置 passwords.txt）。
///
/// <para>「写回用户自己的密码本」是另一条路，见 <see cref="PasswordWriteBackActions"/>；
/// 这里只在"非文本内存储"时把显式保存交给它，保持导入与写回各说各的话。</para>
/// </summary>
internal sealed class PasswordListImportExport
{
    private readonly PasswordListViewModel _vm;
    private readonly PasswordBookRemembering _remembering;
    private readonly PasswordWriteBackLog _log;
    private readonly PasswordWriteBackActions _writeBack;

    internal PasswordListImportExport(
        PasswordListViewModel vm,
        PasswordBookRemembering remembering,
        PasswordWriteBackLog log,
        PasswordWriteBackActions writeBack)
    {
        _vm = vm;
        _remembering = remembering;
        _log = log;
        _writeBack = writeBack;
    }

    internal void ImportPasswords()
    {
        try
        {
            string path = _vm.Dialog.ShowOpenSingleFileDialog(
                "导入密码列表",
                "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*");

            if (string.IsNullOrWhiteSpace(path))
            {
                _vm.Message = "已取消导入。";
                return;
            }

            string sourcePath = _vm.Service.UseTextStore
                ? path
                : PasswordService.CopyImportedBookToInstallDirectory(path);

            var imported = _vm.Service.ImportPasswordList(sourcePath);

            _vm.ReloadFromService();

            if (!_vm.Service.UseTextStore)
            {
                _remembering.RememberPasswordBookPath(sourcePath);
            }

            // 与主界面「工具 → 导入密码本…」同一套文案：条数 + 已记住 + 下次启动自动加载。
            // 以前这里只有"已导入 N 个新密码"，用户看不出到底记住没有，
            // 而且只写在底部一行小字里 —— 现在改成窗口内的提示条，不容易被忽略。
            int warningCount = _vm.Service.LastImportWarnings.Count;

            string summary = _vm.Service.UseTextStore
                ? $"已从「{Path.GetFileName(path)}」导入 {imported.Count} 条新密码，"
                  + $"当前共 {_vm.TotalCount} 条（启用 {_vm.EnabledCount} 条）。"
                  + "已合并进程序内置 passwords.txt；外部文件以后改了也不会再链接。"
                : $"已从「{Path.GetFileName(path)}」导入 {imported.Count} 条新密码，"
                  + $"当前共 {_vm.TotalCount} 条（启用 {_vm.EnabledCount} 条）。"
                  + "已记住这个文件，下次启动会自动加载。";

            if (warningCount > 0)
            {
                summary += Environment.NewLine + "解析警告（这些行被跳过，密码本身没有丢）："
                    + Environment.NewLine + "· " + string.Join(Environment.NewLine + "· ", _vm.Service.LastImportWarnings);

                _vm.Message = $"已导入 {imported.Count} 条密码，有 {warningCount} 条解析警告。";
                _vm.SetNotice(PasswordNoticeKind.Warning, summary);
            }
            else
            {
                _vm.Message = _vm.Service.UseTextStore
                    ? $"已导入 {imported.Count} 条密码，并合并进内置 passwords.txt。"
                    : $"已导入 {imported.Count} 条密码，并记住了这个文件，下次启动会自动加载。";
                _vm.SetNotice(PasswordNoticeKind.Success, summary);
            }
        }
        catch (Exception ex)
        {
            _vm.Message = "导入密码失败：" + ex.Message;
            _vm.SetNotice(PasswordNoticeKind.Error, "导入密码失败：" + ex.Message);
            _vm.Dialog.ShowError("导入密码失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 显式导出完整明文密码本。只写用户选定的副本，程序内部密码本原样保留。
    /// </summary>
    internal void ExportPlaintextPasswords()
    {
        try
        {
            string path = _vm.Dialog.ShowSaveFileDialog(
                "导出明文密码本",
                "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                "passwords.txt");

            if (string.IsNullOrWhiteSpace(path))
            {
                _vm.Message = "已取消导出。";
                return;
            }

            if (!PasswordTextStore.TryExportPlaintext(
                    path,
                    _vm.Passwords.Select(item => item.Value ?? string.Empty),
                    out string exportError))
            {
                throw new IOException(exportError);
            }

            _vm.Message = "已导出明文密码本副本；程序内部密码本没有移动、没有删除。";
            _vm.SetNotice(
                PasswordNoticeKind.Warning,
                "这是一份明文密码副本，任何能打开它的人都能看到密码。"
                + Environment.NewLine
                + "程序内部密码本仍在原位置，后续修改请继续用「保存密码列表」。");
        }
        catch (Exception ex)
        {
            _vm.Message = "导出明文密码本失败：" + ex.Message;
            _vm.SetNotice(PasswordNoticeKind.Error, "导出明文密码本失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 显式保存当前列表（生产入口 = 内置 passwords.txt）。
    /// 这一档不要求存在"未写回的手工条目"，也不看「记住密码列表」开关；
    /// 用户点一次就一定落盘，失败时如实报。
    /// </summary>
    internal void SavePasswordList()
    {
        if (_vm.Passwords.Count == 0)
        {
            _vm.Message = StatusText.PasswordWriteBackStatusNoOp;
            _vm.SetNotice(PasswordNoticeKind.Info, StatusText.PasswordWriteBackNothingToWrite);
            return;
        }

        if (!_vm.Service.UseTextStore)
        {
            _writeBack.WriteBackPasswords();
            return;
        }

        try
        {
            if (!_vm.Service.TextStore.TrySave(
                    _vm.Passwords.Select(item => item.Value),
                    out string failure))
            {
                _vm.Message = "保存内置密码清单失败：" + failure;
                _vm.SetNotice(PasswordNoticeKind.Error, _vm.Message);
                _log.Write(_vm.Message);
                return;
            }

            _vm.Message = "已保存到程序内置密码清单（passwords.txt）。";
            _vm.SetNotice(PasswordNoticeKind.Success, _vm.Message);
            _log.Write("已保存到程序内置密码清单 passwords.txt。");

            _vm.Service.RefreshRememberedBookValues();
            _vm.RefreshUnwrittenMarkers();
            _vm.RefreshStatistics();
        }
        catch (Exception ex)
        {
            _vm.Message = "保存内置密码清单失败：" + ex.Message;
            _vm.SetNotice(PasswordNoticeKind.Error, _vm.Message);
            _log.Write(_vm.Message);
        }
    }
}
