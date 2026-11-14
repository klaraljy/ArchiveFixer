using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;


namespace ArchiveFixer.Services
{
    /// <summary>
    /// 对话框按钮组合。
    /// </summary>
    public enum AppDialogButtons
    {
        /// <summary>只有"确定"。</summary>
        Ok,

        /// <summary>"确定 / 取消"。</summary>
        OkCancel,

        /// <summary>"是 / 否"。</summary>
        YesNo,

        /// <summary>"是 / 否 / 取消"。</summary>
        YesNoCancel
    }
}

