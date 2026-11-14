namespace ArchiveFixer.Detection;

/// <summary>
/// 内容识别的**准入范围**。内容探测不是通用的“每个文件都问一遍”：
/// 只有用户点名的源包，或已经进入同基名/分卷候选池的文件才允许问。
/// </summary>
public enum ContentAdmissionScope
{
    /// <summary>普通产物/无候选上下文：⛔ 不探测。</summary>
    None = 0,

    /// <summary>用户明确导入的源包：只对源包本身做内容判断。</summary>
    UserSource = 1,

    /// <summary>已经进入同基名/分卷候选池的文件：允许浅/深探测。</summary>
    VolumeCandidate = 2
}
