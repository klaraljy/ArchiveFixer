using ArchiveFixer.BindingModels;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 改名预览窗口的宿主注入点。实现在 Views；ViewModel 只拿“确认后的 ViewModel 或 null”。
/// </summary>
internal static class RenamePreviewWindowFactory
{
    public static Func<IEnumerable<RenamePreviewItem>, RenamePreviewViewModel?>? Show { get; set; }
}
