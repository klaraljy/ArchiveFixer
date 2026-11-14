using Microsoft.Win32;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Services;

/// <summary>
/// 文件 / 文件夹选择对话框（从 <see cref="DialogService"/> 按职责拆出的协作类）。
///
/// <para>它是**薄适配**：只把公开 API 翻译成一次
/// <see cref="OpenFileDialog"/> / <see cref="OpenFolderDialog"/> / <see cref="SaveFileDialog"/>
/// 调用；线程与降级一律交给 <see cref="DialogUiThread"/>，⛔ 这里不做等待、不写日志。
/// 标题、多选、过滤串、覆盖提示全部与拆分前逐字一致（改它们等于改用户看到的对话框）。</para>
/// </summary>
internal static class FileDialogPresenter
{
    /// <summary>打开文件选择对话框（支持多选）。</summary>
    internal static List<string> ShowOpenFileDialog()
    {
        return DialogUiThread.RunFileDialog(
            () =>
            {
                var dialog = new OpenFileDialog
                {
                    Title = "选择文件",
                    Multiselect = true,
                    CheckFileExists = true,
                    CheckPathExists = true,
                    Filter =
                        "所有文件 (*.*)|*.*|" +
                        "压缩包 (*.zip;*.rar;*.7z;*.gz;*.bz2;*.xz;*.tar)|*.zip;*.rar;*.7z;*.gz;*.bz2;*.xz;*.tar|" +
                        "伪装常见文件 (*.jpg;*.png;*.pdf;*.mp4;*.txt)|*.jpg;*.jpeg;*.png;*.gif;*.pdf;*.mp4;*.txt"
                };

                return dialog.ShowDialog() == true
                    ? dialog.FileNames.ToList()
                    : new List<string>();
            },
            "ShowOpenFileDialog",
            new List<string>());
    }

    /// <summary>打开单文件选择对话框（无界面宿主返回空串）。</summary>
    internal static string ShowOpenSingleFileDialog(string title, string filter)
    {
        return DialogUiThread.RunFileDialog(
            () =>
            {
                var dialog = new OpenFileDialog
                {
                    Title = title,
                    Multiselect = false,
                    CheckFileExists = true,
                    CheckPathExists = true,
                    Filter = filter
                };

                return dialog.ShowDialog() == true ? dialog.FileName : string.Empty;
            },
            "ShowOpenSingleFileDialog",
            string.Empty);
    }

    /// <summary>
    /// 带标题 / 初始目录的那一版（⑤打包页「选择…」用：标题要说清是"最终压缩包放在哪"）。
    ///
    /// <para>⚠ 两版**必须是同一条路**（①页与⑤页各调一版）：无界面宿主下都返回空串，
    /// 测试只要 override 带参数那一版就能钉住两条"选择…"的真实路径。</para>
    /// </summary>
    internal static string ShowFolderBrowserDialog(string title, string initialDirectory)
    {
        return DialogUiThread.RunFileDialog(
            () =>
            {
                var dialog = new OpenFolderDialog
                {
                    Title = string.IsNullOrWhiteSpace(title) ? "选择文件夹" : title,
                    Multiselect = false
                };

                if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
                {
                    dialog.InitialDirectory = initialDirectory;
                }

                return dialog.ShowDialog() == true ? dialog.FolderName : string.Empty;
            },
            "ShowFolderBrowserDialog",
            string.Empty);
    }

    /// <summary>选择多个文件夹（无界面宿主返回空列表）。</summary>
    internal static List<string> ShowMultiFolderBrowserDialog()
    {
        return DialogUiThread.RunFileDialog(
            () =>
            {
                var dialog = new OpenFolderDialog
                {
                    Title = "选择文件夹",
                    Multiselect = true
                };

                return dialog.ShowDialog() == true
                    ? dialog.FolderNames.ToList()
                    : new List<string>();
            },
            "ShowMultiFolderBrowserDialog",
            new List<string>());
    }

    /// <summary>
    /// 选择保存文件路径（<paramref name="initialDirectory"/> 为空 = 系统默认的"上次用过的那个文件夹"）。
    /// </summary>
    internal static string ShowSaveFileDialog(
        string title,
        string filter,
        string defaultFileName,
        string? initialDirectory)
    {
        return DialogUiThread.RunFileDialog(
            () =>
            {
                var dialog = new SaveFileDialog
                {
                    Title = title,
                    Filter = filter,
                    FileName = defaultFileName,
                    AddExtension = true,
                    OverwritePrompt = true
                };

                if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
                {
                    dialog.InitialDirectory = initialDirectory;
                }

                return dialog.ShowDialog() == true ? dialog.FileName : string.Empty;
            },
            "ShowSaveFileDialog",
            string.Empty);
    }
}
