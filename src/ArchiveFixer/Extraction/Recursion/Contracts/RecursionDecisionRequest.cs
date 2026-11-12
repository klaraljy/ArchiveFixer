using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Extraction
{    /// <summary>
     /// 需要用户拍板的多分支询问。字段是给 GUI 直接用的：<see cref="Prompt"/> 就是弹窗正文。
     /// </summary>
    public sealed class RecursionDecisionRequest
    {
        public string CurrentArchivePath { get; init; } = string.Empty;

        public int Depth { get; init; }

        public IReadOnlyList<string> CandidateArchives { get; init; } = Array.Empty<string>();

        /// <summary>中文，直接弹给用户。</summary>
        public string Prompt { get; init; } = string.Empty;
    }
}
