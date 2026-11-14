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

namespace ArchiveFixer.Extraction;


/// <summary>
/// 递归解压核心（M4）。
///
/// 定位与边界：
/// 1. 引擎、探测器、密码来源**全部从构造函数注入** —— 递归核心不许自己 new 引擎、
///    不许直接拼 7z 参数、不许认识 GUI（AGENTS.md §3.1 四条禁止项）。这样才能脱离 GUI 被测。
/// 2. 它只**解压**：不删源文件（那是 SourceCleanupService 的事）、不改名、不写最终目录
///    （中间产物一律先落工作区，完成后再发布）。
///    它唯一会删的东西是**它自己造出来的那个工作区目录**（<see cref="CurrentWorkspace"/>），
///    而且只在"解完 + 产物发布成功 + 没被取消"时删（见 <see cref="FinalizeRun"/>）；
///    失败 / 取消 / 部分完成一律保留，那些目录是用户唯一的线索。
/// 3. 密码候选由 <c>passwordProvider</c> 给，本类不碰密码本、不记明文；
///    对外只暴露 "空密码" / "******"。
/// 4. 日志由 <c>log</c> 委托注入（可空）：本类不认识 GUI，也不引用具体日志实现 ——
///    与引擎 / 探测器 / 密码来源全部注入同一个理由（AGENTS.md §4 分层铁律）。
///    清理工作区是**不可逆操作**，删前删后各要一条 INFO、删失败一条 WARN（§9.5 同一要求）。
///
/// 主循环是**一趟宽度优先**的：队列里每一项 = "一个待解的归档 + 它在第几层"，
/// 解一层、探一层、再决定要不要把探测到的内层归档入队。
/// 之所以不用递归写法：层数上限、累计预算、以及"多分支要问用户"这三件事，
/// 在循环里都只在**入队前**检查一次，比散在递归调用里更好审。
/// </summary>
public sealed partial class RecursiveExtractor
{
    /// <summary>说明类文件后缀：它们和内层归档同级出现时，不构成"多分支"。</summary>
    private static readonly string[] InformationalExtensions =
    {
        ".txt", ".nfo", ".url", ".md", ".sfv", ".jpg", ".png"
    };

    /// <summary>
    /// 「该层还有哪几个内层包没展开」最多点几个名字（多出来的折成"…还有 K 个"）。
    ///
    /// <para>与「本次内容物」「定稿没能搬运的明细」同一个数字：**前 5 个足够定性**，
    /// 一层里几十个内层包时全列出来只会把摘要撑爆（用户 2026-09-25 第 44 条）。</para>
    /// </summary>
    private const int MaxUnexpandedNameLines = 5;

    /// <summary>
    /// 密码探针目录名（建在这一层产物目录**之外**，⛔ 不许混进产物）。
    ///
    /// <para><c>internal</c>：清工作区前的第二道容器内校验要认它（<see cref="WorkspaceCleanupGuard"/>），
    /// ⛔ 名字只能在这里写一次。</para>
    /// </summary>
    internal const string ProbeDirectoryName = "_密码预检";

    /// <summary>
    /// 内层"双面文件"抠出来的副本放在这一层的这个兄弟目录里（<c>layer-XXX\carved\</c>）。
    ///
    /// <para>⛔ 与 <see cref="ProbeDirectoryName"/> 同一个道理：**绝不放产物目录**
    /// （产物目录里的东西要参与结果校验与发布，一个字节都不能混进去）。</para>
    /// </summary>
    internal const string CarveDirectoryName = "carved";

    /// <summary>
    /// 空间不足模式保留「最后一层过程物」的最终根目录（协调器设成 task.OutputPath）。
    /// 非空时保留的那一份直接落在 &lt;这个目录&gt;\其余物\，活过工作区清理；
    /// 为空时退回老口径（工作区里的 kept 目录）。
    /// </summary>
    public string? RetainedArtifactDirectory { get; set; }

    /// <summary>空间不足模式在逐层回收**之前**已经保留到最终其余物的那一份（最后发现的为准）。</summary>
    public string RetainedProcessArtifactPath { get; private set; } = string.Empty;

    /// <summary>展开比检查的上限保护：只对最大若干层做（防止有人把上限配得极大时白算）。</summary>
    private const int MaxExpansionRatioChecks = 20;

    private readonly IArchiveEngine _engine;
    private readonly IArchiveProber _prober;
    private readonly Func<string, IReadOnlyList<string>> _passwordProvider;
    private readonly RecursiveLayerPlanner _layerPlanner;
    private readonly RecursionLimits _limits;

    /// <summary>
    /// 日志出口（level, message）。为 null 时只做事、不写日志（单元测试直接 new 的场合）。
    ///
    /// 用委托而不是引用具体日志实现：递归核心不认识 GUI，也不该依赖 LogService ——
    /// 与引擎 / 探测器 / 密码来源全部注入是同一个理由。调用方若是多线程宿主，
    /// 得保证这个委托本身线程安全（<c>MainViewModel.AppendLog</c> 走 BeginInvoke，是安全的）。
    /// </summary>
    private readonly Action<string, string>? _log;

    /// <summary>
    /// **先测试再解压**（<c>Settings.TestBeforeExtract</c>，用户 2026-09-27 第 37 条）：
    /// 每个密码候选先让引擎只测试、通过了才真解压。
    ///
    /// <para>用途 = 把这条**旧单层路的能力**接进递归核心（迁移成"普通任务全递归"后它整条丢了，
    /// 运行期实测 `测试调用 0 次 / 解压调用 3 次`）。默认 <c>false</c> ⇒ 行为与接入之前逐字相同；
    /// 由协调器每次任务现建实例时从设置里取。</para>
    /// </summary>
    public bool TestBeforeExtract { get; set; }

    /// <summary>
    /// 每次真正调用引擎后的观察口（result、归档路径、动作）。
    /// 由协调器接到结构化日志；⛔ 不参与任何判据，回调异常也不影响解压。
    /// </summary>
    public Action<ArchiveOperationResult, string, string>? EngineCallObserved { get; set; }

    /// <summary>
    /// 停因判定点（reason、源码文件、行号、说明）。由协调器接到 decision.stop 事件；
    /// ⛔ 不参与判据，观察者异常也不影响解压。
    /// </summary>
    public Action<RecursionStopReason, string, int, string, string>? StopReasonObserved { get; set; }

    /// <summary>本层是否已在更精确的分支里报过停因（避免循环收尾再用概括行覆盖源码位置）。</summary>
    private bool _exactStopReasonReported;

    /// <summary>
    /// **本实例自己持有的**本次（最近一次）运行的工作区。
    ///
    /// 清理永远只针对这个对象，绝不按目录名或"最新目录"去扫 <c>data\work\recursive</c> ——
    /// 并发跑两个递归任务时，那种扫描式删除会把对方正在写的工作区端掉。
    /// 真正被删的是**本次运行**建出来的那一个（<see cref="ExtractAsync"/> 里的局部变量，
    /// 见 <see cref="FinalizeRun"/>），这个属性只是把同一个对象暴露出来给诊断 / 测试 /
    /// "结论被调用方改写"的那条路（<see cref="TryCleanupCurrentWorkspace"/>）用。
    ///
    /// ⚠ 同一个实例**不适合并发跑两次 ExtractAsync**：这个属性会被后一次覆盖。
    /// 协调器是"一个任务一个实例"（<c>ExtractionCoordinator.CreateRecursiveExtractor</c>），
    /// 并发任务各有各的实例，所以互不影响。
    /// </summary>
    public ExtractionWorkspace? CurrentWorkspace { get; private set; }

    /// <summary>
    /// **同目录里若有"同一个物理文件"的规范卷名那一份，就把交给引擎的路径换成它**
    /// （用户 2026-10-16 口径的落点：引擎按"打开的那一份的名字"推兄弟卷名 ⇒ 拿到伪装名必然找不到兄弟卷；
    /// 而规范名那一份就在同目录、同一个 File ID）。真机现场 `21212121` / `24242424` 逐字：
    /// 入口那一份自己叫 `aaa-L2.part0788839中文1.rar` / `111-L2.part020745382.rar`。
    ///
    /// <para>判据全是事实：① 卡上的 `CanonicalVolumeName` 非空且与当前文件名**不同**；
    /// ② 同目录里存在这个名字；③ `FileIdentity.IsSamePhysicalFile` 判**同一份**。
    /// ⛔ 任何一条不成立 / 抛异常 ⇒ **原样返回**（一个字节都不改，老口径）。</para>
    ///
    /// <para>⛔ 只影响"交给引擎的那一份"：任务账（`task.CurrentPath`）与源包档**一个字都不动** ——
    /// 否则「源包彻底删除」会删错名字（删掉那个链接、用户那份伪装名反而留着）。</para>
    /// </summary>
    internal static string PreferCanonicalSiblingName(string archivePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            {
                return archivePath;
            }

            FileNatureCard card = FileNature.Inspect(archivePath);
            string canonical = card.CanonicalVolumeName;

            if (canonical.Length == 0
                || string.Equals(canonical, Path.GetFileName(archivePath), StringComparison.OrdinalIgnoreCase))
            {
                return archivePath;
            }

            string? directory = Path.GetDirectoryName(archivePath);

            if (string.IsNullOrEmpty(directory))
            {
                return archivePath;
            }

            string candidate = Path.Combine(directory, canonical);

            return File.Exists(candidate) && FileIdentity.IsSamePhysicalFile(archivePath, candidate)
                ? candidate
                : archivePath;
        }
        catch
        {
            return archivePath;
        }
    }

    /// <summary>
    /// **「内容物保留关键词」判据**（用户 2026-10-04 的新功能「内容物压缩文件不解压」）。
    ///
    /// <para>命中关键词的内层归档**不当内层归档**：不展开、也不改名（还原工序那一步也不碰它），
    /// 它就当一件普通内容物留在结果里。判据唯一出口 <see cref="ContentKeepRules"/>
    /// （包含即命中、大小写不敏感、⛔ 不做通配 / 正则）。</para>
    ///
    /// <para>由调用方按设置当场赋值（与 <see cref="RecursionLimits"/>
    /// 同一个理由：改完设置不重启也要生效）。默认 <see cref="ContentKeepRules.Empty"/> =
    /// 一个关键词都没有 = 行为与以前**逐字相同**。</para>
    /// </summary>
    public ContentKeepRules KeepRules { get; set; } = ContentKeepRules.Empty;

    /// <summary>
    /// **递归路「逐层回收」的落点**（用户 2026-10-05：「这个我不是说了要同步吗，你当我放屁呢」）。
    ///
    /// <para><b>为什么要有它</b>：AGENTS.md §11.3 早就写定「续解链每一层『定稿 + 输出校验通过 +
    /// 未取消 + 可证完整』之后当场按『删除操作』处理这一层的过程物；最外层源包只留到链尾」——
    /// 但那条实现只接在**轮次续解**那条路上（层是**任务**），而「展开所有分支」那条链是
    /// **一个任务**内由本类展开的（层是**工作区目录**、不是任务）⇒ 递归路没接，于是 4 层链的
    /// 过程物一直攒到定稿那一刻才释放（真机第六批：峰值 26.94 GiB 而结果只有 7.28 GB）。
    /// 这一档就是给递归路补的同一个落点。</para>
    ///
    /// <para><b>谁在什么时候调</b>：某一层**跑成之后、它的下一层队列排好之前**（那一刻这一层的
    /// 输入包才真的用完）—— 位置由主循环里那一段说明逐条钉住（排在失败 / 上限 / 不变量 11 三关
    /// **之后**，所以"失败 / 部分完成 / 取消 ⇒ 一个字节都不动"是由位置本身保证的）。
    /// 判据与执行体都不在本类 —— 判据（批内那个事实位 / 「半套分卷」闸门 / 完整性三态）与删除执行体
    /// 全在协调器那一个出口里（AGENTS.md §9.5），本类只回答"哪几个文件"和"这一层可证完整吗"。
    /// 回调为 null = **一个字节都不动**（默认就是 null）。</para>
    ///
    /// <para>⛔ 最外层源包永远不在 <c>GroupFiles</c> 里（第 0 层压根不建回收项）。</para>
    /// </summary>
    public Action<RecursiveLayerReclaimRequest>? LayerReclaim { get; set; }

    /// <summary>
    /// **曾经交给"逐层回收"去处理的那几个过程物**（绝对路径）—— 唯一消费点是发布那一步
    /// （<see cref="ExtractionWorkspace.Publish"/> 的 <c>alreadyReclaimedPaths</c>）。
    ///
    /// <para><b>为什么要记</b>：最后被消费掉的那个内层包，在发布那一刻**要么按这一档被当场删了、
    /// 要么被闸门拦下还在盘上**。前者没有这份账的话，发布侧会把它读成"搬运失败"并白写一条
    /// "判不出哪一份才是我们自己搬进来的那一份"的 WARN —— 那是一句**假话**，
    /// 用户会当成一次失败的搬运去找原因（AGENTS.md §9.5：删 / 写盘的判据只准读事实）。</para>
    ///
    /// <para>⛔ 它不是判据、也不生成判据：这里只是把"这一份交给回收那一档了"记下来
    /// （登记的时机是**调回调之前**，因为回调自己可能一整份都删掉）；
    /// "到底删没删"仍由发布侧读**盘上事实**回答（<c>!File.Exists</c>）。</para>
    /// </summary>
    internal HashSet<string> ReclaimedProcessArtifacts { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 这条链**最外层源包**在哪（绝对路径；每次 <see cref="ExtractAsync"/> 开始时按调用方给的任务定下来）。
    ///
    /// <para>唯一消费点是"跨盘 ZIP 末片在手、其余几片散在源目录里"那一档：它的**找卷窗口**必须从
    /// **用户导入进来的那个位置**算起（用户 2026-10-05 口径：「你要以一开始的分卷文件为准，不要以 001
    /// 为准，否则这时的第一层父文件夹就探测不到」）—— 窗口是既有出口
    /// <see cref="VolumeNameRepair.EnumerateVolumeCandidatesNearby"/> 算的（自己这一层 + 子目录递归 +
    /// 第一层父文件夹这一家），⛔ 不扫全盘、⛔ 不递归到祖父及以上。</para>
    /// </summary>
    private string _rootSourcePath = string.Empty;

    /// <summary>上面那个窗口里那批候选（懒算一次：同一条链里每一层都问同一份答案）。</summary>
    private IReadOnlyList<VolumeCandidate>? _rootSourceCandidates;

    /// <summary>
    /// **这一趟为"跨盘 ZIP 末片"建出来的临时链接**（绝对路径）。
    ///
    /// <para><b>必须删掉，否则它们会被当成内容物发布出去</b>（2026-10-05 真机当场逮到）：链接就建在
    /// 那一层的产物目录里（7-Zip 只认"入口旁边那几片"），而发布侧搬的是"这一层产物目录里的文件" ⇒
    /// 用户目录里凭空多出三个 200 MiB 的同名副本；更糟的是它们会让「半套分卷」闸门以为这一组被拆开了，
    /// 于是**逐层回收**与**链尾其余物处理**全都拦下（真机：源包留在其余物里没删、诊断写着"其余物没有处理"）。
    /// ⇒ 这一层一跑完（成功或失败）就删；<see cref="ExtractAsync"/> 收尾再兜一次。</para>
    ///
    /// <para>⛔ 删的只是**我们自己刚建的那几个名字**（硬链接：删名字不动数据），源目录里的源片一个字节不动。</para>
    /// </summary>
    private readonly List<string> _crossBoundaryLinks = new();

    /// <summary>
    /// 这一趟**被这条链借来用过的用户源片**（绝对路径）—— 用于"这一组的内容已由谁解出"的记账
    /// （真机第八批：三片外壳那一单在批末还报「分卷缺失」，可内容其实已经解出来了）。
    /// </summary>
    private readonly List<string> _consumedVolumeSources = new();

    /// <summary>
    /// 这一趟停下时**打不开的那几片**（工作区里的绝对路径）—— 跨链收卷用（2026-10-05 真机第九批 CCCC）。
    ///
    /// <para><b>为什么必须记</b>：真机上这一组跨盘 ZIP 的四片分别在四户人家里 —— `.z02`/`.z03` 在用户源目录、
    /// 第 1 片 `111.z01` 压在 `111(2)_.zip` 里、末片 `111.zip` 压在 `111.rar` 里。两条链各自把对方缺的那一片
    /// **解出来了**，可两条链自己都没走完（互相等对方）⇒ 收尾时工作区整份删掉 ⇒ 那两片在批末之前就被自己删了，
    /// "等全部跑完再判"那一站到盘上一看：缺的东西刚刚被自己扔掉。</para>
    ///
    /// <para>⛔ 这里只**记账**（路径列表）：不复制、不搬、不改名、不删 —— 怎么用由协调器决定
    /// （见 <c>ExtractionCoordinator.AdoptUnresolvedVolumePieces</c>）。</para>
    /// </summary>
    private readonly List<string> _unresolvedVolumePieces = new();

    /// <summary>
    /// **这一趟打不开的那几片，抢救到哪个目录**（调用方在跑之前给；⛔ 空 = 不抢救）。
    ///
    /// <para>真机 2026-10-06：那一片（`111.zip`）是下一轮那一组的入口包，而本类收尾会把自己的工作区
    /// 清掉 ⇒ 协调器拿到结论时它已经不在了 ⇒ 整条"入口包留在它自己那条链里"的路都断了。
    /// ⇒ 调用方把它自己的**暂存目录**传进来，本类在**记下那片的同时**复制一份过去
    /// （见 <see cref="RememberUnresolvedVolumePiece"/>）。⛔ 只复制、不改名、不删原件。</para>
    /// </summary>
    public string? UnresolvedPieceRescueDirectory { get; set; }

    /// <summary>找卷窗口里那批候选（见 <see cref="_rootSourcePath"/> 的说明；算不出来就是空池）。</summary>
    internal IReadOnlyList<VolumeCandidate> RootSourceCandidates
    {
        get
        {
            if (_rootSourceCandidates == null)
            {
                try
                {
                    _rootSourceCandidates = string.IsNullOrWhiteSpace(_rootSourcePath)
                        ? Array.Empty<VolumeCandidate>()
                        : VolumeNameRepair.EnumerateVolumeCandidatesNearby(_rootSourcePath);
                }
                catch
                {
                    // 枚举不动 = 判不出：空池 ⇒ 那一档什么都不做。
                    _rootSourceCandidates = Array.Empty<VolumeCandidate>();
                }
            }

            return _rootSourceCandidates;
        }
    }

    /// <summary>
    /// 记下"这一片是一条**还没凑齐**的分卷组里的片"（去重；只记路径，什么都不碰）。
    /// 见 <see cref="_unresolvedVolumePieces"/> 的说明。
    ///
    /// <para>⚠ <b>顺手抢救一份到调用方给的落点</b>（用户 2026-10-06 真机：那一片是 `111.zip`、
    /// 它是下一轮那一组跨盘 ZIP 的入口包）：本类收尾会把这个工作区清掉，
    /// 而协调器拿到结论时它**已经不在盘上了**（排障日志逐字：`那一片已经不在盘上（…\output\111.zip）`）
    /// ⇒ 协调器那一档永远搬不到东西、"结果校验"因此看到空目录 ⇒ 列表显示「解压失败」+ 源包不处理。
    /// ⇒ **谁建谁负责**：记下它的同时就把它复制一份到 <see cref="UnresolvedPieceRescueDirectory"/>
    /// （那个目录是协调器给的、在它自己的暂存目录下，不随本类的工作区一起消失）。
    /// ⛔ 只复制、⛔ 不改名、⛔ 不删原件（本类照旧按原口径处置自己的东西）。</para>
    /// </summary>
    internal void RememberUnresolvedVolumePiece(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        RescueUnresolvedPiece(path!);

        foreach (string existing in _unresolvedVolumePieces)
        {
            if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        _unresolvedVolumePieces.Add(path!);
    }

    /// <summary>
    /// 把这一片复制一份到 <see cref="UnresolvedPieceRescueDirectory"/>（见
    /// <see cref="RememberUnresolvedVolumePiece"/> 的说明）。判不出 / 拷不动 ⇒ 什么都不做。
    /// </summary>
    private void RescueUnresolvedPiece(string piece)
    {
        string? rescueDirectory = UnresolvedPieceRescueDirectory;

        if (string.IsNullOrWhiteSpace(rescueDirectory))
        {
            return;
        }

        try
        {
            if (!File.Exists(piece))
            {
                return;
            }

            Directory.CreateDirectory(rescueDirectory);

            string target = Path.Combine(rescueDirectory, Path.GetFileName(piece));

            if (!File.Exists(target))
            {
                /*
                 * ⚠ 2026-10-10：**先试硬链接**（同卷、零字节，而且与原件是**同一份物理文件**）。
                 *
                 * <para>为什么要紧：这一份只是"让协调器在本类的工作区被清掉之后还看得见这一片"
                 * （见上面那段说明），而原件随后会被接片那一档按规范卷名搬进落点层 ——
                 * 两份**同一个 inode** 时，定稿那一站搬这一份就认得出"它已经在落点层里了"
                 * （<see cref="ArchiveFixer.Detection.FileIdentity"/>）⇒ ⛔ 不会再改名落出第二份。
                 * 真机 EEEE 2026-10-10 的 `…\111\111\111(1).zip` 正是"复制出来两份、定稿按同名冲突
                 * 自动改名"落出来的（用户点名的「第二轮续解又解一遍」。</para>
                 *
                 * <para>⛔ 只加一份**新名字**、⛔ 不动原件一个字节；跨卷 / 文件系统不支持硬链接 ⇒
                 * 退回复制，行为与改动前逐字相同。</para>
                 */
                if (!HardLinkHelper.TryCreateHardLink(target, piece))
                {
                    File.Copy(piece, target, overwrite: false);
                }
            }
        }
        catch
        {
            // 拷不动（占用 / 权限 / 空间）⇒ 什么都不做：协调器那一档照旧按"判不出就不做"处置。
        }
    }

    /// <summary>
    /// 把"跨盘 ZIP 末片"那一档建出来的临时链接删掉（**只删我们刚建的那几个名字**；
    /// 硬链接删名字不动数据，用户的源片一个字节不动）。
    ///
    /// <para>⛔ 只在这个工作区里动手：路径必须以 <see cref="ExtractionWorkspace.RootDirectory"/> 开头，
    /// 越界的一律不碰（兜底永远落"什么都不做"）。</para>
    /// </summary>
    private void RemoveCrossBoundaryLinks()
    {
        if (_crossBoundaryLinks.Count == 0)
        {
            return;
        }

        string root = CurrentWorkspace?.RootDirectory ?? string.Empty;

        foreach (string path in _crossBoundaryLinks)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)
                    || root.Length == 0
                    || !SafePathHelper.GetFullPathSafe(path)
                        .StartsWith(
                            SafePathHelper.GetFullPathSafe(root) + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(path))
                {
                    continue;
                }

                FileOperations.Delete(path);
            }
            catch
            {
                // 删不掉就留着 —— 它只是多出来的一个名字；工作区收尾照样整份清掉。
            }
        }

        _crossBoundaryLinks.Clear();
    }

    /// <summary>
    /// 上一次运行留下、已被本次续跑取代的工作区（多分支询问 → 用户确认继续这一条路）。
    ///
    /// 为什么是**列表**而不是一个槽位：一个实例上可能出现"询问 → 续跑"这样的多次运行，
    /// 单槽位会在后一次续跑时把前一份**悄悄忘掉**——而那一份往往就是几百 MB。
    /// 这里只负责"记着"；清理时机只有一个：某次运行**成功**收尾时（见 FinalizeRun）。
    ///
    /// 换归档（不是同一个包的续跑）时整份清空：那些工作区属于**别的**任务，
    /// 不能因为后来某个任务成功就被删掉 —— 它们的状态是"部分完成，要保留"。
    /// </summary>
    private readonly List<ExtractionWorkspace> _supersededWorkspaces = new();

    /// <summary>最近一次运行解的是哪个归档。用来判断"这一次是不是同一个归档的续跑"。</summary>
    private string _lastRunArchivePath = string.Empty;

    /// <summary>
    /// **不变量 11 的检查口**（AGENTS.md §6 第 11 条），可空。
    ///
    /// <para>
    /// ⚠ **只在第 0 层**（用户给的源包）开工之前问一次："这一层要解的那个源文件还是原来那一份吗？"
    /// 返回非 null = 已经拦下，本层不解、整条递归停下，那句话就是这次的结论。
    /// 实现上的闸门就是主循环里那句 <c>item.IsRoot &amp;&amp; _sourceCheck != null</c>。
    /// </para>
    /// <para>
    /// 为什么第 1 层起不问：那几层解的是**我们自己产出的过程物**（工作区里的内层包），
    /// 它们本来就不在源包快照里 —— 拿源包的快照去比只会得出一句必然错误的结论
    /// （判据与快照都在协调器，见 <c>ExtractionCoordinator.CheckRootSourceUnchangedAsync</c>）。
    /// </para>
    /// <para>
    /// 为什么由调用方注入而不是递归核心自己判：快照挂在 <see cref="ArchiveTask"/> 上、
    /// 判据与状态落法都在协调器（同一句话要同时出现在任务状态、失败清单与日志里）。
    /// 递归核心只知道"要解哪个归档"，它不认识快照，也不该认识 ——
    /// 与引擎 / 探测器 / 密码来源全部注入是同一个理由。
    /// </para>
    /// </summary>
    private readonly Func<ArchiveTask, Task<string?>>? _sourceCheck;

    /// <summary>
    /// **详细日志档**（⑥设置 →「详细日志（排查用）」，默认关）。
    ///
    /// <para>为什么递归核心要知道它（用户 2026-09-27：「开了更详细的日志选项怎么还是这么简单」）：
    /// 递归路径以前**连一条候选日志都没有** —— 真机那次 13 分钟走的正是这条路，
    /// 日志里连"试了几个候选"都看不出来。打开详细日志后，每个候选一条 INFO、
    /// 每层开工一条 INFO，与单层路径同一套措辞（见 <see cref="VerboseLog"/> 的说明）。</para>
    ///
    /// <para>⚠ 候选日志里**只有脱敏占位符**：本类拿到的 passwordProvider 只给值，
    /// 它自己也不知道来源，所以描述由调用方注入（<see cref="DescribeCandidate"/>）。</para>
    /// </summary>
    public bool VerboseLog { get; set; }

    /// <summary>
    /// 把一个密码候选描述成"能写进日志的那半句"（**必须已脱敏**）。
    ///
    /// <para>由调用方注入而不是本类自己拼：候选的来源（空密码 / 统一密码 / 密码本第 N 项…）
    /// 只有 <c>PasswordService</c> 知道，递归核心只拿到值。注入方给的就是单层路径
    /// 用的那一份 <c>BuildTryPasswordLogText</c>，于是两处口径**逐字一致**。</para>
    /// </summary>
    public Func<string, int, string>? DescribeCandidate { get; set; }

    /// <summary>
    /// 某一层的密码**被验证成功**的回传口（参数 = 那把明文密码）。
    ///
    /// <para>存在的理由 = **两条解压路的行为对齐**（AGENTS.md：递归层与单层路行为必须对齐）。
    /// 单层路在候选循环里成功那一刻会调 <c>PasswordService.RecordPasswordSuccess</c>，
    /// 于是"本批已成功的密码"会被提到后面每一个包的候选表**最前面**（用户 2026-09-26 第 45 条：
    /// 「复用本批次已成功的密码，不就是在放屁吗，每次都是重新一轮密码检测」）。
    /// 而默认档是「有压缩包就解压」⇒ 解压交给递归核心做 ⇒ 递归路不报这一声，
    /// 那条复用链在**默认档下整条失效**（2026-10-16 实测：同一批第二个包照旧从空密码开始、
    /// 把 6 个候选全试一遍）。</para>
    ///
    /// <para>⛔ 它**只回传，不落地**：<see cref="RecursionLayerReport"/> 那条"本类不得出现明文密码"
    /// 的规矩一个字不动（这里与既有的 <see cref="DescribeCandidate"/> 同一档 —— 那个口子本来就把
    /// 候选值交给调用方）。</para>
    ///
    /// <para>唯一调用点 = <see cref="FinishSuccessfulLayerAsync"/>（三种成功路的**共同出口**：
    /// 主引擎循环 / 尾部归档直读 / 换引擎兜底），所以直读与兜底两条路也照样报。</para>
    /// </summary>
    public Action<string>? PasswordVerified { get; set; }

    /// <summary>
    /// 「停止后续」的信号（可空）。为真 = 用户想让一切都停下来，**不是**取消当前任务。
    ///
    /// <para>为什么要这个口子（2026-10-05 只读审计）：单层路径在候选之间看 <c>IsStopping</c>
    /// （语义：正在解的那一次不打断，下一个密码不再试），而递归这条路**拿不到这个信号** ——
    /// 用户点了「停止后续」，这一层剩下的候选照样一个一个试完。</para>
    ///
    /// <para>为什么是**可选属性**、不是构造参数：测试里有 20 多处直接 <c>new RecursiveExtractor(...)</c>，
    /// 加参数会把它们全部改一遍；属性不传 = 行为与从前逐字相同。由调用方注入（协调器接
    /// <c>() =&gt; IsStopping</c>），与 <see cref="VerboseLog"/> 同一套注入方式：
    /// 递归核心不认识 GUI，也不该认识。</para>
    /// </summary>
    internal Func<bool>? StopRequested { get; set; }

    /// <summary>
    /// 「可疑条目提示」的**回传口**（可空）：本层清单里若有可执行 / 脚本类条目，
    /// 这里收到那句话（非空），由调用方写进任务字段与日志。
    ///
    /// <para>为什么要回传而不是本类自己写（2026-10-05 只读审计）：那条提示的**判据与文案**
    /// 只有一处（<c>ExtractionCoordinator.AnalyzeDangerousEntries</c>，internal static），
    /// 写进哪个字段也只有一处（<c>PublishDangerousEntriesHint</c>）—— 递归层只管"这一层的清单里有"，
    /// ⛔ 不复制第二份判据、也不自己决定显示在哪。</para>
    /// </summary>
    internal Action<string>? DangerousEntriesReported { get; set; }

    /// <summary>
    /// **这一片要不要交给本单的递归去解**（可空；由调用方回答）。
    ///
    /// <para>参数 = 这一层产物里那个"看起来是归档"的路径；返回 true ⇒ **不排进本单递归**
    /// （只留名，交给这一组的入口那一单）。返回 false / 没注入 ⇒ 照旧排进去。</para>
    ///
    /// <para>为什么由调用方回答（2026-10-22，`24242424` 现场）：判据要读**组事实**
    /// （`VolumeGroupUnit` 的入口包 / 本批已经开过这一组的那一单）——那本账只有协调器有，
    /// 递归核心不认识它。运行期现场：三个外层包的递归各自把同一片当内层包排了队，
    /// 谁先凑齐兄弟卷谁就把整组开出来 ⇒ 同一组解多遍 ⇒ 盘上两份内容物。</para>
    /// </summary>
    internal Func<string, bool>? ShouldStopAtVolumeGroupMember { get; set; }

    /// <summary>
    /// 要不要统计可疑条目（⑥设置 →「可疑条目提示」，默认开）。
    ///
    /// <para>注入方式照 <see cref="VerboseLog"/>：由调用方按设置当场赋值（改完设置不重启也要生效）。
    /// 关掉时判据返回空串 ⇒ 一个字段都不写、一行日志都不打，与加这条之前逐字相同。</para>
    /// </summary>
    public bool ReportDangerousEntries { get; set; }

    /// <summary>
    /// passwordProvider：给定归档路径，返回按优先级排好的密码候选（**空字符串代表试空密码**）。
    /// 它由调用方注入，递归层自己不碰密码本，也不记明文。
    /// </summary>
    /// <param name="log">
    /// 日志出口（level, message），可空。清理工作区是**不可逆操作**，删前删后各要一条 INFO、
    /// 删失败要一条 WARN（AGENTS.md §9.5 同一要求）—— 所以本类需要一个写日志的地方。
    /// </param>
    /// <param name="sourceCheck">
    /// 每层开工前的"源文件有没有变"检查（不变量 11），可空（不传 = 不做这件事，
    /// 既有调用点与单测的行为因此一个字都不变）。
    /// </param>
    public RecursiveExtractor(
        IArchiveEngine engine,
        IArchiveProber prober,
        Func<string, IReadOnlyList<string>> passwordProvider,
        RecursionLimits? limits = null,
        Action<string, string>? log = null,
        Func<ArchiveTask, Task<string?>>? sourceCheck = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _prober = prober ?? throw new ArgumentNullException(nameof(prober));
        _passwordProvider = passwordProvider ?? (_ => Array.Empty<string>());
        _limits = limits ?? RecursionLimits.Default;
        _log = log;
        _sourceCheck = sourceCheck;
        _layerPlanner = new RecursiveLayerPlanner(BuildPasswordCandidates, Log);
    }

    private async Task<LayerOutcome> ExtractLayerAsync(
        WorkItem item,
        CancellationToken cancellationToken,
        IProgress<ArchiveProgress>? progress = null,
        Action<ArchiveStallNotice>? stalled = null,
        string? carvedSource = null)
    {
        _exactStopReasonReported = false;
        /*
         * ===== 这一层到底交给引擎哪一个文件（2026-10-04 真机，用户报"密码是对的怎么解压不了"）=====
         *
         * 现场：`HK.7z.001` 第 0 层解出 3 个 4K 视频（7.11 GiB，全部正常），第 1 层探到
         * `4K (11)_2.mp4` 是内层包 → 直接把**原文件**交给 7-Zip → 7-Zip 回
         * `Cannot open the file as archive` → 整条链判"部分完成" ⇒ 已解出的 7.11 GiB 一个字节都不发布、
         * 工作区整份被清掉（14 分钟白跑）。
         *
         * 根因：那个 mp4 是**双面文件**（真视频 + 尾部一整个 ZIP，用户资源包里的常见形态）。
         * 7-Zip 只在"前面垫的数据 ≤ 8 MiB"时才容忍这种整体偏移，超过就报上面那句原话；
         * 单层路径早就接了这一档（先按偏移抠出来再解），**递归这条路一直没接** ——
         * 而 2026-10-04 起出厂默认档正是"展开所有分支"，于是这条路成了默认路径。
         *
         * 修法：本方法多一个"改用哪一份"的参数。正常传 null（照旧用 <see cref="WorkItem.ArchivePath"/>）；
         * 引擎回过"这不是归档"、而它又确实是双面文件时，外层用
         * <see cref="TryCarveEmbeddedInnerArchiveAsync"/> 抠出副本，再带这个参数重跑一次本方法。
         * ⛔ 抠出来的只是**副本**，原文件一个字节都不动；⛔ 判不出是不是双面文件 ⇒ 什么都不做（照旧失败）。
         */

        LayerPlan plan = _layerPlanner.Prepare(item.Depth, item.ArchivePath, carvedSource, progress, VerboseLog);
        string archivePath = plan.ArchivePath;
        string layerLabel = plan.LayerLabel;

        /*
         * ===== 跨层收卷 + 凑不齐就别试（用户 2026-10-05 真机）=====
         *
         * 现场：三卷 7z 的三片分别躺在**三个不同的层产物目录**里（`layer-001\output\HK.7z.002`、
         * `layer-002\output\HK.7z.003`、`layer-003\output\HK.7z.001`）。引擎找兄弟卷**只看入口文件
         * 旁边那一层** ⇒ 第 2 层拿 `HK.7z.001` 单独去解必然报
         * `Open ERROR: Cannot open the file as [7z] archive` ⇒ 整条链判「部分完成」、
         * 工作区 7 个文件 / 13.48 GiB 整份删掉、什么都没发布。用户原话：
         * 「各分卷在不同的目录，你就将其全部移动到头文件 .001 同级目录里面去」、
         * 「要不然你在分开了你还会继续解压单独的001」。
         *
         * <para>挂点为什么在**这里**（而不是 `ProbeInnerArchivesAsync` 的「还原」工序之后）：
         * ① 收卷要的是"**到解这一层时**兄弟卷都齐了"—— 三片由三个同深度的分支各自产出，
         * 谁先谁后由队列决定，在"产出"那一刻收会漏掉后面才产出 / 才被抠出来的那几片；
         * 解这一层是**唯一**能确定"该在的都在了"的时刻。
         * ② "凑不齐就别试"本来就只能在**调用引擎之前**判（这里正是引擎调用之前、候选循环之外）。
         * ③ 「还原」工序那一挂点照旧不动：它管的是本层产物的名字，与收卷互不干扰。</para>
         *
         * <para>⛔ 判据与执行体都不在这里：整件事转调 `VolumeNameRepair.ResolveCrossLayerVolumeGather`
         * （纯计划）+ `VolumeNameRepair.TryApply`（全成或全不成、绝不覆盖、失败倒序回滚）。
         * ⛔ 候选池只有两处：**入口自己那一层**（只读）+ **这条链的各层产物目录的直接子文件**
         * （`CurrentWorkspace.Layers`，⛔ 不递归、⛔ 不含 `carved` 这种我们自造的兄弟目录、
         * ⛔ 绝不碰用户源目录）；跨盘一律不收（`BuildPlanFromOrder` 里判）。</para>
         */
        /*
         * ===== 跨盘 ZIP：末片在手、其余几片散在源目录里（2026-10-05 真机第八批）=====
         *
         * 现场：`111.z0删除1/2/3` 三片散在三个源目录里，而这一组的末片 `111.zip` 压在两层层层加密的
         * RAR 里面。批首那一刻末片还在包里 ⇒ 源包那条路看不见它；链把末片解出来之后，其余几片又全在
         * 用户源目录里 ⇒ 上面那条跨层收卷（候选池 = 工作区各层产物）也看不见它们。两条路各自封闭，
         * 结果就是 7-Zip 报 `Missing volume : 111.z01`、整条链判「部分完成」、什么都没发布。
         *
         * 判据是**硬证据**：末片的 EOCD 是明文（`-p` 只加密数据），它自己写着"我是第 k 片、一共 n 片"。
         * 动作 = 给源目录里那几片在入口这一层**多起一个规范卷名**（硬链接，零字节；⛔ 不改名、不搬、
         * 绝不把用户的源片搬进工作区 —— 工作区是整份删的）。凑不齐 ⇒ 一次引擎调用都不做（不变量 7）。
         */
        /*
         * ⛔ **候选池要并上"这条链各层产物里的分卷片"**（2026-10-07 真机 EEEE）：老写法只给
         * "找卷窗口"（RootSourceCandidates），而 111(3) 解出来的那一片
         * 111(3)\111(3)\111.z0删除2 是**链产物**、不在窗口里 ⇒ 末片 gather 永远凑不齐
         * z01/z02/z03，整组判"判不出该怎么收"、零引擎调用。
         * 并池只多给"能被名字/内容定出盘号"的候选；⛔ 动作仍是硬链接（源片不改名不搬），
         * 盘号判不出/撞号的候选在 gather 内部照旧被拒（ResolveSpannedZipDiskGather 的判据不动）。
         */
        List<VolumeCandidate> spannedPool = RootSourceCandidates
            .Concat(EnumerateWorkspacePool(CurrentWorkspace))
            .ToList();
        SpannedZipStageResult spannedStage = RecursiveSpannedZipStage.Run(archivePath, layerLabel, spannedPool);

        if (spannedStage.Blocked)
        {
            Log("WARN", spannedStage.BlockedMessage);
            RememberUnresolvedVolumePiece(archivePath);

            return LayerOutcome.Stop(
                BuildLayerReport(
                    item,
                    result: null,
                    succeededPassword: null,
                    overrideMessage: spannedStage.BlockedMessage,
                    manifestEntries: null,
                    overrideStatus: StatusText.VolumeMissing),
                RecursionStopReason.MissingVolume);
        }

        if (spannedStage.LinkedCount > 0)
        {
            _crossBoundaryLinks.AddRange(spannedStage.LinkedPaths);
            _consumedVolumeSources.AddRange(spannedStage.LinkedSources);
            Log("INFO", spannedStage.LinkedMessage);
        }

        return await LayerExecutor.ExecuteAsync(
                this,
                new LayerExecutionState
                {
                    Item = item,
                    ArchivePath = archivePath,
                    Options = plan.Options,
                    Candidates = plan.Candidates,
                    LayerLabel = layerLabel,
                    CarvedSource = carvedSource,
                    CancellationToken = cancellationToken,
                    Progress = progress,
                    Stalled = stalled,
                    ReportStopReason = (r, m) => ReportStopReason(r, m)
                })
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 同一层里还有**名字**属于同一组的东西吗（⛔ 不看内容 —— 用法见
    /// <see cref="IsPieceOfIncompleteGroup"/> 里那条"片都在、只是名字都坏了"的闸门）？
    /// 判不出 ⇒ true（不抢结论）。
    /// </summary>
    private static bool HasSameGroupNeighbourByName(string piecePath)
    {
        string pieceName = FileNameHelper.GetFileName(piecePath);
        string? directory = Path.GetDirectoryName(piecePath);

        if (pieceName.Length == 0 || string.IsNullOrWhiteSpace(directory))
        {
            return true;
        }

        string self = SafePathHelper.GetFullPathSafe(piecePath);

        try
        {
            foreach (string candidate in Directory.EnumerateFiles(directory))
            {
                if (self.Length > 0 && SafePathHelper.PathEquals(candidate, self))
                {
                    continue;
                }

                string name = FileNameHelper.GetFileName(candidate);

                if (name.Length == 0)
                {
                    continue;
                }

                if (LegacyVolumeNames.BelongsToSameGroup(name, pieceName)
                    || string.Equals(
                        FileNameHelper.GetArchiveBaseName(name),
                        FileNameHelper.GetArchiveBaseName(pieceName),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            return true;
        }

        return false;
    }
    /// <summary>
    /// 这一片分卷的**入口包**在不在它旁边（同一个目录里）？
    ///
    /// <para>判据只认两把既有尺子：<see cref="LegacyVolumeNames.BelongsToSameGroup"/>（同族同基名）
    /// + <see cref="FileNameHelper.IsVolumeContinuationPart"/>（是不是后续卷）。
    /// 「同组里有一份**不是后续卷**的」= 入口在 ✓（RAR 新式是 `.part1.rar`、7z 数字族是 `.001`、
    /// 跨盘 ZIP 族是 `.zip` 那一片）。</para>
    ///
    /// <para>⛔ **判不出 ⇒ 返回 true**（= 按老口径：照旧走引擎给的那一档，一个字节不改）——
    /// 这是补充闸门，不许因为目录读不了把正常结论覆盖掉。</para>
    ///
    /// <para>⚠ 2026-10-13（困难批 `run-23232323`，运行期定案）：**这一份自己不算"同组的入口"**。
    /// 老写法把**待查的这一份自己**也当候选（它当然"同族同基名"、而且不在"后续卷"那一档）⇒
    /// 对**跨盘 ZIP 族**（那一族的入口名就是 `.zip`）恒返回"入口在旁边" ⇒ 补充闸门等于瞎的：
    /// 夹具 `dbg-c6092d64` 里 `out\bbb-W0-0\bbb-L2.zip`（内容自证是分卷组的一员、同组还缺 `.z01/.z02`）
    /// 明明只有它自己一个文件，却被判成"入口在"⇒ 落「达到密码尝试上限」⇒ 整单失败 + 片被扔掉。
    /// ⇒ 跳过与 <paramref name="piecePath"/> **同一个文件**的候选（比对规范化后的完整路径）。</para>
    /// </summary>
    internal static bool IsGroupEntryPresentBeside(string piecePath)
    {
        string pieceName = FileNameHelper.GetFileName(piecePath);
        string? directory = Path.GetDirectoryName(piecePath);

        if (pieceName.Length == 0 || string.IsNullOrWhiteSpace(directory))
        {
            return true;
        }

        string self = SafePathHelper.GetFullPathSafe(piecePath);

        try
        {
            foreach (string candidate in Directory.EnumerateFiles(directory))
            {
                if (self.Length > 0 && SafePathHelper.PathEquals(candidate, self))
                {
                    // ⛔ 这一份自己不是"同组的入口"（见上面 2026-10-13 那条）。
                    continue;
                }

                string name = FileNameHelper.GetFileName(candidate);

                if (name.Length == 0
                    || !LegacyVolumeNames.BelongsToSameGroup(name, pieceName))
                {
                    continue;
                }

                if (!FileNameHelper.IsVolumeContinuationPart(name))
                {
                    return true;
                }
            }
        }
        catch
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// **这一份是不是"某个还没凑齐的分卷组的一员"**（唯一出口；⛔ 只读事实、⛔ 不动盘上任何东西）。
    ///
    /// <para>两个条件同时成立才算：① **它属于某个分卷组** —— 名字像后续卷
    /// （<see cref="FileNameHelper.IsVolumeContinuationPart"/>，能吃骨架档 `par@@@@t3`）
    /// **或** 内容自证（底层识别那张卡 <see cref="FileNatureCard.Membership"/> = <c>VolumeMember</c>）；
    /// ② **同组入口不在它旁边**（<see cref="IsGroupEntryPresentBeside"/>）。</para>
    ///
    /// <para><b>为什么要有这个出口</b>（2026-10-13 困难批 `run-4444`，运行期定案）：以前这一档
    /// 只有名字那一半，于是**名字被伪装过头**的那一片（后缀整个换成 `.jpg`）落不进"缺卷"，
    /// 掉进"密码对了但数据坏了 ⇒ 损坏"⇒ 整单「解压失败」+ 已经解出来的那一片被扔掉
    /// ⇒ 那一组永远缺片（①红）。而事实是**这一组不全**：手上只有这一卷，引擎当然读不到
    /// 后面那些卷里的数据。内容那一半（`Membership`）正是用户要的"底层识别"结论：
    /// RAR 的 Volume 位 / RAR4 的首卷标记 / 跨盘 ZIP 的盘号 —— ⛔ 全都不看名字。</para>
    ///
    /// <para>⛔ 判据**没有放宽到"判不出"**：`Membership == Unknown` 照旧按名字那一半判，
    /// 名字也不像 ⇒ 走引擎给的那一档；`ProvablyNotVolumeMember`（可证不是分卷成员）更是明确排除。</para>
    ///
    /// <para>⛔ <b>落"缺卷"之前先过一道闸：同一层里还有没有别的<b>名字</b>属于这一组的东西</b>
    /// （唯一出口 <see cref="HasSameGroupNeighbourByName"/>）。</para>
    ///
    /// <para><b>为什么这一条对两条路都必须有</b>（守门用例实测逐字）：`EncryptedVolumePipelineTests`
    /// 那两条把**三片都放在同一层**（7z 造的 ZIP 分卷、每片名字缀「删除」）。本机用 7z 复刻同一形状 +
    /// `volinfo` 探针实测逐字：第一片是 `格式=Zip 分卷身份=VolumeMember 卷号=判不出`，
    /// **另外两片内容里一点证据都没有**（`格式=Unknown 分卷身份=Unknown` —— 中间片本来就没有魔数）
    /// ⇒ "旁边还有同族成员"那条**内容**否决对它们等于瞎的；而**名字**那把尺子看得见
    /// （探针逐字：`enc.zip.002删除` ⇒ `名字：卷号=2 … 基名=enc`）。
    /// 缺了它，会把"片都在、只是名字都坏了"错判成「缺卷」⇒ 两条既有守门当场红
    /// （`Expected: "密码错误" Actual: "分卷缺失"`）。</para>
    ///
    /// <para>⚠ 2026-10-21：这一条原来**只给单层路**那两个调用点（参数 `requireNothingElseBeside`），
    /// 迁移成"普通任务全递归"之后同一个形状改由**递归路**遇到 ⇒ 运行期复现
    /// （夹具 `dbg-a9668697`：`enc.zip.001` 是完整一组的第一片、密码错误）被测成「分卷缺失」。
    /// ⇒ 参数删掉、两条路共用同一条闸门（⛔ 不再按"哪条路"分档）。</para>
    /// </summary>
    internal static bool IsPieceOfIncompleteGroup(string archivePath, bool requireNothingElseBeside = false)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            return false;
        }

        /*
         * 单层路那一档多一条闸门：**同层里不许还有别的"属于这一组"的东西**
         * （三片同层、只是名字都坏了 ⇒ 那不是"入口不在旁边"，是这一组齐了/在尝试拼）。
         */
        if (requireNothingElseBeside && HasSameGroupNeighbourByName(archivePath))
        {
            return false;
        }

        FileNatureCard card = FileNature.Inspect(archivePath);

        /*
         * ⛔ **内容可证"不是分卷的一员" ⇒ 名字那一格一律作废**（2026-10-18，唯一出口
         * <see cref="Detection.VolumePieceFacts.ContentProvesNotAVolumePiece"/>）。
         *
         * <para>现场逐字（困难批 `24242424`，`walk` 探针）：`222-W1-1.932` 的真实内容是
         * **单盘 ZIP**（卡上 `Membership=ProvablyNotVolumeMember / BodyKind=SingleComplete`），
         * 老写法在这一行**之前**就用名字的 `IsVolumeContinuationPart` 当场 `return true` ——
         * 于是递归路把一条普通 ZIP 当成"这一组的后续卷、入口不在旁边"⇒ 第一条密码候选一错就收场
         * （9 个候选只试了 1 个）⇒ 它里面那一片（第 2 片）永远出不来 ⇒ 整组缺卷。</para>
         *
         * <para>⛔ 判据只否证这一种；`Unknown`（7z 中间片等）照旧让名字那一半说话（见下面那条）。</para>
         */
        if (Detection.VolumePieceFacts.ContentProvesNotAVolumePiece(card))
        {
            return false;
        }

        /*
         * ===== 通用分片族的**第一片**（2026-10-18，困难批 `30303030` 现场）=====
         *
         * <para>现场逐字：`333-L2.zip.png`（7z `-tzip -v` 的第 1 片、被伪装成 `.png`）单独打开 ⇒
         * 7-Zip `Unexpected end of archive` ⇒ 老判据落「压缩包损坏」⇒ 整单失败 + 那一片被连工作区扔掉
         * ⇒ 那一组永远缺第 1 片 ⇒ `contentBytes=0`。</para>
         *
         * <para>内容事实（卡上）：格式=Zip + **开头是本地文件头** + **尾部没有 EOCD** ⇒ 它是从数据里
         * 切出来的**第 1 片**（一份完整的单卷 zip 一定有 EOCD）。⇒ 按「缺卷」收，片原样留着，
         * ⛔ 不按「损坏」扔掉。⛔ 同组入口就在旁边 ⇒ 不抢结论（交给引擎）。</para>
         */
        if (card.Format == VolumeContentFormat.Zip
            && card.HasZipLocalHeader
            && !card.HasZipEndRecord
            && !IsGroupEntryPresentBeside(archivePath)
            && !HasSameGroupNeighbourByName(archivePath))
        {
            return true;
        }

        if (FileNameHelper.IsVolumeContinuationPart(FileNameHelper.GetFileName(archivePath)))
        {
            return true;
        }

        if (card.Membership != VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember)
        {
            /*
             * ⚠ 2026-10-14（官方环台 `10101010`，运行期事实 —— 用户当场点出来的那一条：
             * 「这个不就是 7z.001 吗，为什么底层识别不到」）：**底层识别到了，是这一档没看那张卡**。
             *
             * <para>卡上逐字（那一刻的探针）：`…\recursive\pack111-W0-0_…\layer-000\output\pack111-L2.7z.txt
             * ⇒ 格式=SevenZip 分卷身份=Unknown 卷号=判不出 专属算法=**VolumeFirstPiece**
             * 自述整包=62919013 体积=26214400`（文件头 `37 7A BC AF 27 1C 00 04` = 7z 魔数）
             * —— 起始头自述的整包字节数比这一份大 ⇒ **还有别的片** ⇒ 这一组还没凑齐。</para>
             *
             * <para>为什么原来轮不到看它：**7z 这个格式里根本没有卷号字段** ⇒ 它的 `Membership`
             * 恒为 `Unknown` ⇒ 老这一支（"判不出 / 可证不是成员 ⇒ 返回 false"）**把 7z 族整族挡在门外**，
             * 于是那一份被当成"引擎解不开的怪东西" ⇒ 整单落「引擎操作失败」⇒ 产物连工作区一起被清掉
             * ⇒ 组 `pack111-L2.7z.001` 永远缺第 1 片（判据 ① 红）。</para>
             *
             * <para>⇒ 判据：**卡上说"我是这一组的第一片"就当真**（`BodyKind == VolumeFirstPiece`），
             * 结论是**缺卷**（⛔ 不是「损坏」、⛔ 不是「失败」）。
             * ⚠ 先过"同层里还有没有同组的东西"那道闸 —— 实测 `EncryptedVolumePipelineTests.加密的RAR4三卷_名字被改烂…`
             * 就是这一档：三片都在同一层、只是名字都坏了，引擎其实解得开 ⇒ 这一档必须**不抢结论**（先让引擎去解）。
             * ⚠ 2026-10-21：这道闸现在**两条路共用**（参数 `requireNothingElseBeside` 已删，见方法说明）。</para>
             */
            if (Detection.VolumePieceFacts.CardSaysItIsOneOfTheGroup(card)
                && !HasSameGroupNeighbourByName(archivePath))
            {
                /*
                 * ⚠ 2026-10-14 同一类的**第二句**也补上：`VolumePiece` = 卡上写着"我是这一组的一片、
                 * 而且不是第 1 片"（7z 族的中间片 / 末片正是这一档：`格式=Unknown` 但
                 * `专属算法：单卷/分卷=VolumePiece`）—— 它**同样**只有这张卡答得出，
                 * 名字那半边（后缀被改、卷标记被吃）与内容那半边（7z 没有卷号字段）都答不出。
                 */
                return true;
            }

            // 判不出 / 可证不是分卷成员 ⇒ 老口径（⛔ 判据没有放宽到"判不出"）。
            return false;
        }

        /*
         * ⚠ 2026-10-13（困难批 `run-23232323`，运行期定案）：**"我不是第一片"这件事内容自己说得出来**，
         * 那一档就不必再问"入口在不在旁边"——入口**不可能**是这一份自己。
         *
         * <para>现场逐字（夹具 `dbg-31260820`）：程序产出那一份跨盘 ZIP 末片 `out\bbb-W0-0\bbb-L2.zip`
         * （内容自述"一共 3 片、我是第 3 片"），第 1 层开它 ⇒ 引擎解不动（同组还缺 `.z01/.z02`）
         * ⇒ 10 条候选试完落「达到密码尝试上限」⇒ 那一单「解压失败」。
         * 为什么原来那两半都没拦住它：名字那一半认的是"后续卷"，而**跨盘 ZIP 族的入口名就是 `.zip`**
         * （那一族的头尾编号是反的）⇒ 名字说"它是入口"；而"入口在不在旁边"那把尺子会把**它自己**
         * 算成"同组入口"（同名同族、又不在"后续卷"那一档）⇒ 两道都放行。</para>
         *
         * <para>内容证据（卷号 &gt; 1）不给这种歧义留位置：这一份**自己写着不是第 1 片**
         * ⇒ 它那一组的入口必然在别处 ⇒ 结论就是**缺卷**（跳过语义），⛔ 不是失败。</para>
         */
        if (card.VolumeNumber is int number && number > 1)
        {
            return true;
        }

        if (HasSameGroupNeighbourByName(archivePath))
        {
            // 同层里还有"名字也属于这一组"的东西 ⇒ 可能是"片都在、只是名字都坏了" ⇒ 不抢结论。
            return false;
        }
        // 判不出片号 / 内容说"我是第 1 片" ⇒ 才需要"同组入口不在它旁边"这一条
        return !IsGroupEntryPresentBeside(archivePath);
    }

    /// <summary>单测用：把"这一份是不是某个还没凑齐的分卷组的一员"那个**唯一出口**露给测试项目。</summary>
    internal static bool IsPieceOfIncompleteGroupForTests(string path) => IsPieceOfIncompleteGroup(path);
    /// <summary>单测用：把"同组入口在不在它旁边"那一半露给测试项目（用来钉住它的**已知限制**）。</summary>
    internal static bool IsGroupEntryPresentBesideForTests(string path) => IsGroupEntryPresentBeside(path);

    /// <summary>
    /// 这一层**真的解开了**之后的共同收尾：量产物 → 落点校验 → 探内层包 → 出层报告。
    ///
    /// <para><b>为什么把它抽出来</b>（2026-10-05 真机第六批）：多了一条**不经过引擎**的成功路
    /// （尾部归档里只有一个原样存的条目 ⇒ 直接按偏移把那一段取出来，见
    /// <see cref="TryTakeSingleStoredEntryAsync"/>）。两条路必须交出**同一形状**的层报告 ——
    /// 落点校验、内层包探测、清单口径一个字都不许分叉（§9.5）。</para>
    /// </summary>
    /// <summary>
    /// **候选解完之后校验这一层的产物**（判据唯一出口 = <see cref="OutputVerifier.Verify"/>）。
    ///
    /// <para>与单层路 <c>VerifyStageProductsAsync</c> 用的是**同一个**出口、同一套规则
    /// （0/0 判失败、"实际多于预期也算通过"、拿不到清单 ⇒ 只做非空校验…），
    /// ⛔ 这里不许另写一套尺子。</para>
    ///
    /// <para>清单取"解开它的那个候选"自己列出来的那一份（<c>candidateManifest</c>）：
    /// 加密头包用错密码时引擎也会回一份残缺清单，拿它当预期会把解得好好的判成不完整 ——
    /// 与层报告里"采信成功那个候选的清单"是同一条口径。</para>
    /// </summary>
    internal static bool CandidateProductsVerified(
        WorkItem item,
        LayerManifest candidateManifest,
        out string message,
        out OutputVerificationResult verification)
    {
        /*
         * 清单取不到 ⇒ 与收尾那一处**逐字同一处置**：给一份 `Success = false` 的预期，
         * `OutputVerifier` 会退化成"只做非空校验"（⛔ 不是"期望 0 个 / 0 字节"——
         * 那样等于把任何产物都放过）。
         */
        ArchiveListResult expected = candidateManifest.Available
            ? candidateManifest.ToExpected()
            : new ArchiveListResult
            {
                Success = false,
                Message = candidateManifest.Reason
            };

        verification = OutputVerifier.Verify(item.Layer.OutputPath, expected);

        message = verification.Message;
        return verification.Verified;
    }

}
