using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ArchiveFixer.Storage
{    /// <summary>
     /// 一次删除调用的选项。
     ///
     /// <see cref="UserConfirmed"/> 是**必填**的语义（默认 false）：没有它的调用一律被拒。
     /// 这不是"少一层校验"，而是"默认拒绝"这条规矩的落点 —— 删除不可逆，
     /// 只有调用方（界面层）明确说了"用户已确认"，本服务才动手。
     /// </summary>
    public sealed class DeleteOptions
    {
        /// <summary>允许的根目录。目标必须落在它之内，且不得等于它。</summary>
        public string AllowedRoot { get; init; } = string.Empty;

        /// <summary>调用方是否已获得用户明确确认。<b>默认 false = 一律拒绝</b>。</summary>
        public bool UserConfirmed { get; init; }

        /// <summary>删除档位，默认回收站。</summary>
        public DeleteMode Mode { get; init; } = DeleteMode.RecycleBin;

        /// <summary>本批次的删除理由（单个请求没写理由时用它），写进日志。</summary>
        public string Reason { get; init; } = string.Empty;
    }

}
