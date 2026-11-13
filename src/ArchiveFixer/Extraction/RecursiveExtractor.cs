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
{

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
        private IReadOnlyList<VolumeCandidate> RootSourceCandidates
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
        private void RememberUnresolvedVolumePiece(string? path)
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
            ExtractOptions options = plan.Options;
            IReadOnlyList<string> candidates = plan.Candidates;

            PasswordAttemptLedger passwordLedger = new(_limits.MaxPasswordAttemptsPerLayer);
            bool triedAny = false;
            ArchiveOperationResult? lastFailure = null;
            ArchiveOperationResult? informativeFailure = null;
            ArchiveOperationResult? success = null;
            string? succeededPassword = null;

            /*
             * ===== 换引擎兜底要用的两笔账（用户 2026-10-05 拍板"补"）=====
             *
             * · <c>triedCandidates</c> —— 这一层**真正试过**的那几个候选（保序）。
             *   兜底要把"同一批候选"再试一遍，所以⛔ 不能拿 `candidates`（那张表可能更长：
             *   含被跳过的空密码、也含被每层上限截掉的尾巴）—— 两处的候选集合必须逐字相同。
             * · <c>sawEncryptedArchive</c> —— 这一层的归档**确实加密**（判据第三条）。
             *   只读结构化事实：清单里的加密位 / 引擎报的加密头。⛔ 不比中文、⛔ 不自己猜。
             */
            var triedCandidates = new List<string>();
            bool sawEncryptedArchive = false;

            /*
             * 候选循环**走到过的最后一个候选**在 <see cref="candidates"/> 里的下标
             * （-1 = 一个都还没碰）。收尾点名"哪几条没试到"要用它算起点：
             * 上限卡住时循环是在**还没碰那一条**的时候就 break 的（break 在尝试之前），
             * 所以起点 = 它 + 1；被跳过的空密码也算"碰过"（它不是"没试到"，是"按规矩跳过"）。
             */
            int lastConsumedCandidateIndex = -1;

            /*
             * 两义那一档的"第一次出现"（引擎既说密码不对、又说数据坏了）：
             * 收尾时优先拿它当结论（<see cref="ResolvePasswordStopReason"/>），
             * 于是结论与日志同时保留两种可能，⛔ 不会退化成一句"文件损坏"或"密码错误"。
             */
            ArchiveOperationResult? ambiguousPasswordFailure = null;


            /*
             * 本层的清单（L3 的预期，见 <see cref="RecursionLayerReport.Manifest"/>）：
             * `candidateManifest` 是"当前这个候选列出来的清单"，`layerManifest` 只认
             * **真正解开这一层的那一个候选**的清单（见下面赋值处）。
             */
            LayerManifest candidateManifest = LayerManifest.Unavailable("这一层还没有列过清单");
            LayerManifest layerManifest = LayerManifest.Unavailable("这一层没能列出清单（解压前那次列目录没成功）");

            /*
             * 本层"最近一次列目录成功的逐条清单"（部分完成发布的输入，见 ManifestEntries）。
             * 只记**成功**的那一次：列不出来（密码不对 / 加密头）时盘上那份清单根本不存在，
             * 记一份空的/残缺的只会让下游把"缺得太多"当成"包坏了"。
             */
            IReadOnlyList<(string Path, long Size)> lastListedEntries = Array.Empty<(string, long)>();

            /*
             * 「可疑条目提示」每层只回传一次（见下面那一段的说明）：它是"这个包里有什么"，
             * 与用哪个密码打开无关，逐候选重报只会把日志刷成噪声。
             */
            bool dangerousEntriesHintReported = false;

            /*
             * 「引擎读不动 + 双面文件 + 单条目原样存 ⇒ 直接取那一段」这条近路，**这一层只试一次**
             * （挂点在候选循环里"引擎列不出清单"那一刻；试不成 ⇒ 照旧走老路，不逐候选重试）。
             */
            bool attemptedDirectTake = false;

            /*
             * 这一层的最多候选数：**与循环用的同一个上限**（`_limits.MaxPasswordAttemptsPerLayer`）。
             * 先算出来是为了让"候选 i/N"里的 N 与真正会试的个数一致 ——
             * 写成 candidates.Count 会在被上限截断时给出一个永远到不了的 N。
             */
            int layerCandidateLimit = Math.Min(candidates.Count, _limits.MaxPasswordAttemptsPerLayer);

            /*
             * 「加密包不试空密码」要用到的一个事实（用户 2026-10-04 真机）：
             * 这一层**除空密码以外还有没有候选可试**（一个都不剩时不许跳 —— 与单层路径同一条边界，
             * 见 `ExtractionCoordinator` 的 `skippedOnlyEmptyPasswordBecauseEncrypted`）。
             */
            int usableCandidates = candidates.Count(candidate => !string.IsNullOrEmpty(candidate));

            /*
             * 这一层**跳过**了几个候选（"整包已加密" / "文件名已加密"两档：它们一个字节都没解）。
             *
             * <para>为什么要单独记一笔（与单层路径同一条口径）：收尾的
             * <see cref="ResolvePasswordStopReason"/> 是拿"候选总数 vs 试过几个"判断
             * "是不是被每层上限截断了"的，而被跳过的那几个**根本没试** ——
             * 不把它们从总数里减掉，就会把"候选全试完了都不对"误报成
             * 「达到密码尝试上限（候选还有剩余）」。单层路径那边是"先 RemoveAll 再重算
             * maxPasswordAttempts"（见 <c>ExtractionCoordinator</c> 候选循环里那一段），
             * 同一个意思。</para>
             */
            int skippedCandidates = 0;

            /*
             * ===== 这一层"到底有没有列出过清单"（用户 2026-10-05 拍板的那半条）=====
             *
             * `-mhe`（7z）/ `-hp`（RAR）的包**每个候选都列不出来**，引擎在列目录那一步就说"加密头"。
             * 这是「文件名已加密」唯一的证据来源（与单层路径同一个判据）：
             * ① 从头到尾**一次都没列成功**；② 引擎说的确实是加密头（结构化错误类型，⛔ 不比中文）；
             * ③ 没出现过**别的**列目录错误 —— 三条同时成立才认（证据混了 / 判不出 ⇒ 退回既有口径）。
             *
             * ⛔ 只要任何一个候选成功列出过清单，这一档就不成立：那时失败发生在**解压**阶段，
             * 结论照旧走密码错误 / 两义 / 损坏 —— 别把"密码试错了但清单列得出来"的普通加密包说成它。
             */
            bool listedAnyCandidate = false;
            bool sawEncryptedHeadersListFailure = false;
            bool sawOtherListFailure = false;

            /* 引擎在列目录那一步的原话（加密头那一档要带进结论里，与单层路径同一句文案）。 */
            string encryptedHeadersListMessage = string.Empty;

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

            foreach (string candidate in candidates)
            {
                if (!passwordLedger.CanTry)
                {
                    break;
                }

                // 这一条**被循环碰过了**（无论后面是试、还是按规矩跳过）——收尾点名"哪几条没试到"的起点靠它。
                lastConsumedCandidateIndex++;

                /*
                 * ===== 「停止后续」在候选之间生效（2026-10-05 只读审计）=====
                 *
                 * 单层路径 2026-09-27 就修掉了这一档（用户原话：「我都暂停了，你还在尝试新的密码」），
                 * 判据是"正在解的那一个不打断、下一个密码一个都不再试"；可递归这条路**拿不到那个信号**，
                 * 于是用户点了「停止后续」，这一层剩下的候选照样一个一个试完（大包就是几十分钟）。
                 *
                 * 位置刻意在 `attempts` 递增**之前**、与单层路径同一个落点（候选之间）：
                 * 已经试过至少一个候选（`passwordLedger.Tried > 0`）才看信号 —— 一个都没试就停会让收场落到
                 * `triedAny == false`（"没有可用密码"），那是对用户的误报。
                 *
                 * ⛔ 停下之后按"候选还有、只是不再试"收尾（`passwordLedger.Tried < candidates.Count` ⇒
                 * 「达到密码尝试上限」那一档），**不许报成密码错误** —— 候选根本没试完。
                 */
                if (RecursiveStopPolicy.ShouldStop(passwordLedger.Tried, StopRequested))
                {
                    Log(
                        "WARN",
                        RecursiveStopPolicy.BuildMessage(
                            layerLabel,
                            layerCandidateLimit - passwordLedger.Tried));

                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();

                triedAny = true;

                /*
                 * ===== 候选日志（用户 2026-09-27：「试了几个候选」必须看得见）=====
                 *
                 * **默认档就写**：单层路径那条同类日志本来就在候选循环里（每候选一行 INFO），
                 * 而递归路径过去**一条都没有** —— 真机那次 13 分钟走的正是递归路径，
                 * 日志里连"试了几个候选"都看不出来。两条路的口径必须一样。
                 *
                 * 措辞与单层路径**共用同一个格式常量**（StatusText.PasswordCandidateAttemptLogFormat），
                 * 来源说明也共用同一份描述器（DescribeCandidate ← PasswordService.BuildTryPasswordLogText）。
                 */
                /*
                 * 第一道防线（不变量 4）：解压前先列目录，把危险条目名挑出来。
                 *
                 * 递归展开的内层包过去**完全绕过** Security 层（预检与落点校验都只接在单层 GUI 路径上），
                 * 于是一个带 `..\` 的内层包会被原样解开、产物落到目标根之外，而报告里一个字都不提。
                 * 这里按"当前这个密码候选"列一次目录：能列出来就顺手做预检，
                 * 列不出来（加密头 -mhe、损坏）**不拦** —— 拦下来会让正常包也解不开，
                 * 那道兜底是解压后的落点校验。
                 */
                (string? unsafeSummary, ArchiveListResult? listedThisCandidate) =
                    await CheckEntriesBeforeExtractAsync(
                            archivePath,
                            candidate,
                            cancellationToken)
                        .ConfigureAwait(false);

                /*
                 * 本层的清单（L3 的预期）**只认真正解开的那一个候选**：加密头包用错密码时
                 * 引擎也可能回一份残缺/空清单，拿它当预期会把"解得好好的"判成不完整。
                 * 于是每个候选各记一份，等这次解压成功（下面的 `result.Success`）才采信。
                 */
                (candidateManifest, item.ListingSaidNoEncryption) =
                    RecursiveCandidateFacts.FromListing(listedThisCandidate);

                /*
                 * ===== 引擎读不动它 + 它确实是双面文件 ⇒ 直接按偏移取出那唯一一个原样存的条目 =====
                 *
                 * 真机现场（`ArchiveFixer-本次操作_20261005_134118.txt`）：第 1 层三个双面视频，
                 * 每个都先白试一次引擎（`Cannot open the file as archive`，写盘的那一次解压，
                 * 日志里留下三条假 ERROR），再把尾部归档整份抠成副本（78/48/83 秒），
                 * 然后让 7-Zip 从副本里**再解一遍**那唯一一个条目（88/48/92 秒）—— 同一份字节写了两遍、读了两遍。
                 *
                 * 判据三条缺一不可：① 引擎**列不出**这一份（列得出来就说明引擎本来读得动它 ⇒ 照旧走原路，
                 * ⛔ 近路绝不抢在引擎前面）；② 尾部确实有一个自洽的内嵌归档（识别阶段同一个检测器）；
                 * ③ 里面**恰好一个原样存的条目**（见 <see cref="TryTakeSingleStoredEntryAsync"/>）。
                 * 判不出来 ⇒ 一个字都不改，照旧走今天这条路。
                 */
                if (carvedSource == null
                    && !attemptedDirectTake
                    && listedThisCandidate is { Success: false })
                {
                    attemptedDirectTake = true;

                    LayerOutcome? taken = await TryTakeSingleStoredEntryAsync(item, progress, cancellationToken)
                        .ConfigureAwait(false);

                    if (taken != null)
                    {
                        return taken;
                    }

                    LayerOutcome? direct = await TryTakeEmbeddedZipDirectAsync(
                            item,
                            candidates,
                            progress,
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (direct != null)
                    {
                        return direct;
                    }

                    if (taken != null)
                    {
                        return taken;
                    }
                }

                if (listedThisCandidate is { Success: true })
                {
                    listedAnyCandidate = true;

                    lastListedEntries = PartialPublishPlanner.ToManifestEntries(listedThisCandidate);

                    // 「这一层的归档是加密的」（换引擎兜底判据的第三条）：只读清单里的结构化加密位。
                    if (listedThisCandidate.IsEncrypted)
                    {
                        sawEncryptedArchive = true;
                    }

                    /*
                     * ===== 可疑条目提示（2026-10-05 只读审计）=====
                     *
                     * 单层路径在源包预检之后就会统计"可执行 / 脚本类条目"并写进任务字段
                     * （`ExtractionCoordinator.AnalyzeDangerousEntries` + `PublishDangerousEntriesHint`），
                     * 而递归这条路**一次都不做** —— 出厂默认档（展开所有分支）下，内层包里的
                     * `.exe / .bat / .ps1` 一个提示都没有。
                     *
                     * ⛔ 判据与文案仍只有那一处（转调同一个 static，不复制第二份）；
                     * 本类只负责"这一层的清单里有"，显示在哪由调用方决定（<see cref="DangerousEntriesReported"/>）。
                     *
                     * 每层只回传一次：同一份清单每个候选都列得出来，逐候选重报只会把日志刷成噪声
                     * （提示说的是这个包有什么，与用哪个密码打开无关）。
                     */
                    dangerousEntriesHintReported = RecursiveLayerDiagnostics.ReportDangerousEntriesOnce(
                        dangerousEntriesHintReported,
                        ReportDangerousEntries,
                        DangerousEntriesReported,
                        listedThisCandidate);
                }
                else if (string.Equals(
                    listedThisCandidate?.ErrorType,
                    EngineErrorTypes.EncryptedHeaders,
                    StringComparison.OrdinalIgnoreCase))
                {
                    // 这一次列目录失败是"加密头"：它是「文件名已加密」那一档的证据（见上面的说明）。
                    sawEncryptedHeadersListFailure = true;

                    // 加密头的包当然**是加密的**（换引擎兜底判据的第三条，与上一条同一性质）。
                    sawEncryptedArchive = true;
                    encryptedHeadersListMessage = listedThisCandidate?.Message ?? string.Empty;
                }
                else
                {
                    /*
                     * 别的列目录失败（引擎抛异常时为 null、损坏、超时…）：证据混了 ⇒ 那一档不成立，
                     * 结论退回既有的密码口径（"判不出就什么都不改"）。
                     */
                    sawOtherListFailure = true;
                }

                /*
                 * ===== 加密包不试空密码（用户 2026-10-04 真机；**唯一判据** = PasswordProbe）=====
                 *
                 * 现场：真机日志「第 0 层：第6集.zip：开始解压，密码候选 1/10，尝试空密码」——
                 * 一个**已加密**的包先拿空密码白跑一整包。单层路径 2026-09-25 就修掉了这一档
                 * （`PasswordProbe.ShouldSkipEmptyPassword`），可**递归这条路没接**：
                 * 一键处理里内层包走的正是递归，于是同一件事在两条路上长成两种行为。
                 *
                 * 判据只读引擎给的事实（清单里有没有加密条目），⛔ 不比中文、也不自己猜；
                 * 边界与单层路径**逐字相同**：还有别的候选可试才跳（一个都不剩时照旧试空密码，
                 * 否则收场会落到一句更难懂的"未知解压失败"）。
                 *
                 * 账目：这一档**没解过任何东西**，所以不占尝试次数（撤掉 `attempts` 那一格）、
                 * 也不算"试过了"（`triedAny` 跟着退回去），候选总数 N 也跟着减 1 ——
                 * 否则日志里的「候选 i/N」会给出一个永远到不了的 N。
                 */
                EmptyPasswordSkip emptyPasswordSkip = RecursivePasswordSkipPolicy.Match(
                    candidate, usableCandidates, listedThisCandidate);

                if (emptyPasswordSkip != EmptyPasswordSkip.None)
                {
                    passwordLedger.Skip("空密码按规则跳过");
                    triedAny = passwordLedger.Tried > 0;
                    layerCandidateLimit = Math.Max(1, layerCandidateLimit - 1);
                    skippedCandidates++;

                    if (emptyPasswordSkip == EmptyPasswordSkip.EncryptedEntry)
                    {
                        Log(
                            "INFO",
                            string.Format(
                                System.Globalization.CultureInfo.CurrentCulture,
                                StatusText.RecursionSkippedEmptyPasswordLogFormat,
                                layerLabel,
                                Math.Min(usableCandidates, _limits.MaxPasswordAttemptsPerLayer)));
                    }
                    else
                    {
                        Log(
                            "INFO",
                            string.Format(
                                System.Globalization.CultureInfo.CurrentCulture,
                                StatusText.RecursionSkippedEmptyPasswordEncryptedHeadersLogFormat,
                                layerLabel,
                                listedThisCandidate?.ErrorType ?? string.Empty));
                    }

                    continue;
                }

                /*
                 * ===== 单文件上限：本层清单里最大的那个条目（2026-10-05 只读审计）=====
                 *
                 * 不变量 8 的四条"解压前"上限（单文件 / 总大小 / 文件数 / 展开比）里，递归这条路
                 * 过去只映了三条（见 `ExtractionCoordinator.BuildRecursionLimits`）——
                 * 出厂默认档下，一个含 200 GiB 单文件的包不会被这道闸门拦下，事后报的还不是这一条。
                 *
                 * 判据与文案**都转调解压前预算那一份**（<see cref="ResourceBudget.FindLargestFileEntry"/>
                 * / <see cref="ResourceBudget.DescribeSingleFileOverLimit"/>）：同一个上限在两条路上
                 * 必须用同一把尺子、同一句话（§9.5）。
                 *
                 * ⛔ 拿不到清单 ⇒ **不拦**（与 <see cref="IsExpansionRatioExceededAsync"/> 的兜底同口径）：
                 * 加密头包 / 列目录失败时盘上那份清单根本不存在，凭"不知道"去拦会把正常包误杀。
                 */
                if (listedThisCandidate is { Success: true }
                    && RecursiveLayerBudget.FindSingleFileViolation(
                        listedThisCandidate, _limits.MaxSingleFileSize) is string singleFileReason)
                {
                    Log("ERROR", $"{layerLabel}：{singleFileReason}");

                    return LayerOutcome.Stop(
                        BuildLayerReport(
                            item,
                            result: null,
                            succeededPassword: null,
                            overrideMessage: singleFileReason),
                        RecursionStopReason.MaxSingleFileSizeReached);
                }

                if (unsafeSummary != null)
                {
                    return LayerOutcome.Stop(
                        BuildLayerReport(item, lastFailure, succeededPassword: null, unsafeSummary),
                        RecursionStopReason.UnsafeEntry);
                }

                /*
                 * 走到这里 = 这个候选**真的会被拿去解**（上面那几档"按规矩跳过"的都没命中）
                 * ⇒ 记进"这一层真正试过的候选"：换引擎兜底要拿**同一批**再试一遍，
                 * ⛔ 不能把被跳过的空密码也算进去（加密包拿空密码去问 WinRAR 必然是白跑一次）。
                 */
                passwordLedger.Try();

                triedCandidates.Add(candidate);

                // 试过几个候选是**事实**：失败/撞上限那几条路也要带上（协调器据此把"已试 N 个候选"写进结论）。
                item.TriedCandidateCount = triedCandidates.Count;

                /*
                 * ===== 先只解最小的那个条目（探针），再解整包 =====
                 *
                 * 与单层路径**同一个出口**（<see cref="PasswordProbe"/>）：清单里那个最小的文件能解开
                 * = 这个候选是对的；解不开（引擎明确说密码不对）= 这个候选不对 ——
                 * 一个字节的整包数据都没动。挑不出探针、或引擎给不出确定结论 ⇒ 照旧解整包。
                 *
                 * 为什么递归这条路也必须接（用户 2026-10-04 真机）：递归里每个候选过去都是**一次完整解压**，
                 * 大包 + 多个候选 = 几十分钟白跑（单层路径 2026-09-25 修的就是这个）。
                 *
                 * 探针失败的结论**原样当成"这个候选不对"**（`lastFailure` 收那一份引擎结果）⇒
                 * 收尾的停因、两义那一档、失败清单的口径一个字都不用改。
                 */
                ArchiveOperationResult? probeFailure = await ProbeCandidateAsync(
                        item,
                        archivePath,
                        options,
                        listedThisCandidate,
                        candidate,
                        progress,
                        stalled,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (probeFailure != null)
                {
                    lastFailure = probeFailure;

                    Log(
                        "INFO",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.PasswordCandidateRejectedLogFormat,
                            layerLabel));

                    continue;
                }

                /*
                 * ===== 每换一个候选，先把这一层的产物目录清空（用户 2026-09-25 真机铁证）=====
                 *
                 * 与 ExtractionCoordinator 候选循环里同一处修复（那边有完整现场说明）：
                 * 7z 用**错密码**时也会先在输出目录里建出 0 字节的桩文件，然后才报 Wrong password；
                 * 而本层的 options 是 `SkipExisting`（`-aos`）—— 那是为了"归档内有重复条目时保留先落地的那个"，
                 * 结果下一个候选会被这些桩文件**跳过**、直接报成功，正确的密码反而永远没机会解一次。
                 *
                 * ⚠ 清的只是**这一层在工作区里的产物目录**（我们自己造的），绝不碰源文件、也绝不碰最终输出目录；
                 * 清不掉（被占用 / 权限）时不抛：引擎那边由结果校验兜底（校验不过就不算成功）。
                 */
                /*
                 * ⚠ 2026-10-17（`Item45LogAndPasswordTests.真7z_文件名也加密且密码排在后面_空密码照样跳过`）：
                 * **候选日志挪到真正解压之前** —— 原来它排在"列目录"之前，于是"空密码"这一档会先写一行
                 * `开始解压，密码候选 1/6，尝试空密码`，紧接着又写「整包已加密 —— 跳过「空密码」这一档」
                 * ⇒ 日志宣告了一件**根本没做**的事（用户 2026-09-27 那条要的是真事）。
                 * 两个跳过支都 `continue` ⇒ 放在这里等于"只有真去解才写这一行"。
                 */
                int ordinal = passwordLedger.Tried;
                string described = DescribeCandidate?.Invoke(candidate, ordinal)
                    ?? $"尝试密码候选第 {ordinal} 项：******";

                Log(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.PasswordCandidateAttemptLogFormat,
                        layerLabel,
                        ordinal,
                        layerCandidateLimit,
                        described));

                /*
                 * ===== 「先测试再解压」（`Settings.TestBeforeExtract`，用户 2026-09-27 第 37 条）=====
                 *
                 * 每个候选先让引擎**只测试**、通过了才真解压。旧单层路有这一段，迁移成
                 * "普通任务全递归"之后**整条丢了**（运行期实测
                 * `ExtractionPipelineFixTests.先测试再解压_两义档要继续试下一个候选…`：
                 * `测试调用 0 次 / 解压调用 3 次` ⇒ 用户开了那个设置根本不生效）。
                 * ⛔ 判据与处置与单层路那一段**逐字同一条**（`ExtractionCoordinator` 的 pre-test 循环）。
                 */
                if (TestBeforeExtract)
                {
                    ArchiveOperationResult preTest = await _engine
                        .TestAsync(ArchiveRequest.For(archivePath, candidate), cancellationToken)
                        .ConfigureAwait(false);

                    ObserveEngineCall(preTest, archivePath, "test");

                    if (!preTest.Success)
                    {
                        lastFailure = preTest;

                        if (preTest.IsWrongPassword || preTest.IsNeedPassword)
                        {
                            Log("INFO", $"{layerLabel}：解压前测试 —— 这个密码候选不对，继续试下一个。");
                            continue;
                        }

                        /*
                         * 两义那一档（引擎自己说"可能密码不对、也可能数据坏了"）：与上面候选循环同一条
                         * 处置 —— **继续试下一个候选**，并把"两种可能"记下来当收尾结论
                         * （⛔ 不替引擎挑一个）。
                         */
                        if (preTest.IsPasswordOrCorrupted)
                        {
                            ambiguousPasswordFailure ??= preTest;

                            Log(
                                "WARN",
                                string.Format(
                                    System.Globalization.CultureInfo.CurrentCulture,
                                    StatusText.CandidatePasswordOrCorruptedLogFormat,
                                    layerLabel));

                            continue;
                        }

                        // 其余（权限 / 引擎不可用 / 不支持 …）：换密码解决不了，停下并原样上报。
                        Log("ERROR", $"{layerLabel}：解压前测试没过：{PasswordMasker.Sanitize(preTest.Message)}");

                        return LayerOutcome.Stop(
                            BuildLayerReport(item, preTest, succeededPassword: null),
                            RecursionStopReason.EngineFailed);
                    }
                }

                TryResetLayerOutputDirectory(item.Layer.OutputPath);

                ArchiveOperationResult result = await _engine.ExtractAsync(
                        new ArchiveRequest
                        {
                            ArchivePath = archivePath,
                            OutputPath = item.Layer.OutputPath,
                            Password = candidate,

                            /*
                             * 进度与"长时间无响应"提示（2026-09-22 补，本批之前递归内层完全没有进度）。
                             *
                             * 只**转发**，不在这里做任何加工：节流在引擎层（ArchiveProgressReporter，
                             * 最多 250ms 一条），落地在协调器的 TaskProgressSink。
                             * 参数为 null 时引擎侧一个字都不报 —— 既有调用点（测试、CLI 式用法）行为不变。
                             */
                            Progress = progress,
                            Stalled = stalled
                        },
                        options,
                        cancellationToken)
                    .ConfigureAwait(false);

                ObserveEngineCall(result, archivePath, "extract");

                if (result.Success)
                {
                    /*
                     * ===== 「引擎说成功」不等于「这一层的产物就是对的」（2026-10-16）=====
                     *
                     * 与单层路**同一条尺子**：候选解完之后先校验产物，判否 ⇒ **这个候选不算数**
                     * （清掉它写下的东西、接着试下一个候选）。两条路的口径必须一样
                     * （AGENTS.md：递归层与单层路行为必须对齐）。
                     *
                     * 现场（实测取的运行期事实，⛔ 不是读代码推的）：
                     * `OutputVerificationGateTests.第一个候选产出0字节_第二个候选正确_最终成功且两个候选都试过`
                     * —— 候选 1（错密码）在产物目录里留下两个 **0 字节桩文件**，7-Zip 退出码 0；
                     * 老写法当场 `break` 接受它 ⇒ 收尾校验判否 ⇒ **整单落「解压失败」**，
                     * 而候选 2（正确密码）**一次都没被试过**（探针逐字 `Passwords=wrong-one`，
                     * 日志里只有 `密码候选 1/2`）。单层路在同一个形状上会接着试下一个候选并成功。
                     *
                     * 例外两条（与单层路那两条例外**同源**）：
                     * · **产物越界**：那是"写到了目标根之外"这个更具体的问题，比"候选不行"更值得说，
                     *   交给 <see cref="FinishSuccessfulLayerAsync"/> 的落点校验去报（这里放行）；
                     * · **候选没有了**（这一条是最后一个 / 已经到了每层尝试上限）：没有下一个可试的，
                     *   把产物留着交给收尾说清为什么失败（⛔ 不许清掉 —— 那正是单层路
                     *   "最后一个候选不清盘"那条口径）。
                     */
                    bool productsVerified = CandidateProductsVerified(
                        item,
                        candidateManifest,
                        out string candidateVerifyMessage,
                        out OutputVerificationResult candidateVerification);

                    /*
                     * ⚠ 2026-10-17（用户 2026-09-27 口径「包没加密 / 结果不变 → 不许再烧候选」，
                     * 与单层路**逐字同一条**：`realContentProduced && stage.List is { Success: true, IsEncrypted: false }`，
                     * 其中 `realContentProduced = stage.Verification.ActualTotalSize > 0`）：
                     * **这一候选真写出了字节**、而**清单里一个加密条目都没有** ⇒ 换密码根本不可能改变结果
                     * ⇒ 不再烧后面的候选，产物留着交给收尾如实说。
                     *
                     * <para>⛔ 前半截（真写出了字节）缺不得 —— 第一版漏了它，0 字节桩那一档就再也不试下一个候选，
                     * 被既有守门 `OutputVerificationGateTests.第一个候选产出0字节_第二个候选正确_最终成功且两个候选都试过`
                     * 当场逮住（候选 2 才是对的）。</para>
                     *
                     * <para>实测（`ExtractionPipelineFixTests.没加密的包_产物校验不过时_不再换密码也不写密码错误`，
                     * 走的正是递归路）：包没加密、清单 `IsEncrypted=false`，候选却被**烧了 4 次**
                     * （逐字 `Assert.Single() Failure: The collection contained 4 items`）——
                     * 单层路早就有这条闸门，递归路一直没有（AGENTS：两条路的口径必须一样）。</para>
                     */
                    bool listingSaysNoEncryption =
                        candidateVerification.ActualTotalSize > 0
                        && listedThisCandidate is { Success: true, IsEncrypted: false };

                    /*
                     * ===== 「连续两个候选的结果完全相同」也要在**这一支**生效（2026-10-21）=====
                     *
                     * 用户 2026-09-27 的口径是"结果不变 ⇒ 不许再烧候选"，它并不区分引擎怎么说：
                     * 错密码那一支早就有（见上面候选循环里那条 WARN），而"引擎说成功、产物却没对上清单"
                     * 这一支一直没有 ⇒ 实测 `ExtractionPipelineFixTests.连续两个候选结果完全相同_不再试第三个`
                     * 把 4 个候选全烧了一遍（`Expected: 2 / Actual: 4`）。
                     *
                     * 判据只用事实：两次校验的**文件数 + 字节数**相同、而且真的解出了字节（0 字节桩那一档
                     * 必须继续试下一个 —— 正确密码可能就在后面，既有守门
                     * `OutputVerificationGateTests.第一个候选产出0字节_第二个候选正确_…` 钉着它）。
                     */
                    bool sameVerifiedResultAsPrevious =
                        !productsVerified
                        && candidateVerification.ActualTotalSize > 0
                        && item.LastVerifiedCandidateFileCount >= 0
                        && item.LastVerifiedCandidateFileCount == candidateVerification.ActualFileCount
                        && item.LastVerifiedCandidateBytes == candidateVerification.ActualTotalSize;

                    if (!productsVerified)
                    {
                        item.LastVerifiedCandidateFileCount = candidateVerification.ActualFileCount;
                        item.LastVerifiedCandidateBytes = candidateVerification.ActualTotalSize;
                    }

                    bool keepProductsForFailureVerdict =
                        listingSaysNoEncryption
                        || sameVerifiedResultAsPrevious
                        || FindLandingViolation(item.Layer.OutputPath) != null
                        // ⚠ `lastConsumedCandidateIndex` 是**0 起**的下标（初值 -1，每进一次循环 +1）⇒
                        // "这一条就是最后一个" = `>= candidates.Count - 1`。
                        // 第一版写成 `>= candidates.Count` ⇒ 最后一个候选也被判成"还有下一个" ⇒
                        // 0 字节产物被清掉、整层落「密码错误」—— 被既有守门
                        // `假引擎成功但产物全是0字节_不是成功_不续解_不搬其余物_源包不动` 当场逮住
                        // （逐字 `Expected: "解压失败" / Actual: "密码错误"`）。
                        || lastConsumedCandidateIndex >= candidates.Count - 1
                        || !passwordLedger.CanTry;

                    if (sameVerifiedResultAsPrevious)
                    {
                        item.CandidatesProducedIdenticalResults = true;

                        Log(
                            "WARN",
                            $"{layerLabel}：连续两个候选解出来的产物完全相同（{candidateVerification.ActualFileCount} 个文件 / "
                            + $"{candidateVerification.ActualTotalSize} 字节）"
                            + " —— 换密码不会改变结果，不再试后面的候选。");
                    }

                    if (!productsVerified && !keepProductsForFailureVerdict)
                    {
                        Log(
                            "WARN",
                            $"{layerLabel}：结果校验 —— {candidateVerifyMessage}" +
                            "（这个候选不算数，继续尝试下一个候选密码）");

                        // 与"换引擎兜底"同一处清法（⛔ 不在这里另写一份"怎么清"）：
                        // 7z 用错密码会留下 0 字节桩文件，而本层是 `-aos`（跳过已存在）
                        // ⇒ 不清掉的话下一个候选会被自己的桩文件跳过。
                        TryResetLayerOutputDirectory(item.Layer.OutputPath);
                        continue;
                    }

                    /*
                     * 走到这里、产物又没通过校验，只可能是上面那几条之一。其中"清单说没加密"这一档
                     * 要**说清为什么不再试后面的候选**（否则用户看到"只试了一个候选"会以为程序偷懒）。
                     */
                    if (!productsVerified && listingSaysNoEncryption)
                    {
                        Log(
                            "WARN",
                            $"{layerLabel}：结果校验 —— {candidateVerifyMessage}" +
                            "（这个包没有加密：清单里没有加密条目，换密码不会改变结果，不再试后面的候选密码）");
                    }

                    item.TriedCandidateCount = triedCandidates.Count;

                    succeededPassword = candidate;
                    success = result;
                    lastFailure = null;

                    // 这一层真正解开了 ⇒ 采信"解开它的那个候选"给出的清单（L3 的预期）。
                    layerManifest = candidateManifest;

                    /*
                     * 详细日志档：成功那一次的命令行与原话也写下来 ——
                     * 排查的人要拿"成功那次调了什么"去对照"失败那次差在哪"。
                     * 默认档一个字都不写（成功 = 一行摘要，第 44 条）。
                     */
                    if (VerboseLog)
                    {
                        EngineOutputLog.LogVerbose(Log, layerLabel, result);
                    }

                    break;
                }

                lastFailure = result;

                /*
                 * ===== 引擎原话落日志（用户 2026-09-27：「引擎原话从不落日志」）=====
                 *
                 * 放在 `lastFailure = result` 之后、所有 return / continue **之前**：
                 * 三条出路（损坏停下 / 错密码继续 / 其它停下）都要留下这一次的原话 ——
                 * 但**每个候选只留这一份**：默认档与详细档都走这里，出口只有
                 * <see cref="EngineOutputLog"/> 一个（与单层路径同一个），
                 * ⛔ 这里不许自己挑行、自己拼前缀。
                 *
                 * ⚠ 详细档的"参数摘要 + 原话"由下面的 LogVerbose 写（INFO）。
                 */
                if (VerboseLog)
                {
                    EngineOutputLog.LogVerbose(Log, layerLabel, result);
                }

                /*
                 * 默认档：**这个候选失败了**那一条（ERROR / WARN，一行）——
                 * 与单层路径的候选循环同一口径（不变量 6：失败必须留痕），
                 * 十个错候选就是十行，一行一个候选，读得出来"第几个候选开始不对"。
                 */
                if (!VerboseLog)
                {
                    /*
                 * ⚠ 2026-10-17（用户 2026-09-25 第 45 条）：**这一步是"这个候选被否、继续下一个"，不是终态** ⇒
                 * 引擎原话按 **INFO** 写（进"每任务攒着、成功就丢"的缓冲；真失败的收场由协调器写 ERROR 并且
                 * `EndTaskLogCapture` 会把细节全吐出来）。上一版写 ERROR ⇒ 两个错候选就把缓冲冲掉 ⇒
                 * 错候选之后成功的任务留下 27 行（`Item45LogAndPasswordTests` 实测，红检就是那条用例）。
                 */
                    EngineOutputLog.LogFailure(Log, layerLabel, result, levelOverride: "INFO");
                }

                /*
                 * ===== 报错结论要取**走得最远的那一次**，不是最后一次（用户 2026-09-30 真机）=====
                 *
                 * 现场：`giu.7z.001`（7z `-mhe` 四卷）的候选循环里，**正确密码那一次**真解了 13 分钟、
                 * 写出 17.7 GiB 才失败；紧跟其后的几个错密码候选 50 毫秒就被 7-Zip 顶回来
                 * （`Cannot open encrypted archive. Wrong password?`）。收尾用的是 `lastFailure` ⇒
                 * 用户看到的是**最后那个错候选**的原话，真正那次失败（数据校验不过 / 解不出来）被吃掉，
                 * 结论永远是"密码错误：所有候选都试过了" —— 方向全错。
                 *
                 * 判据只用**事实**：这一次尝试在产物目录里留下了多少字节。留下过东西的那一次，
                 * 信息量必然大于"连门都没进去"的那些 ⇒ 记下**第一次**留下产物的失败，收尾优先用它
                 * （没有就退回 `lastFailure`，行为与以前逐字一致）。
                 */
                if (informativeFailure == null && ProducedBytesInLayerOutput(item) > 0)
                {
                    informativeFailure = result;
                }

                /*
                 * ===== 「单独一片后续卷」开失败 ⇒ 结论是**缺卷**，不是「损坏」（2026-10-13 困难批 `run-2222`）=====
                 *
                 * <para><b>现场逐字</b>：第 2 层开 `bbb-L2.par@@@@t3.rar`（这一组的**第 3 卷**、入口在别的目录）——
                 * 候选 1..6 引擎都说 `Data Error in encrypted file. Wrong password?`；**候选 7（真密码）**
                 * 解出了 `2 个文件 / 共 10489352 字节` ⇒ 老判据（"密码已证实 ⇒ 数据坏了"）当场落**损坏**
                 * ⇒ 整层判损坏、整单失败、**连这一片都被丢掉** ⇒ 那一组永远缺第 3 片 ⇒ ①「内容物解出来」红。
                 * 事实是：**这一组不全**（手上只有第 3 卷）—— 引擎当然解不动它的数据；
                 * 这不是"包坏了"，是"组还没凑齐"。</para>
                 *
                 * <para><b>判据（只读事实，两把既有尺子）</b>：① 这一份是**后续卷**
                 * （唯一出口 <see cref="FileNameHelper.IsVolumeContinuationPart"/>，能吃骨架档 `par@@@@t3`）；
                 * ② 同组入口**不在它旁边**（同目录里没有"同族同基名、且自己不是后续卷"的那一份）。
                 * 两条同时成立 ⇒ 这一趟就按"缺卷"收（与第 0 层那条入口路**同一个出口**：
                 * 记下这一片、交给跨链/批末那一套去凑）。</para>
                 *
                 * <para>⛔ **不动"开不开"**（那一格试过一版"不打开"，把真机形状守门打红 ⇒ 已撤回）：
                 * 候选照旧全试、动作一个字不改，只把**结论**从"损坏"改成"缺卷"。
                 * ⛔ 位置必须在"损坏"与"密码已证实 ⇒ 数据坏了"那两支**之前**（它们会当场 return）。
                 * ⛔ 判不出（目录读不了 / 名字解析不出）⇒ 照旧走引擎给的那一档。</para>
                 *
                 * <para>⛔⛔ **2026-10-18 补：名字那一半必须让内容先说**（困难批 `24242424`，`walk` 探针逐字）——
                 * `222-W1-1.932` 的真实内容是**单盘 ZIP**（卡上 `ProvablyNotVolumeMember` /
                 * `SingleComplete`），名字里的 `932` 只是伪装尾巴 ⇒ 老写法在这一支当场收场
                 * （9 个候选只试 1 个），真正的密码就在密码本第 7 条（`7z t -p…` 实测 `Everything is Ok`）。
                 * 判据 = 唯一出口 <see cref="Detection.VolumePieceFacts.ContentProvesNotAVolumePiece"/>，
                 * 内容可证"不是分卷的一员" ⇒ 名字不算数，照旧把候选试完。</para>
                 */
                if (!result.Success
                    && !Detection.VolumePieceFacts.ContentProvesNotAVolumePiece(archivePath)
                    && FileNameHelper.IsVolumeContinuationPart(Path.GetFileName(archivePath))
                    && !IsGroupEntryPresentBeside(archivePath))
                {
                    Log(
                        "WARN",
                        $"{layerLabel}：这一片是这一组的「后续卷」、而同组的入口包不在这一层 ⇒ 按「缺卷」处理"
                        + "（⛔ 不是「压缩包损坏」）—— 片原样留着，交给批末/跨链那一套去凑。"
                        + $"（引擎原话：{PasswordMasker.Sanitize(result.Message)}）");

                    RememberUnresolvedVolumePiece(archivePath);

                    return LayerOutcome.Stop(
                        BuildLayerReport(item, result, succeededPassword: null, manifestEntries: lastListedEntries),
                        RecursionStopReason.MissingVolume);
                }

                /*
                 * 损坏不再换候选重试（规则 1）：换密码对损坏的归档没有任何帮助，
                 * 只会把同一个损坏包重试 N 遍，既浪费时间又掩盖真正的问题。
                 *
                 * <para>⚠ **2026-10-18 补：落「损坏」之前先问一次"这一份是不是某一组的一片"**
                 * （困难批 `30303030` 运行期定案）—— 手上只有第 1 片时，7-Zip 对通用分片族说的正是
                 * `Unexpected end of archive`（与"包坏了"逐字相同）⇒ 老写法落「压缩包损坏」⇒
                 * 整单失败 + **那一片被连工作区一起扔掉** ⇒ 那一组永远缺第 1 片（`contentBytes=0`）。
                 * 判据与处置**逐字同前两处**（唯一出口 <see cref="IsPieceOfIncompleteGroup"/>）：
                 * 内容自证是分卷组的一员（`Membership=VolumeMember` 或"通用分片第 1 片"）且入口不在旁边
                 * ⇒ 落「缺卷」，片原样留着。⛔ 判不出 ⇒ 照旧落「损坏」（老口径一个字不放宽）。</para>
                 */
                if (result.IsCorrupted)
                {
                    if (IsPieceOfIncompleteGroup(archivePath))
                    {
                        Log(
                            "WARN",
                            $"{layerLabel}：这一片属于某个分卷组、而同组的入口包不在这一层 ⇒ "
                            + "按「缺卷」处理（⛔ 不是「压缩包损坏」）—— 片原样留着，交给批末/跨链那一套去凑。"
                            + $"（引擎原话：{PasswordMasker.Sanitize(result.Message)}）");

                        RememberUnresolvedVolumePiece(archivePath);

                        return LayerOutcome.Stop(
                            BuildLayerReport(item, result, succeededPassword: null, manifestEntries: lastListedEntries),
                            RecursionStopReason.MissingVolume);
                    }

                    Log(
                        "WARN",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.CandidateStoppedByCorruptedLogFormat,
                            layerLabel));

                    return LayerOutcome.Stop(
                        BuildLayerReport(item, result, succeededPassword: null, manifestEntries: lastListedEntries),
                        RecursionStopReason.Corrupted);
                }

                /*
                 * ===== 「密码其实已经对了，坏的是数据」这一档（用户 2026-10-01 真机 `giu910`）=====
                 *
                 * 判据与单层路径（`ExtractionCoordinator` 的候选循环）**同一个出口**
                 * （<see cref="ProducedContentGate"/>）：这一趟在**这一层的产物目录**里真解出了多少东西。
                 *
                 * ⚠ 为什么必须补在这里：那一档修复（2026-10-01）先只接在**单层**那条路上，
                 * 而真机 `giu910` 走的**是这一条**（`SingleChain` 递归）—— 结果用户当晚重跑一次，
                 * 13 分钟、17.7 GiB 又白扔了一遍，报的还是「密码错误」（他原话：
                 * 「为什么试了密码之后再去试一次，我说过要试密码的话要在最开始的时候」）。
                 *
                 * 两义那一档（`IsPasswordOrCorrupted`）一并覆盖：只要这一趟**真解出了内容**，
                 * 两义就当场收敛成"数据坏了"——密码已经不需要再猜。
                 */
                if ((result.IsWrongPassword || result.IsNeedPassword || result.IsPasswordOrCorrupted)
                    && ProducedContentGate.TryMeasure(
                        item.Layer.OutputPath,
                        result.ReportedSubItemErrors,
                        result.FailedEntryNames.Count,
                        out int provenFiles,
                        out long provenBytes))
                {
                    /*
                     * ⚠ 2026-10-13（困难批 `run-4444`，运行期定案 · 与下面 `:2318` 那条**同一个出口**）：
                     * **"密码对了但数据坏了"这一档之前不看"这一份到底是什么"** ⇒ 手上只有某一卷、其余几卷
                     * 在别处（甚至还没被解出来）时，引擎当然读不到后面那些卷里的数据 ⇒ 它报 CRC 错，
                     * 于是这一档把整层落成「损坏」⇒ 这一单「解压失败」+ **已经解出来的那一片被扔掉**。
                     * 逐字现场（夹具 `dbg-7c44505e`，第 2 层开 `bbb-L2.jpg`）：
                     * `密码已经证实是对的（这一趟真的解出了 2 个文件 / 共 10489352 字节）` ⇒
                     * `bbb-W2-1.rar：已完成 2 层，停在第 3 层，原因：压缩包损坏` ⇒
                     * `产物校验未通过：输出目录是空目录，没有产物` ⇒ 盘上再没有 `bbb-L2.jpg`。
                     * ⇒ 落「损坏」之前先问一次那唯一一个出口：**它是不是"某个还没凑齐的分卷组的一员"**。
                     */
                    if (IsPieceOfIncompleteGroup(archivePath))
                    {
                        Log(
                            "WARN",
                            $"{layerLabel}：这一片属于某个分卷组、而同组的入口包不在这一层 ⇒ "
                            + "按「缺卷」处理（⛔ 不是「压缩包损坏」）—— 片原样留着，交给批末/跨链那一套去凑。"
                            + $"（引擎原话：{PasswordMasker.Sanitize(result.Message)}）");

                        RememberUnresolvedVolumePiece(archivePath);

                        return LayerOutcome.Stop(
                            BuildLayerReport(item, result, succeededPassword: null, manifestEntries: lastListedEntries),
                            RecursionStopReason.MissingVolume);
                    }

                    string provenMessage = string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.PasswordProvenDataCorruptedFormat,
                            provenFiles,
                            provenBytes)
                        + "（引擎原话：" + PasswordMasker.Sanitize(result.Message) + "）";

                    Log("WARN", $"{layerLabel}：{provenMessage}");

                    return LayerOutcome.Stop(
                        BuildLayerReport(
                            item,
                            result,
                            succeededPassword: null,
                            overrideMessage: provenMessage,
                            manifestEntries: lastListedEntries),
                        RecursionStopReason.Corrupted);
                }

                // 需要密码 / 密码错误 → 这个候选没用了，试下一个。
                if (result.IsWrongPassword || result.IsNeedPassword)
                {
                    /*
                     * ⛔ **错密码这一支不做"结果相同 ⇒ 提前停"**（2026-10-21 全量当场逮到）：
                     * 加密包的错密码也会在产物目录里留下**一模一样的半截数据**，而正确的密码
                     * 排在后面时它产出的东西完全不同 —— 实测 `Item45LogAndPasswordTests.真7z_先试错两个候选再成功_成功档照样只留一行`
                     * 因此从 Succeeded 变成 Failed（候选 3 才是对的那一个，循环在候选 2 就停了）。
                     * "结果相同 ⇒ 换密码没用"这条只对**引擎已经接受密码**那一支成立
                     * （见下面"引擎说成功但产物没通过校验"那一档），错密码这一支照老口径试完。
                     */
                    Log(
                        "INFO",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.PasswordCandidateRejectedLogFormat,
                            layerLabel));

                    continue;
                }

                /*
                 * ===== 两义那一档（用户 2026-09-30 真机）=====
                 *
                 * 引擎自己说"密码可能不对、也可能数据坏了"（RAR 1.5–4.x 的 `-p` 包：退出码 3 +
                 * 「在加密文件 X 里校验和错误。文件已损坏或密码错误。」），**谁都不许单独定原因**：
                 * · 判成"已损坏"⇒ 当场停下、其余 10 个候选一个都不试（真机就是这么只试了第 1 个）；
                 * · 判成"密码错误"⇒ 把真损坏说成密码错（用户会去反复核对没写错的密码本）。
                 * 所以与"密码错误"同一处置：**继续试下一个候选**；到收尾时结论落"两义"那一档
                 * （<see cref="TaskOutcomeClassifier.TryResolveRecursionStop"/>），日志里也保留两种可能。
                 * 与 7-Zip 侧同一口径（`CRC Failed in encrypted file. Wrong password?` 那句从不当"已损坏"，
                 * 见 `Item37SafetyTests`）。
                 */
                if (result.IsPasswordOrCorrupted)
                {
                    ambiguousPasswordFailure = result;

                    Log(
                        "WARN",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.CandidatePasswordOrCorruptedLogFormat,
                            layerLabel));

                    continue;
                }

                /*
                 * ===== 引擎说"这根本不是归档"时**不写 ERROR**（用户 2026-10-05：三条假 ERROR）=====
                 *
                 * 现场：第 1 层三个双面视频各留了一行
                 * `[ERROR] 解压失败：7-Zip 无法识别或不支持该格式`，而下面紧接着就是
                 * "已按偏移把归档那一段取出成副本……再解一次"并且真的解开了 —— 那是**已知原因**的一步，
                 * 用户读到的却是一句"包坏了"。识别阶段本来就是靠"尾部有归档"把这个文件认成归档的，
                 * 这个事实在交引擎之前就在手上 ⇒ 命中就不报失败，改由补救那一行 INFO 说清。
                 *
                 * ⛔ 对照（用户点名要保留的）：**普通"名字像归档其实不是"的文件**照旧走引擎、照旧报 ERROR ——
                 * 判据只认"尾部确实有一个自洽的内嵌归档"，看不出就是看不懂，一个字都不许放宽。
                 */
                bool looksDoubleFaced = carvedSource == null
                    && string.Equals(result.DetectedErrorType, EngineErrorTypes.UnsupportedFormat, StringComparison.Ordinal)
                    && IsDoubleFacedEmbeddedArchive(archivePath);

                if (!looksDoubleFaced)
                {
                    /*
                     * ⚠ 2026-10-09（用户点名："第一个红字…为啥显示解压失败"）：**引擎报缺卷时不写 ERROR、
                     * 也不叫"解压失败"** —— 缺卷不是失败（「连完整的都没有，这就不是程序的错误」），
                     * 而且"在最后一步之前碰到分卷缺失的就先跳过" ⇒ 这一行改 WARN + 先说"先跳过"。
                     * 判据读**结构化错误码**（`DetectedErrorType`），⛔ 不比中文文案。
                     */
                    bool missingVolume = string.Equals(
                        result.DetectedErrorType,
                        EngineErrorTypes.VolumeMissing,
                        StringComparison.Ordinal);

                    // 其余（引擎不可用、路径问题、输出冲突…）换密码也解决不了，直接停。
                    Log(
                        missingVolume ? "WARN" : "ERROR",
                        missingVolume
                            ? string.Format(
                                System.Globalization.CultureInfo.CurrentCulture,
                                StatusText.CandidateMissingVolumeLogFormat,
                                layerLabel,
                                result.Message)
                            : string.Format(
                                System.Globalization.CultureInfo.CurrentCulture,
                                StatusText.CandidateStoppedByEngineErrorLogFormat,
                                layerLabel,
                                result.Message));
                }

                /*
                 * 例外：引擎说"这根本不是归档"时，先看一眼它是不是**双面文件**
                 * （真视频 / 图片 + 尾部一整个 ZIP）—— 是的话抠出副本再解一次，
                 * 而不是把它当"这一层失败"（那样会把整条链判成部分完成、已解出的内容全丢）。
                 * 只在第一次（还没抠过）时试，⛔ 不会套娃重试。
                 */
                if (carvedSource == null
                    && string.Equals(result.DetectedErrorType, EngineErrorTypes.UnsupportedFormat, StringComparison.Ordinal))
                {
                    LayerOutcome? retried = await RetryWithCarvedEmbeddedArchiveAsync(
                            item,
                            cancellationToken,
                            progress,
                            stalled)
                        .ConfigureAwait(false);

                    if (retried != null)
                    {
                        return retried;
                    }
                }

                RecursionStopReason engineStop = MapEngineErrorToStopReason(result);

                /*
                 * ===== 「单独一片后续卷」开失败 ⇒ 结论是**缺卷**，不是「损坏」（2026-10-13 困难批 `run-2222`）=====
                 *
                 * <para><b>现场逐字</b>：第 2 层开 `bbb-L2.par@@@@t3.rar`（这一组的**第 3 卷**、入口在别的目录）——
                 * 候选 1..6 引擎都说 `Data Error in encrypted file. Wrong password?`；**候选 7（真密码）**
                 * 解出了 `2 个文件 / 共 10489352 字节` ⇒ 老判据（"密码已证实 ⇒ 数据坏了"）当场落**损坏**
                 * ⇒ 整层判损坏 ⇒ **整单失败、连这一片都被丢掉** ⇒ 那一组永远缺第 3 片 ⇒ ①「内容物解出来」红。
                 * 而事实是：**这一组不全**（手上只有第 3 卷）—— 引擎当然解不动它的数据，
                 * 这不是"包坏了"，是"组还没凑齐"。</para>
                 *
                 * <para><b>判据（只读事实）</b>：① 这一份**是分卷组的一员**；② 同组入口**不在它旁边**
                 * （同目录里没有"同族同基名、且自己不是后续卷"的那一份）。
                 * 两条同时成立 ⇒ 把这一层的停因落成 <see cref="RecursionStopReason.MissingVolume"/>
                 * （= 缺卷，与第 0 层那条入口路**同一个出口**：记下这一片、交给跨链/批末那一套去凑）。</para>
                 *
                 * <para>⚠ 2026-10-13（困难批 `run-4444`，运行期定案）：① 这一条原来**只读名字**
                 * （<see cref="FileNameHelper.IsVolumeContinuationPart"/>）⇒ 名字被伪装过头的那一片
                 * （夹具 `dbg-7c44505e` 里是 `bbb-L2.jpg`：后缀整个被换成 `.jpg`）**落不进这一档**，
                 * 于是同一件事走了另一条结论：那一层落「压缩包损坏」⇒ 这一单落「解压失败」+
                 * **已经解出来的那一片被连工作区一起扔掉** ⇒ 那一组永远缺片。
                 * 逐字现场：`第 2 层：bbb-L2.jpg：文件损坏：密码已经证实是对的（这一趟真的解出了 2 个文件 /
                 * 共 10489352 字节）…` ⇒ `bbb-W2-1.rar：已完成 2 层，停在第 3 层，原因：压缩包损坏` ⇒
                 * `产物校验未通过：输出目录是空目录，没有产物`。**根因是"组不全"而不是"包坏了"** ——
                 * 手上只有这一卷，引擎当然读不到后面那些卷里的数据。
                 * ⇒ 补一条**内容证据**（底层识别那张卡的唯一出口）：`Membership == VolumeMember`
                 * 就是"内容自证我是分卷组的一员"（RAR 的 Volume 位 / RAR4 首卷标记 / 跨盘 ZIP 盘号）。
                 * ⛔ **判据没有放宽到"判不出"**：`Unknown` 照旧走引擎给的那一档（`ProvablyNotVolumeMember`
                 * 更是明确排除）。</para>
                 *
                 * <para>⛔ **不动"开不开"**（那一格试过一版"不打开"，把真机形状守门打红 ⇒ 已撤回）：
                 * 候选照旧全试、动作一个字不改，只把**结论**从"损坏"改成"缺卷"。
                 * ⛔ 判不出（目录读不了 / 名字解析不出 / 内容读不出）⇒ 照旧走引擎给的那一档。</para>
                 */
                if (!result.Success && IsPieceOfIncompleteGroup(archivePath))
                {
                    Log(
                        "WARN",
                        $"{layerLabel}：这一片属于某个分卷组、而同组的入口包不在这一层 ⇒ "
                        + "按「缺卷」处理（⛔ 不是「压缩包损坏」）—— 片原样留着，交给批末/跨链那一套去凑。"
                        + $"（引擎原话：{PasswordMasker.Sanitize(result.Message)}）");

                    RememberUnresolvedVolumePiece(archivePath);

                    return LayerOutcome.Stop(
                        BuildLayerReport(item, result, succeededPassword: null, manifestEntries: lastListedEntries),
                        RecursionStopReason.MissingVolume);
                }

                /*
                 * 引擎说"分卷缺失"时，把**这一层打不开的那一片**记下来（跨链收卷，2026-10-05 真机第九批 CCCC）：
                 * 这一片可能正是**同批另一组**缺的那一片（真机：`111(2)_.zip` 解出来的 `111.z01`，
                 * 而 `111.rar` 那一组的末片正等着它）—— 这条链一收尾工作区就整份删掉了，
                 * 不记下来的话，批末那一站到盘上一看：缺的东西刚刚被自己扔掉。
                 */
                if (engineStop == RecursionStopReason.MissingVolume)
                {
                    RememberUnresolvedVolumePiece(archivePath);
                }

                return LayerOutcome.Stop(
                    BuildLayerReport(item, result, succeededPassword: null, manifestEntries: lastListedEntries),
                    engineStop);
            }

            if (succeededPassword == null)
            {
                /*
                 * 收尾取"走得最远的那一次"的失败（`informativeFailure`，可能为 null）——
                 * 见上面那一大段说明：最后一次候选往往只是"连门都没进去"，它会把真原因盖掉。
                 *
                 * ⚠ 两义那一档**优先**（`ambiguousPasswordFailure`）：它是"引擎自己说两种可能都有"
                 * 那次的原话，比"最后一次错密码"信息量大得多，也是结论能同时保留两种可能的唯一来源。
                 */
                ArchiveOperationResult? conclusion =
                    ambiguousPasswordFailure ?? informativeFailure ?? lastFailure;

                RecursionStopReason reason = ResolvePasswordStopReason(
                    candidates.Count - skippedCandidates,
                    passwordLedger.Tried,
                    conclusion,
                    triedAny);

                /*
                 * ===== 换引擎再试一遍（用户 2026-10-05 拍板"补"；唯一实现 PasswordEngineFallback）=====
                 *
                 * <para><b>真机现场</b>（AGENTS.md §11.5 最后一条）：同一个包、同一条密码 ——
                 * WinRAR 6.11 退出码 0、两卷原样解出；内置 7-Zip 26.03 退出码 2、<c>Wrong password</c>、
                 * 一个字节都没出来。候选循环只用 7-Zip，而 <c>WrongPassword</c> 落在"不换引擎"那一档
                 * ⇒ 程序把"这个引擎吃不下这个 ZIP"如实报成了「密码错误 / 达到上限」——一条假结论。</para>
                 *
                 * <para>⛔ **正常路径一个字不改**：循环**内部**遇到 <c>WrongPassword</c> 照旧换下一个候选、
                 * 照旧不换引擎（省时间）。而且这一档**只在**"循环跑完 + 失败是密码类 + 归档确实加密 +
                 * 本机有 WinRAR"四条同时成立时才动，用的还是**同一批候选**（<c>triedCandidates</c>）。</para>
                 *
                 * <para>位置：紧跟候选循环之后、结论组装之前。命中 ⇒ 按"这个密码是对的"走
                 * <see cref="FinishSuccessfulLayerAsync"/>（与循环里成功那一支**同一个出口**）；
                 * 没命中 ⇒ 落到下面那段**原样的**失败结论（⛔ 一个字都不改，只多一句"已经换引擎试过"）。</para>
                 */
                bool passwordClassFailure =
                    conclusion != null &&
                    (conclusion.IsWrongPassword || conclusion.IsNeedPassword || conclusion.IsPasswordOrCorrupted) &&
                    reason is RecursionStopReason.WrongPassword
                        or RecursionStopReason.PasswordAttemptsExceeded
                        or RecursionStopReason.PasswordOrCorrupted;

                PasswordFallbackTriggerFacts fallbackFacts = new()
                {
                    AlreadySucceeded = false,
                    PasswordClassFailure = passwordClassFailure,
                    ArchiveEncrypted = sawEncryptedArchive,

                    /*
                     * "候选跑完" = 该试的都试了（每层上限内）：试过的个数 ≥ min(候选数 − 被跳过的, 每层上限)。
                     * ⛔ 一条都没试（没候选 / 上限 0）不算 —— 那时兜底无事可做。
                     */
                    CandidatesFinished = triedCandidates.Count > 0
                        && reason is RecursionStopReason.WrongPassword
                            or RecursionStopReason.PasswordAttemptsExceeded
                            or RecursionStopReason.PasswordOrCorrupted,

                    UserAskedToStop = StopRequested?.Invoke() == true,
                    HasCandidates = triedCandidates.Count > 0,
                    EngineAvailable = PasswordEngineFallback.IsEngineAvailable,

                    /*
                     * ===== 主引擎写出过字节 ⇒ 不认 WinRAR 的命中（2026-10-05 实测，见那个属性的说明）=====
                     *
                     * `informativeFailure` 就是"某一次候选失败时，这一层的产物目录里已经留下了字节"
                     * （同一个事实位，见上面 `informativeFailure` 那一段）—— 它非空即"主引擎进得去这一份的
                     * 数据流"。而 WinRAR 用错密码时也会按原大小把乱码写进同一个目录（名字 / 大小 /
                     * 时间戳与真产物一样）⇒ 这一档两个引擎给不出可分辨的证据 ⇒ 判不出 ⇒ 不换引擎、结论照旧。
                     */
                    MainEngineWroteBytes = informativeFailure != null
                };

                if (PasswordEngineFallback.ShouldFallBack(fallbackFacts))
                {
                    PasswordEngineFallbackOutcome fallback = await PasswordEngineFallback
                        .TryAsync(new PasswordFallbackRequest
                        {
                            ArchivePath = archivePath,
                            Candidates = triedCandidates,
                            TargetDirectory = item.Layer.OutputPath,
                            Label = layerLabel,
                            FailedEngineName = ResolveFailedEngineName(conclusion),
                            FailedErrorType = conclusion?.DetectedErrorType,

                            // 与循环里同一个清法（⛔ 不在这里另写一份"怎么清"）。
                            ResetTarget = _ =>
                            {
                                TryResetLayerOutputDirectory(item.Layer.OutputPath);
                                return Task.CompletedTask;
                            },

                            DescribeCandidate = DescribeCandidate,
                            Log = Log,
                            MainEngine = _engine,
                            CancellationToken = cancellationToken
                        })
                        .ConfigureAwait(false);

                    if (fallback.Succeeded)
                    {
                        succeededPassword = fallback.HitPassword;

                        /*
                         * 这一层**不是主引擎解的** ⇒ 报告里不带主引擎那次的结果（`success: null` 让状态
                         * 落在既有的「解压成功」上，与直读那条近路同一种做法）。
                         * 清单照旧取"这一层列过的那一份"：列目录与密码无关，能列出来的包任何候选都列得出来；
                         * 列不出来的（`-mhe`）本来就是 Unavailable ⇒ 下游落"判不出 ⇒ 什么都不做"。
                         */
                        success = null;

                        /*
                         * 换引擎兜底解开了，主引擎之前没能列出清单 —— 现在密码已经由兜底确认，
                         * 必须拿这个密码再向主引擎要一次清单；不能把“加密头列不出”永久当成
                         * “完整性无法确认”，否则成功链里的内层过程物永远删不掉。
                         */
                        LayerManifest recoveredManifest;
                        IReadOnlyList<(string Path, long Size)> fallbackEntries =
                            PartialPublishPlanner.ToManifestEntries(fallback.ManifestListing);

                        if (fallback.ManifestListing is { Success: true })
                        {
                            // 清单与实际解压引擎同源：优先于主引擎（7z）的清单。
                            recoveredManifest = LayerManifest.From(fallback.ManifestListing);
                            lastListedEntries = fallbackEntries;
                        }
                        else
                        {
                            recoveredManifest = listedAnyCandidate && candidateManifest.Available
                                ? candidateManifest
                                : LayerManifest.Unavailable("这一层没能列出清单（换引擎兜底解开的，主引擎没列出过）");
                        }

                        if (!recoveredManifest.Available && !string.IsNullOrEmpty(fallback.HitPassword))
                        {
                            EncryptedHeaderManifestRecoveryResult recovered = await EncryptedHeaderManifestRecovery
                                .TryRelistAsync(
                                    _engine,
                                    archivePath,
                                    fallback.HitPassword,
                                    cancellationToken,
                                    message => Log("INFO", $"{layerLabel}：{message}"))
                                .ConfigureAwait(false);

                            if (recovered.Manifest.Available)
                            {
                                recoveredManifest = recovered.Manifest;
                                lastListedEntries = recovered.Entries;
                            }
                        }

                        layerManifest = recoveredManifest;

                        return await FinishSuccessfulLayerAsync(
                                item,
                                success,
                                layerManifest,
                                succeededPassword,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    _passwordFallbackNote = fallback.Note;
                }
                else if (PasswordEngineFallback.ShouldReportMissingEngine(fallbackFacts))
                {
                    /*
                     * 判据成立、本机没有 WinRAR ⇒ 不跑，**但必须说清**（用户 2026-10-05 口径：
                     * ⛔ 不许静默跳过、⛔ 不许把没跑过的兜底说成跑过了）。
                     */
                    _passwordFallbackNote = PasswordEngineFallback
                        .ReportMissingEngine(layerLabel, ResolveFailedEngineName(conclusion), Log)
                        .Note;
                }
                else if (PasswordEngineFallback.ShouldReportIndistinguishableEvidence(fallbackFacts))
                {
                    /*
                     * 判据成立、但**主引擎已经写出过字节** ⇒ 不换引擎（证据分辨不出来，
                     * 见 `PasswordFallbackTriggerFacts.MainEngineWroteBytes`）。
                     * ⛔ 什么都不做、⛔ 结论一个字都不改，只写一行如实说明（不许静默跳过）。
                     */
                    PasswordEngineFallback.ReportIndistinguishableEvidence(
                        layerLabel,
                        ResolveFailedEngineName(conclusion),
                        Log);
                }

                /*
                 * ===== 收尾那行要点名"哪几条候选没试到"（用户 2026-10-05 真机第七批，要求 ①）=====
                 *
                 * 与单层路**同一个出口**（`PasswordCandidateGap`）：候选共 12 个、每层上限 10 ⇒
                 * **第 11 条从没被试过**，只写一句「达到密码尝试上限」用户看不出是哪一条。
                 * ⛔ 只在**真被每层上限截断**时才说（`PasswordAttemptsExceeded`）——
                 * 用户点了「停止后续」时剩余候选同样没试到，但那是另一回事（循环里已经写过它）。
                 *
                 * ⚠ 序号从**循环自己的计数**接着往下数（`triedCandidates.Count + 1`）：
                 * 被跳过的空密码不占序号（循环里那行"候选 i/N"也是这么数的），
                 * 拿列表下标当序号会与日志里已经出现过的那几行对不上。
                 */
                if (reason == RecursionStopReason.PasswordAttemptsExceeded)
                {
                    int firstUntriedIndex = Math.Min(candidates.Count, lastConsumedCandidateIndex + 1);

                    PasswordCandidateGap.Gap? untriedCandidates = PasswordCandidateGap.Describe(
                        candidates,
                        firstUntriedIndex,
                        triedCandidates.Count + 1,
                        (value, ordinal) => DescribeCandidate?.Invoke(value, ordinal)
                            ?? $"尝试密码候选第 {ordinal} 项：******");

                    if (untriedCandidates.HasValue)
                    {
                        Log(
                            "WARN",
                            PasswordCandidateGap.BuildLogLine(
                                layerLabel,
                                _limits.MaxPasswordAttemptsPerLayer,
                                untriedCandidates.Value));

                        _passwordFallbackNote = _passwordFallbackNote
                            + PasswordCandidateGap.BuildConclusionSuffix(untriedCandidates.Value);
                    }
                }

                /*
                 * ===== 「文件名已加密」在递归路也要成立（2026-10-05 用户拍板）=====
                 *
                 * 现场：同一个 `-mhe` / `-hp` 的包，单层路报「文件名已加密」，而递归路
                 * （出厂默认档 = 展开所有分支）报「密码错误」—— 用户被指去翻密码本，
                 * 而他要做的是"先给它一个密码"（这个包连内容清单都读不出来）。
                 *
                 * 判据 = 四件事**同时**成立：
                 * ① 收尾本来就落在**「密码错误」**那一档（候选全试完了都不对）；
                 * ② 这一层从头到尾**一次都没列成功**；③ 引擎说的是**加密头**（结构化错误类型，⛔ 不比中文）；
                 * ④ 没出现过别的列目录错误（证据混了 ⇒ 判不出 ⇒ 退回既有口径）。
                 *
                 * ⚠ ① 是照单层路径**逐字对齐**的：那边恢复这个结论的条件是
                 * `preflightSaidEncryptedHeaders && (密码错误 || 解压失败)`，而它**刻意不覆盖**
                 * 「达到密码尝试上限」（那边原话："那是更具体的结论，不动"）—— 候选还有剩、再试就能开，
                 * 说成"文件名已加密"会把用户从"去补候选"引开。同理也不覆盖两义与损坏（各自断言了别的原因）。
                 */
                (RecursionStopReason failureReason, string? failureMessage, bool exactReported) =
                    RecursiveFailureClassifier.ApplyEncryptedHeaders(
                        reason,
                        listedAnyCandidate,
                        sawEncryptedHeadersListFailure,
                        sawOtherListFailure,
                        layerLabel,
                        encryptedHeadersListMessage,
                        (r, m) => ReportStopReason(r, m),
                        Log);

                reason = failureReason;
                string? encryptedHeadersMessage = failureMessage;
                bool encryptedHeadersConclusion = exactReported;

                if (exactReported)
                {
                    _exactStopReasonReported = true;
                }

                /*
                 * ⚠ 2026-10-13（困难批 `run-23232323` / `run-21212121`，运行期定案）：**候选试完 / 试满上限
                 * 之后还要问一次"这一份到底是什么"** —— 这是同一个出口的**第三处**（前两处：`:2150`
                 * 「密码对了但数据坏了」、`:2318`「引擎报错」）。
                 *
                 * <para><b>逐字现场</b>（`run-23232323`，第 1 层开 `bbb-L2`）：真密码在候选第 7 条上、
                 * **确实被试过了**（日志逐字 `第 0 层：bbb-W0-0.zip：开始解压，密码候选 7/9，尝试密码列表第 7 项`；
                 * 候选顺序就是**密码本顺序**，用户的密码在第 7 行 ⇒ 位置 8（含空密码那一档），在每层上限 10 之内），
                 * 可引擎照样一个字节都解不出来（手上只有这一卷）⇒ 10 条试完落
                 * `RecursionStopReason.PasswordAttemptsExceeded` ⇒ `bbb-W0-0.zip：产物校验未通过：输出目录是空目录，
                 * 没有产物` ⇒ 整单「解压失败」+ 那一片被扔掉。</para>
                 *
                 * <para>⇒ 判据与处置**逐字同前两处**（唯一出口 <see cref="IsPieceOfIncompleteGroup"/>）：
                 * 可证"这一份是某个还没凑齐的分卷组的一员" ⇒ 结论是**缺卷**（跳过语义），⛔ 不是失败；
                 * 片记下来（<see cref="RememberUnresolvedVolumePiece"/>），交给跨链/批末那一套去凑。</para>
                 *
                 * <para>⛔ 判据没有放宽：内容判不出 / 可证不是分卷成员 ⇒ 照旧走引擎给的那一档（这一处一个字不改）。</para>
                 */
                if (IsPieceOfIncompleteGroup(archivePath))
                {
                    Log(
                        "WARN",
                        $"{layerLabel}：这一片属于某个分卷组、而同组的入口包不在这一层 ⇒ "
                        + "按「缺卷」处理（⛔ 不是「解压失败」）—— 片原样留着，交给批末/跨链那一套去凑。");

                    RememberUnresolvedVolumePiece(archivePath);

                    return LayerOutcome.Stop(
                        BuildLayerReport(
                            item,
                            result: null,
                            succeededPassword: null,
                            overrideMessage: null,
                            manifestEntries: lastListedEntries,
                            overrideStatus: StatusText.VolumeMissing),
                        RecursionStopReason.MissingVolume);
                }

                RecursionLayerReport failureReport = BuildLayerReport(
                    item,
                    conclusion,
                    succeededPassword: null,
                    overrideMessage: encryptedHeadersMessage,
                    manifestEntries: lastListedEntries,
                    overrideStatus: encryptedHeadersConclusion ? StatusText.EncryptedHeaders : null);

                return LayerOutcome.Stop(failureReport, reason);
            }

            return await FinishSuccessfulLayerAsync(
                    item,
                    success,
                    layerManifest,
                    succeededPassword,
                    cancellationToken)
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
        private static bool IsGroupEntryPresentBeside(string piecePath)
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
        private static bool CandidateProductsVerified(
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
}
