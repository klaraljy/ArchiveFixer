using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using ArchiveFixer.Packing;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;


namespace ArchiveFixer.ViewModels;

/// <summary>窗口内提示条的类别（与密码列表窗口同一套写法，样式在 App.xaml 里）。</summary>
public enum PackingNoticeKind
{
    None = 0,
    Info = 1,
    Success = 2,
    Warning = 3,
    Error = 4
}

