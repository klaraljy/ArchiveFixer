using System.Collections.Generic;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 递归解压的工作区：所有中间产物先落在这里，确认无误后再"发布"到最终目录。
///
/// 为什么要这么一层（设计.md §十三/§十四、AGENTS.md §6 第 12 条）：
/// ① 递归展开会解出**很多**中间文件，直接往最终目录写会让用户在失败时收到一堆半成品；
/// ② 每层都在自己的目录里，失败能定位到层、能从那一层重试；
/// ③ 只有"全部完成"才发布 —— 部分完成时产物留在工作区，用户能看、能接着处理，不会被半成品污染目标目录。
///
/// 本类只保留**公开 API 的薄转发**；按职责拆开的协作类：
/// <list type="bullet">
/// <item><description><see cref="WorkspaceLayout"/>：工作区根/任务目录的唯一来源 + 崩溃恢复记录；</description></item>
/// <item><description><see cref="WorkspaceStaging"/>：层暂存树（建层、层清单、叶子/父层拓扑）；</description></item>
/// <item><description><see cref="WorkspacePublisher"/>：发布编排；<see cref="WorkspaceInPlacePublisher"/>：就地替换那一档；
/// <see cref="WorkspaceContentMover"/>：搬运细节；</description></item>
/// <item><description><see cref="WorkspaceCleanup"/>：整棵工作区删除；<see cref="WorkspaceConsumedCleanup"/>：已消费内层包/分卷组的删除闸门；</description></item>
/// <item><description><see cref="WorkspacePublishOutcome"/>：发布失败模型与结果话术；<see cref="WorkspacePathRules"/>：路径判据唯一出口。</description></item>
/// </list>
///
/// 安全边界（发布与清理会动盘上的东西，所以每一条都写死在这里，实现分别在上述协作类）：
/// 1. <c>RootDirectory</c> 为空直接抛 —— 那会把工作区建在进程当前目录，属于调用方的编程错误
///    （<see cref="WorkspaceLayout.Create"/>）；
/// 2. <c>taskId</c> 必须清洗成**单个目录名**，否则 <c>".."</c> / <c>"a\..\..\b"</c> 这类值
///    能让"工作区"落到 root 之外，后续 Cleanup 就会删掉用户的东西（<see cref="WorkspaceLayout.Create"/>）；
/// 3. <see cref="Cleanup"/> 删之前再规范化比较一次，确认确实在 root 之下，不在就**什么都不删**
///    （<see cref="WorkspaceCleanup"/>）；
/// 4. <see cref="Publish"/> 移动时同名绝不覆盖（AutoRename），移动用的 File.Move 两参数版本
///    在目标存在时也会抛，是最后一道保险（<see cref="WorkspaceContentMover"/>）。
/// </summary>
public sealed class ExtractionWorkspace
{
    /// <summary>
    /// 工作区目录名前缀。带序号且补零，是为了在资源管理器里天然按层序排好。
    ///
    /// <para><c>internal</c>：清工作区前的第二道容器内校验要认它（<see cref="WorkspaceCleanupGuard"/>），
    /// ⛔ 前缀只能在这里写一次。</para>
    /// </summary>
    internal const string LayerDirectoryPrefix = "layer-";

    private readonly WorkspaceLayout _layout;
    private readonly WorkspaceStaging _staging;
    private readonly WorkspacePublisher _publisher;

    /// <summary>创建并准备一个工作区（<c>&lt;rootDirectory&gt;\&lt;清洗后的 taskId&gt;</c> 会被建出来）。</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="rootDirectory"/> 为空。这是**调用方的编程错误**，不是用户输入错误 ——
    /// 用户输入的空目录应该在更上层就被拦住并给出可读提示，走到这里说明上层漏了校验，
    /// 此时静默继续只会把产物写到一个没人知道的地方，所以必须当场炸出来。
    /// </exception>
    public ExtractionWorkspace(string rootDirectory, string taskId)
    {
        _layout = WorkspaceLayout.Create(rootDirectory, taskId);
        _staging = new WorkspaceStaging(_layout.TaskDirectory);
        _publisher = new WorkspacePublisher(_staging, _layout.RootDirectory);
    }

    /// <summary>工作区根目录（由调用方指定，通常是某个临时目录）。</summary>
    public string RootDirectory => _layout.RootDirectory;

    /// <summary>本任务的工作区目录：<c>&lt;root&gt;\&lt;清洗后的 taskId&gt;</c>。</summary>
    public string TaskDirectory => _layout.TaskDirectory;

    /// <summary>已经创建的层，按层号升序。</summary>
    public IReadOnlyList<WorkspaceLayer> Layers => _staging.Layers;

    /// <summary>
    /// 创建下一层目录（layer-000、layer-001…）并返回。
    /// 层号取 <see cref="Layers"/>.Count，所以"层号连续、不跳号"是结构上保证的。
    /// </summary>
    public WorkspaceLayer CreateNextLayer(string inputArchivePath)
    {
        return _staging.CreateNextLayer(inputArchivePath);
    }

    /// <summary>
    /// 把最终产物"发布"到 <paramref name="targetDirectory"/>（**移动**，绝不覆盖）。
    ///
    /// 发布内容 = **所有叶子层**的产物，即"没有别的层是从它里面解出来的"那些层：
    /// 单链包只有最深那一层是叶子（所以中间层的 inner.7z 不会被搬出去），多分支包每个分支
    /// 各是一层叶子（所以 a.txt 与 b.txt 会一起落到目标目录）。
    /// 只发布叶子、不发布全部层，是为了不让"已经被展开掉的中间归档"混进最终产物 ——
    /// 用户要的是解到底的东西，不是半路的压缩包。
    ///
    /// ⛔ <b>发布这一步**一个外壳都不摊**</b>（2026-10-02 真机修）：这一层的产物**原样**搬进
    /// <paramref name="targetDirectory"/>。理由有两条，缺一不可：
    /// ① "哪一层留、那一层叫什么"的**唯一出口**是 <see cref="ResultFinalizer"/>（判定表 ①③④ 与
    /// "同名不套层"都在那里；套娃目录 <c>pack\pack\</c> 由它自己的同名那一档收掉）；
    /// 两处各摊一次 = 同一个事实两个判据，真机上表现为"用户的文件夹被吃过一层"，而且没有日志。
    /// ② 用户 2026-09-30 的红线原话："如果是一个文件夹 <c>1111</c> 里面包裹真正的内容物，
    /// 这个时候你就会把 <c>1111</c> 省略，这是非常大忌" —— 现场就是
    /// <c>风景01.7z.001</c>（只解一层、内容全在一个 <c>风景\</c> 文件夹里）被摊成了
    /// <c>&lt;包名&gt;\1.mp4</c>。
    ///
    /// <para>
    /// ⛔ <b>这一次递归展开了内层包时走"就地替换"那一档</b>（<paramref name="inPlaceInnerPackages"/>
    /// 传 true，用户 2026-09-30 中午的规则）：被解开的内层包在**它原来的位置**留下一个
    /// **以它命名的文件夹**（去掉假后缀的基名）放内容物，真文件原地不动 ——
    /// 形状 <c>AAA\DDDD\内容物</c>、<c>AAA\BBBB\CCCCC\内容物</c>。
    /// ⛔ 老口径"把所有叶子层产物摊到发布目标根上"（等于把它们都搬到顶层）**只有在这一档之外**
    /// 才是对的；两档现在都不摊"外壳" —— 那些文件夹是打包人自己的结构，见 <see cref="PackageLayerRules"/>。
    /// </para>
    ///
    /// <paramref name="targetDirectory"/> 必须由调用方给出**完整目标位置**
    /// （本方法不会替它再拼一层包名）：递归模式下"层产物"与"统一目标目录"的关系
    /// 由调用方决定，工作区不替用户拍板。
    /// </summary>
    /// <param name="inPlaceInnerPackages">
    /// 这一次递归展开了内层包没有（默认 false = 与加"最少两层 / 就地替换"那两条规则之前**逐字相同**）。
    /// 判据**不在本类**：由调用方从 <see cref="PackageLayerRules"/> 那**一个**出口读，本类只认这一个布尔。
    /// </param>
    /// <param name="intakeStaging">
    /// 这次发布的目标**只是"入仓目录"**（中间产物暂存处，定稿时才搬进落点），不是用户看得见的落点。
    ///
    /// <para>2026-10-07 真机 EEEE：递归跑在任务里时目标 = <c>&lt;工作区&gt;\&lt;任务&gt;-hash\stage</c>，
    /// 而结论句把同一个路径同时印成「产物：X」与「已发布 N 个文件到 X」—— 用户读成
    /// "报了解压成功、产物却留在工作区没发布"（其实日志下一行就是"定稿完成 → 落点"）。
    /// 传 true ⇒ 那句话改写成"已入仓 N 个文件（中间产物，定稿时才搬进落点）"，⛔ 数字与
    /// 改名/失败那几句一个字不动。</para>
    ///
    /// <para>判据**不在本类**：由调用方按"目标在不在工作区根里面"回答，本类只认这一个布尔
    /// （与 <paramref name="inPlaceInnerPackages"/> 同一条纪律）。</para>
    /// </param>
    public WorkspacePublishResult Publish(
        string targetDirectory,
        bool inPlaceInnerPackages = false,
        IReadOnlyCollection<string>? alreadyReclaimedPaths = null,
        bool intakeStaging = false)
    {
        return _publisher.Publish(
            targetDirectory,
            inPlaceInnerPackages,
            alreadyReclaimedPaths,
            intakeStaging);
    }

    /// <summary>
    /// 删除工作区，并如实回答"删了没有、为什么"。
    ///
    /// 只在调用方**确认后**调用（AGENTS.md §6 第 13 条：清工作区必须先经用户确认）。
    /// 对"本次任务自己造出来的中间产物"来说，那个确认点就是"任务已成功、产物已经发布出去"；
    /// 失败 / 取消 / 部分完成时**不许**调用（此时工作区里的东西是用户唯一的线索）。
    ///
    /// 三道防线：
    /// ① 删之前用规范化的完整路径确认 <see cref="TaskDirectory"/> 确实位于 <see cref="RootDirectory"/> 之下
    ///    （相等也不行 —— 那等于把整个 root 端掉），不满足就**什么都不删**；
    /// ② 目录不存在时直接返回，不去"顺手"删父目录；
    /// ③ 删非空目录用递归删除（工作区本来就是自己造的，里面全是本任务的中间产物）；
    ///    删不掉（占用 / 权限）只如实报告，**不抛异常** —— 清工作区失败不该让已经成功的任务变成失败。
    ///
    /// 删除失败时**不清空** <see cref="Layers"/>：那些层目录还在盘上，层清单必须继续如实描述现状。
    /// </summary>
    public WorkspaceCleanupResult Cleanup()
    {
        WorkspaceCleanupResult result = WorkspaceCleanup.Remove(
            _layout.RootDirectory,
            TaskDirectory,
            out bool layersInvalidated);

        if (layersInvalidated)
        {
            _staging.ForgetLayers();
        }

        return result;
    }

    /// <summary>
    /// 写 report.json（给"崩溃后可恢复"用，字段自己定，但必须是可读的 JSON）。
    ///
    /// 两条硬约束：
    /// ① **绝不写密码**（AGENTS.md §6 第 5 条）—— 报告只记结构和数量，调用方传进来的对象里
    ///    也不该有明文密码，本方法不做任何"帮你去掉密码"的补救；
    /// ② 写不进去（磁盘满 / 权限不足 / 序列化失败）**吞掉异常**：报告是给恢复用的辅助品，
    ///    写不出来不该让整个已经跑完的任务失败。
    /// </summary>
    public void WriteReport(object report)
    {
        _layout.WriteReport(report);
    }

    /// <summary>
    /// 这一份是不是**跟被消费的那一份同目录、同族、同基名**的分卷（组的成员判据）。
    ///
    /// <para>判据本体在 <see cref="WorkspaceConsumedCleanup"/>（那才是唯一出口）；本方法保留在
    /// <see cref="ExtractionWorkspace"/> 上只是**薄转发**，让既有引用点与
    /// <c>VolumeGroupRulerParityTests</c> 继续指向同一把尺子：⛔ 用例里不许复制一份判据，
    /// ⛔ 判据本身也不许出现第二份。为什么要 <c>internal</c>：那个用例要把这一把尺子与
    /// 递归折叠那一把（<c>RecursiveExtractor.IsSameGroupContinuationVolume</c>）放在**同一份名字语料**上
    /// 逐格比对；所以只放宽可见性，判据本体一个字没动。</para>
    /// </summary>
    internal static bool IsSameGroupMemberInSameDirectory(
        string candidatePath,
        string consumedSourceFullPath,
        string consumedSourceName)
    {
        return WorkspaceConsumedCleanup.IsSameGroupMemberInSameDirectory(
            candidatePath,
            consumedSourceFullPath,
            consumedSourceName);
    }
}
