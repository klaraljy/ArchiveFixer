using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Input;


namespace ArchiveFixer.ViewModels;

/// <summary>
/// 窗口内提示条的类别。决定提示条的底色/边框/文字色（样式在 App.xaml 里）。
///
/// 为什么要有它：导入结果、解析警告以前只写在窗口底部的一行小字里，
/// 那一行又和按钮同宽、会被挤掉 —— 用户"导入了却像没导入"的观感就是这么来的。
/// </summary>
public enum PasswordNoticeKind
{
    /// <summary>无提示。</summary>
    None,

    /// <summary>一般信息。</summary>
    Info,

    /// <summary>操作成功。</summary>
    Success,

    /// <summary>需要注意（解析警告、正在显示明文等）。</summary>
    Warning,

    /// <summary>失败。</summary>
    Error
}

