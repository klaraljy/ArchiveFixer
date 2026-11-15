using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ArchiveFixer.Extraction;

/// <summary>
/// <see cref="RecursiveExtractor"/> 一次运行的**注入依赖、可配档位与跨层账本**（状态/装配）。
///
/// <para>为什么单独成类：递归核心的类型行数与成员数里有一大块只是"存放"——引擎、探测器、
/// 密码来源、上限、日志出口、源包快照检查，加上每一趟要清零的那几本账、调用方现赋的那几档开关。
/// 集中到这里之后，宿主只保留公开 API 与转发；状态的生命周期（谁在什么时候写、什么时候清）
/// 仍然逐条写在本类成员旁边，判据与不可逆动作**一个都没有**搬进来。</para>
///
/// <para>⛔ 本类不新增判据、不扫描目录、不碰盘：除 <see cref="RememberUnresolvedVolumePiece"/> /
/// <see cref="RemoveCrossBoundaryLinks"/> 两个既有出口外，它只存值。所有"该不该删 / 该不该动"
/// 的判定仍在协调器与既有唯一出口里（AGENTS.md §9.5）。</para>
///
/// <para>⚠ 一个实例只服务一个 <see cref="RecursiveExtractor"/> 实例（协调器是"一个任务一个实例"，
/// <c>ExtractionCoordinator.CreateRecursiveExtractor</c>），因此不需要额外加锁；
/// ⛔ 不把它做成静态共享状态，也不许借它把状态外泄给别的阶段。</para>
/// </summary>
internal sealed class RecursiveCoreState
{
    /// <summary>
    /// 装配注入依赖：引擎 / 探测器 / 密码来源**全部由调用方给**（递归核心不许自己 new 引擎、
    /// 不许认识 GUI）。空值口径与抽类之前逐字相同：engine、prober 为 null ⇒
    /// <see cref="ArgumentNullException"/>；passwordProvider 为 null ⇒ 退化成"没有候选"的空函数；
    /// limits 为 null ⇒ <see cref="RecursionLimits.Default"/>。
    /// </summary>
    internal RecursiveCoreState(
        IArchiveEngine engine,
        IArchiveProber prober,
        Func<string, IReadOnlyList<string>> passwordProvider,
        RecursionLimits? limits,
        Action<string, string>? log,
        Func<ArchiveTask, Task<string?>>? sourceCheck)
    {
        Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        Prober = prober ?? throw new ArgumentNullException(nameof(prober));
        PasswordProvider = passwordProvider ?? (_ => Array.Empty<string>());
        Limits = limits ?? RecursionLimits.Default;
        Log = log;
        SourceCheck = sourceCheck;
    }

    /// <summary>解压引擎（与候选循环用的是**同一个**实例；⛔ 递归核心不许自己 new 引擎）。</summary>
    internal IArchiveEngine Engine { get; }

    /// <summary>内容探测器（只读事实；⛔ 递归核心不在这里做内容识别判据）。</summary>
    internal IArchiveProber Prober { get; }

    /// <summary>
    /// passwordProvider：给定归档路径，返回按优先级排好的密码候选（**空字符串代表试空密码**）。
    /// 它由调用方注入，递归层自己不碰密码本，也不记明文。
    /// </summary>
    internal Func<string, IReadOnlyList<string>> PasswordProvider { get; }

    /// <summary>
    /// 递归上限：层数 / 累计文件数 / 累计大小 / 展开比 / 每层密码尝试数
    /// （判据的唯一消费点仍在主循环与候选循环里，本类只存值）。
    /// </summary>
    internal RecursionLimits Limits { get; }

    /// <summary>
    /// 日志出口（level, message）。为 null 时只做事、不写日志（单元测试直接 new 的场合）。
    ///
    /// <para>用委托而不是引用具体日志实现：递归核心不认识 GUI，也不该依赖 LogService ——
    /// 与引擎 / 探测器 / 密码来源全部注入是同一个理由。调用方若是多线程宿主，
    /// 得保证这个委托本身线程安全（<c>MainViewModel.AppendLog</c> 走 BeginInvoke，是安全的）。</para>
    /// </summary>
    internal Action<string, string>? Log { get; }

    /// <summary>
    /// **不变量 11 的检查口**（AGENTS.md §6 第 11 条），可空。
    ///
    /// <para>
    /// ⚠ **只在第 0 层**（用户给的源包）开工之前问一次："这一层要解的那个源文件还是原来那一份吗？"
    /// 返回非 null = 已经拦下，本层不解、整条递归停下，那句话就是这次的结论。
    /// 实现上的闸门仍是主循环里那句 <c>item.IsRoot &amp;&amp; _sourceCheck != null</c>
    /// （<see cref="RecursiveExtractor.ExtractAsync"/>）。
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
    internal Func<ArchiveTask, Task<string?>>? SourceCheck { get; }

    /// <summary>本层是否已在更精确的分支里报过停因（避免循环收尾再用概括行覆盖源码位置）。</summary>
    internal bool ExactStopReasonReported { get; set; }

    /// <summary>
    /// **本实例自己持有的**本次（最近一次）运行的工作区。
    ///
    /// 清理永远只针对这个对象，绝不按目录名或"最新目录"去扫 <c>data\work\recursive</c> ——
    /// 并发跑两个递归任务时，那种扫描式删除会把对方正在写的工作区端掉。
    /// 真正被删的是**本次运行**建出来的那一个（<see cref="RecursiveExtractor.ExtractAsync"/> 里的
    /// 局部变量，见 <c>RecursiveExtractor.FinalizeRun</c>），这个属性只是把同一个对象暴露出来
    /// 给诊断 / 测试 / "结论被调用方改写"的那条路（<see cref="RecursiveExtractor.TryCleanupCurrentWorkspace"/>）用。
    ///
    /// ⚠ 同一个实例**不适合并发跑两次 ExtractAsync**：这个属性会被后一次覆盖。
    /// 协调器是"一个任务一个实例"（<c>ExtractionCoordinator.CreateRecursiveExtractor</c>），
    /// 并发任务各有各的实例，所以互不影响。
    /// </summary>
    internal ExtractionWorkspace? CurrentWorkspace { get; set; }

    /// <summary>
    /// 空间不足模式保留「最后一层过程物」的最终根目录（协调器设成 task.OutputPath）。
    /// 非空时保留的那一份直接落在 &lt;这个目录&gt;\其余物\，活过工作区清理；
    /// 为空时退回老口径（工作区里的 kept 目录）。
    /// </summary>
    internal string? RetainedArtifactDirectory { get; set; }

    /// <summary>空间不足模式在逐层回收**之前**已经保留到最终其余物的那一份（最后发现的为准）。</summary>
    internal string RetainedProcessArtifactPath { get; set; } = string.Empty;

    /// <summary>
    /// **先测试再解压**（<c>Settings.TestBeforeExtract</c>，用户 2026-09-27 第 37 条）：
    /// 每个密码候选先让引擎只测试、通过了才真解压。
    ///
    /// <para>用途 = 把这条**旧单层路的能力**接进递归核心（迁移成"普通任务全递归"后它整条丢了，
    /// 运行期实测 `测试调用 0 次 / 解压调用 3 次`）。默认 <c>false</c> ⇒ 行为与接入之前逐字相同；
    /// 由协调器每次任务现建实例时从设置里取。</para>
    /// </summary>
    internal bool TestBeforeExtract { get; set; }

    /// <summary>
    /// 每次真正调用引擎后的观察口（result、归档路径、动作）。
    /// 由协调器接到结构化日志；⛔ 不参与任何判据，回调异常也不影响解压。
    /// </summary>
    internal Action<ArchiveOperationResult, string, string>? EngineCallObserved { get; set; }

    /// <summary>
    /// 停因判定点（reason、源码文件、行号、说明）。由协调器接到 decision.stop 事件；
    /// ⛔ 不参与判据，观察者异常也不影响解压。
    /// </summary>
    internal Action<RecursionStopReason, string, int, string, string>? StopReasonObserved { get; set; }

    /// <summary>
    /// **「内容物保留关键词」判据**（用户 2026-10-04 的新功能「内容物压缩文件不解压」）。
    ///
    /// <para>命中关键词的内层归档**不当内层归档**：不展开、也不改名（还原工序那一步也不碰它），
    /// 它就当一件普通内容物留在结果里。判据唯一出口 <see cref="ContentKeepRules"/>
    /// （包含即命中、大小写不敏感、⛔ 不做通配 / 正则）。</para>
    ///
    /// <para>由调用方按设置当场赋值（与 <see cref="RecursionLimits"/> 同一个理由：改完设置不重启也要生效）。
    /// 默认 <see cref="ContentKeepRules.Empty"/> = 一个关键词都没有 = 行为与以前**逐字相同**。</para>
    /// </summary>
    internal ContentKeepRules KeepRules { get; set; } = ContentKeepRules.Empty;

    /// <summary>
    /// **递归路「逐层回收」的落点**（用户 2026-10-05：「这个我不是说了要同步吗，你当我放屁呢」）。
    ///
    /// <para><b>为什么要有它</b>：AGENTS.md §11.3 早就写定「续解链每一层『定稿 + 输出校验通过 +
    /// 未取消 + 可证完整』之后当场按『删除操作』处理这一层的过程物；最外层源包只留到链尾」——
    /// 但那条实现只接在**轮次续解**那条路上（层是**任务**），而「展开所有分支」那条链是
    /// **一个任务**内由递归核心展开的（层是**工作区目录**、不是任务）⇒ 递归路没接，于是 4 层链的
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
    internal Action<RecursiveLayerReclaimRequest>? LayerReclaim { get; set; }

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
    /// 这条链**最外层源包**在哪（绝对路径；每次 <see cref="RecursiveExtractor.ExtractAsync"/>
    /// 开始时按调用方给的任务定下来）。
    ///
    /// <para>唯一消费点是"跨盘 ZIP 末片在手、其余几片散在源目录里"那一档：它的**找卷窗口**必须从
    /// **用户导入进来的那个位置**算起（用户 2026-10-05 口径：「你要以一开始的分卷文件为准，不要以 001
    /// 为准，否则这时的第一层父文件夹就探测不到」）—— 窗口是既有出口
    /// <see cref="VolumeNameRepair.EnumerateVolumeCandidatesNearby"/> 算的（自己这一层 + 子目录递归 +
    /// 第一层父文件夹这一家），⛔ 不扫全盘、⛔ 不递归到祖父及以上。</para>
    /// </summary>
    internal string RootSourcePath { get; set; } = string.Empty;

    /// <summary>
    /// 上面那个窗口里那批候选的缓存槽（懒算实现在宿主
    /// <see cref="RecursiveExtractor.RootSourceCandidates"/>；每一趟开始时由
    /// <c>RecursiveExtractor.ExtractAsync.cs</c> 的 <c>BeginRunVolumeWindow</c> 置空）。
    /// </summary>
    internal IReadOnlyList<VolumeCandidate>? RootSourceCandidatesCache { get; set; }

    /// <summary>
    /// **这一趟为"跨盘 ZIP 末片"建出来的临时链接**（绝对路径）。
    ///
    /// <para><b>必须删掉，否则它们会被当成内容物发布出去</b>（2026-10-05 真机当场逮到）：链接就建在
    /// 那一层的产物目录里（7-Zip 只认"入口旁边那几片"），而发布侧搬的是"这一层产物目录里的文件" ⇒
    /// 用户目录里凭空多出三个 200 MiB 的同名副本；更糟的是它们会让「半套分卷」闸门以为这一组被拆开了，
    /// 于是**逐层回收**与**链尾其余物处理**全都拦下（真机：源包留在其余物里没删、诊断写着"其余物没有处理"）。
    /// ⇒ 这一层一跑完（成功或失败）就删；<see cref="RecursiveExtractor.ExtractAsync"/> 收尾再兜一次。</para>
    ///
    /// <para>⛔ 删的只是**我们自己刚建的那几个名字**（硬链接：删名字不动数据），源目录里的源片一个字节不动。</para>
    /// </summary>
    internal List<string> CrossBoundaryLinks { get; } = new();

    /// <summary>
    /// 这一趟**被这条链借来用过的用户源片**（绝对路径）—— 用于"这一组的内容已由谁解出"的记账
    /// （真机第八批：三片外壳那一单在批末还报「分卷缺失」，可内容其实已经解出来了）。
    /// </summary>
    internal List<string> ConsumedVolumeSources { get; } = new();

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
    internal List<string> UnresolvedVolumePieces { get; } = new();

    /// <summary>
    /// **这一趟打不开的那几片，抢救到哪个目录**（调用方在跑之前给；⛔ 空 = 不抢救）。
    ///
    /// <para>真机 2026-10-06：那一片（`111.zip`）是下一轮那一组的入口包，而递归核心收尾会把自己的工作区
    /// 清掉 ⇒ 协调器拿到结论时它已经不在了 ⇒ 整条"入口包留在它自己那条链里"的路都断了。
    /// ⇒ 调用方把它自己的**暂存目录**传进来，本类在**记下那片的同时**复制一份过去
    /// （见 <see cref="RememberUnresolvedVolumePiece"/>）。⛔ 只复制、不改名、不删原件。</para>
    /// </summary>
    internal string? UnresolvedPieceRescueDirectory { get; set; }

    /// <summary>
    /// 上一次运行留下、已被本次续跑取代的工作区（多分支询问 → 用户确认继续这一条路）。
    ///
    /// 为什么是**列表**而不是一个槽位：一个实例上可能出现"询问 → 续跑"这样的多次运行，
    /// 单槽位会在后一次续跑时把前一份**悄悄忘掉**——而那一份往往就是几百 MB。
    /// 这里只负责"记着"；清理时机只有一个：某次运行**成功**收尾时（见
    /// <c>RecursiveExtractor.FinalizeRun</c>）。
    ///
    /// 换归档（不是同一个包的续跑）时整份清空：那些工作区属于**别的**任务，
    /// 不能因为后来某个任务成功就被删掉 —— 它们的状态是"部分完成，要保留"。
    /// </summary>
    internal List<ExtractionWorkspace> SupersededWorkspaces { get; } = new();

    /// <summary>最近一次运行解的是哪个归档。用来判断"这一次是不是同一个归档的续跑"。</summary>
    internal string LastRunArchivePath { get; set; } = string.Empty;

    /// <summary>
    /// **详细日志档**（⑥设置 →「详细日志（排查用）」，默认关）。
    ///
    /// <para>为什么递归核心要知道它（用户 2026-09-27：「开了更详细的日志选项怎么还是这么简单」）：
    /// 递归路径以前**连一条候选日志都没有** —— 真机那次 13 分钟走的正是这条路，
    /// 日志里连"试了几个候选"都看不出来。打开详细日志后，每个候选一条 INFO、
    /// 每层开工一条 INFO，与单层路径同一套措辞（见 <see cref="RecursiveExtractor.VerboseLog"/> 的说明）。</para>
    ///
    /// <para>⚠ 候选日志里**只有脱敏占位符**：本类拿到的 passwordProvider 只给值，
    /// 它自己也不知道来源，所以描述由调用方注入（见 <see cref="DescribeCandidate"/>）。</para>
    /// </summary>
    internal bool VerboseLog { get; set; }

    /// <summary>
    /// 把一个密码候选描述成"能写进日志的那半句"（**必须已脱敏**）。
    ///
    /// <para>由调用方注入而不是本类自己拼：候选的来源（空密码 / 统一密码 / 密码本第 N 项…）
    /// 只有 <c>PasswordService</c> 知道，递归核心只拿到值。注入方给的就是单层路径
    /// 用的那一份 <c>BuildTryPasswordLogText</c>，于是两处口径**逐字一致**。</para>
    /// </summary>
    internal Func<string, int, string>? DescribeCandidate { get; set; }

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
    /// <para>唯一调用点 = <see cref="RecursiveExtractor.FinishSuccessfulLayerAsync"/>（三种成功路的
    /// **共同出口**：主引擎循环 / 尾部归档直读 / 换引擎兜底），所以直读与兜底两条路也照样报。</para>
    /// </summary>
    internal Action<string>? PasswordVerified { get; set; }

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
    internal bool ReportDangerousEntries { get; set; }

    /// <summary>
    /// 未凑齐分卷片的记账与抢救：转调 <see cref="RecursiveWorkspaceArtifacts.RememberUnresolvedPiece"/>。
    /// 账本、救援目录与"只复制不改名不删原件"的口径全部原样。
    /// </summary>
    internal void RememberUnresolvedVolumePiece(string? path)
        => RecursiveWorkspaceArtifacts.RememberUnresolvedPiece(
            path,
            UnresolvedVolumePieces,
            UnresolvedPieceRescueDirectory);

    /// <summary>
    /// 跨盘 ZIP 临时链名的清理：转调 <see cref="RecursiveWorkspaceArtifacts.RemoveCrossBoundaryLinks"/>。
    /// 只删本工作区里我们自己建的那几个名字，越界一律不碰。
    /// </summary>
    internal void RemoveCrossBoundaryLinks()
        => RecursiveWorkspaceArtifacts.RemoveCrossBoundaryLinks(CurrentWorkspace, CrossBoundaryLinks);
}
