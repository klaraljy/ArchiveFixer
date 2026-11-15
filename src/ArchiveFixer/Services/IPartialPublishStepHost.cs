using ArchiveFixer.BindingModels;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;

namespace ArchiveFixer.Services;

/// <summary>部分完成发布这一步要问宿主的事（设置、空间不足档、引擎、日志）。</summary>
internal interface IPartialPublishStepHost
{
    AppSettings? Settings { get; }

    /// <summary>本批是不是"空间不足"档（那一档与部分完成发布硬互斥）。</summary>
    bool SpaceTightThisBatch { get; }

    /// <summary>批首 SpaceTightPolicy；LastProcessOnFailure 等都由它给出。</summary>
    SpaceTightPolicy SpaceTightPolicy { get; }

    IArchiveEngine Engine { get; }

    void AppendLog(string level, string message);

    /// <summary>本批的其余物处理档（部分完成发布时用来决定"其余物要不要一并处理"）。</summary>
    string RestHandlingThisBatch { get; }

    /// <summary>这个路径是不是落在递归工作区里（纯路径判据，唯一实现留在协调器那侧）。</summary>
    bool IsInsideRecursionWorkspace(string? path);

    /// <summary>按档处理这一单的其余物（唯一实现：RestHandlingStep）。</summary>
    void PurgeRestItemsExceptChainRootSource(ArchiveTask task);
}
