using System;

namespace ArchiveFixer.Models;

/// <summary>这一份构建的发行档名字（只影响「帮助 → 关于」与日志）。</summary>
internal static class BuildEdition
{
#if RELEASE_EDITION
    /// <summary>发行档的名字（进「帮助 → 关于」与日志，方便一眼看出跑的是哪一份）。</summary>
    internal const string Name = "发行版";
#else
    /// <summary>发行档的名字（进「帮助 → 关于」与日志，方便一眼看出跑的是哪一份）。</summary>
    internal const string Name = "开发版";
#endif
}
