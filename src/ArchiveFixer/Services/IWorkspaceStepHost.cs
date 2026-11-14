using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using System;

namespace ArchiveFixer.Services
{
    /// <summary>工作区这一步要问宿主的事（只用非 WPF 类型 —— Extraction 层不许引用 ViewModels）。</summary>
    internal interface IWorkspaceStepHost
    {
        PathService PathService { get; }

        AppSettings? Settings { get; }

        OneClickRunOptions? RunOptions { get; }

        ExtractOptions BuildExtractOptions(bool tryExtractUnknownFormat);

        bool IsUnresolvedVolumeGroupMember(ArchiveTask task);

        /// <summary>"这个名字是不是我们自己造的工作区子目录"（判据唯一出口在协调器那一个静态函数）。</summary>
        Func<string, bool> IsOurWorkspaceSubdirectory { get; }

        void AppendLog(string level, string message);

        /// <summary>把这一单的递归器从账本上摘下来（摘不到 ⇒ null）。</summary>
        RecursiveExtractor? TakeTaskRecursiveExtractor(ArchiveTask task);
    }
}
