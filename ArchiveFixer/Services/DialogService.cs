using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 对话框服务。
    /// 
    /// 职责：
    /// 1. 封装打开文件对话框。
    /// 2. 封装选择文件夹对话框。
    /// 3. 封装确认和消息提示。
    /// 
    /// 注意：
    /// Service 不直接依赖具体控件，只使用标准 WPF 对话框能力。
    /// </summary>
    public class DialogService
    {
        /// <summary>
        /// 打开文件选择对话框。
        /// 支持多选。
        /// </summary>
        public List<string> ShowOpenFileDialog()
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

            bool? result = dialog.ShowDialog();

            if (result == true)
            {
                return dialog.FileNames.ToList();
            }

            return new List<string>();
        }

        /// <summary>
        /// 打开单文件选择对话框。
        /// </summary>
        public string ShowOpenSingleFileDialog(
            string title = "选择文件",
            string filter = "所有文件 (*.*)|*.*")
        {
            var dialog = new OpenFileDialog
            {
                Title = title,
                Multiselect = false,
                CheckFileExists = true,
                CheckPathExists = true,
                Filter = filter
            };

            bool? result = dialog.ShowDialog();

            return result == true
                ? dialog.FileName
                : string.Empty;
        }

        /// <summary>
        /// 选择文件夹。
        /// .NET 8 WPF 可使用 Microsoft.Win32.OpenFolderDialog。
        /// </summary>
        public string ShowFolderBrowserDialog()
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择文件夹",
                Multiselect = false
            };

            bool? result = dialog.ShowDialog();

            return result == true
                ? dialog.FolderName
                : string.Empty;
        }

        /// <summary>
        /// 选择多个文件夹。
        /// </summary>
        public List<string> ShowMultiFolderBrowserDialog()
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择文件夹",
                Multiselect = true
            };

            bool? result = dialog.ShowDialog();

            if (result == true)
            {
                return dialog.FolderNames.ToList();
            }

            return new List<string>();
        }

        /// <summary>
        /// 选择保存文件路径。
        /// </summary>
        public string ShowSaveFileDialog(
            string title = "保存文件",
            string filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            string defaultFileName = "")
        {
            var dialog = new SaveFileDialog
            {
                Title = title,
                Filter = filter,
                FileName = defaultFileName,
                AddExtension = true,
                OverwritePrompt = true
            };

            bool? result = dialog.ShowDialog();

            return result == true
                ? dialog.FileName
                : string.Empty;
        }

        /// <summary>
        /// 确认对话框。
        /// </summary>
        public bool ShowConfirm(string message)
        {
            MessageBoxResult result = MessageBox.Show(
                message,
                "确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            return result == MessageBoxResult.Yes;
        }

        /// <summary>
        /// 是/否/取消对话框。
        /// </summary>
        public MessageBoxResult ShowYesNoCancel(string message)
        {
            return MessageBox.Show(
                message,
                "确认",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);
        }

        /// <summary>
        /// 信息提示。
        /// </summary>
        public void ShowInfo(string message)
        {
            MessageBox.Show(
                message,
                "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        /// <summary>
        /// 警告提示。
        /// </summary>
        public void ShowWarning(string message)
        {
            MessageBox.Show(
                message,
                "警告",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        /// <summary>
        /// 错误提示。
        /// </summary>
        public void ShowError(string message)
        {
            MessageBox.Show(
                message,
                "错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        /// <summary>
        /// 异常提示。
        /// </summary>
        public void ShowException(Exception ex, string prefix = "发生异常")
        {
            string message = ex == null
                ? prefix
                : $"{prefix}：{ex.Message}";

            ShowError(message);
        }
    }
}
