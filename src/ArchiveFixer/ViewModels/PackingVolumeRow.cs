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


namespace ArchiveFixer.ViewModels
{
    /// <summary>结果区分卷表格的一行。</summary>
    public sealed class PackingVolumeRow
    {
        public string FileName { get; init; } = string.Empty;

        public string SizeText { get; init; } = string.Empty;
    }
}

