namespace ArchiveFixer.Models;

/// <summary>内容组的最终内容结论；⛔ 任务级“解压成功”不能替代它。</summary>
public enum FinalContentState
{
    Unknown = 0,

    /// <summary>有产物但证据只到非空/未逐条核对，不能当最终成功。</summary>
    Partial = 1,

    /// <summary>产物校验通过且逐条核对过，可当最终成功。</summary>
    Verified = 2,

    /// <summary>没有最终内容。</summary>
    Missing = 3
}
