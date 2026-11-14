namespace ArchiveFixer.Packing;

/// <summary>打包走到哪一步了（界面上的"当前步骤"就用它）。</summary>
public enum PackingStep
{
    /// <summary>还没开始 / 空闲。</summary>
    Idle = 0,

    /// <summary>准备（校验落点、算空间）。</summary>
    Preparing = 1,

    /// <summary>第一步：7z 加密分卷。</summary>
    Volumes = 2,

    /// <summary>第二步：外层容器（rar 或 7z）。</summary>
    OuterContainer = 3,

    /// <summary>第三步：校验产物。</summary>
    Verifying = 4,

    /// <summary>结束。</summary>
    Finished = 5
}
