using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Helpers;

namespace ArchiveFixer.Detection
{
    /// <summary>
    /// 一条**可核查证据**的种类。
    ///
    /// <para>分卷组装的全部判据只有这六条，判据实现只此一处（AGENTS.md §9.5「同一件事只有一个出口」）。</para>
    /// </summary>
    public enum VolumeEvidenceKind
    {
        /// <summary>基名相等（去卷标记、去归档后缀之后名字相同）。</summary>
        BaseName = 0,

        /// <summary>卷号从 1 起连续、没有洞（<c>.00N</c> / <c>.z0N</c> / <c>.rNN</c> / <c>.partNN</c>）。</summary>
        Sequence = 1,

        /// <summary>体积关系：除最后一卷外体积相等（裸切），最后一卷 ≤ 其余。</summary>
        SizePattern = 2,

        /// <summary>物理同一性：同一目录 / 同一卷根；同 FileId ⇒ 同一份（硬链接或同一个文件的两个名字）。</summary>
        PhysicalIdentity = 3,

        /// <summary>没有卷号的候选按**体积 + 目录内位置/时间**插进序列（**推定**，置信度低一档）。</summary>
        PositionInference = 4,

        /// <summary>硬链接试开（最高权重、最终判据）。</summary>
        TrialOpen = 5,
    }

    /// <summary>证据权重。落结论时**只按权重与是否成立**，不按"名字像不像"。</summary>
    public enum VolumeEvidenceWeight
    {
        /// <summary>弱证据：只够"报出来"，不够"据此下破坏性结论"。</summary>
        Weak = 0,

        /// <summary>强证据：名字 / 体积 / 位置 / 物理同一性这一档。</summary>
        Strong = 1,

        /// <summary>决定性证据：引擎试开成立或不成立 —— 它一票定案。</summary>
        Decisive = 2,
    }

    /// <summary>一条证据在这次判定里的结论。<see cref="Unknown"/> ≠ <see cref="NotSatisfied"/>（没取到 ≠ 不成立）。</summary>
    public enum VolumeEvidenceOutcome
    {
        /// <summary>没取到 / 没试（例如没有可用引擎、跨卷做不了硬链接）。</summary>
        Unknown = 0,

        /// <summary>成立。</summary>
        Satisfied = 1,

        /// <summary>不成立（**是事实，不是猜测**）。</summary>
        NotSatisfied = 2,
    }

    /// <summary>一条证据（证据表里的一行）。</summary>
    public sealed class VolumeEvidence
    {
        /// <summary>哪一条证据。</summary>
        public VolumeEvidenceKind Kind { get; init; }

        /// <summary>权重。</summary>
        public VolumeEvidenceWeight Weight { get; init; }

        /// <summary>这次的结论。</summary>
        public VolumeEvidenceOutcome Outcome { get; init; }

        /// <summary>证据名（一行，给用户看的）。</summary>
        public string Title { get; init; } = string.Empty;

        /// <summary>怎么取的 + 取到了什么（尽量带数字，AGENTS.md §9.5「排错先量化」）。</summary>
        public string Detail { get; init; } = string.Empty;
    }

    /// <summary>
    /// **对外唯一的那一个结论**。四档，没有第五档；⛔ 不许再往外吐"可能缺卷"这类没定义的中间态。
    /// </summary>
    public enum VolumeGroupVerdict
    {
        /// <summary>完整（有证据 —— 名字序号齐全，或试开成立）。</summary>
        Complete = 0,

        /// <summary>不完整·缺第 N 卷（**有证据**：卷号上有洞，且这一洞没有任何可采信的填充物）。</summary>
        IncompleteMissingVolume = 1,

        /// <summary>不完整·疑缺卷（**弱证据**：只有"按体积/位置推定"的候选，或体积规律对不上）。⛔ 只报不删。</summary>
        IncompleteSuspected = 2,

        /// <summary>判不出（名字对不上、目录读不到、关键事实取不到）。⛔ 不删源、不移动源。</summary>
        Undetermined = 3,
    }

    /// <summary>组里已定位的一卷。</summary>
    public sealed class ResolvedVolume
    {
        /// <summary>完整路径。</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>卷序（1 起）。</summary>
        public int Index { get; init; }

        /// <summary>字节数（量不出时 &lt;= 0）。</summary>
        public long Size { get; init; }

        /// <summary>
        /// 它的**名字里带不带卷号**。false = 靠体积/位置推定或靠试开才认进来的
        /// （例如无后缀的 <c>111</c>、被改成 <c>一只顶美.z删除ip</c> 的那一卷、伪装成 <c>.mp4</c> 的续卷）。
        /// </summary>
        public bool NameCarriesVolumeNumber { get; init; }

        /// <summary>这一卷是靠"按位置推定"插进来的（置信度低一档）。</summary>
        public bool InferredByPosition { get; init; }

        /// <summary>这一卷是靠试开才认进来的。</summary>
        public bool ConfirmedByTrialOpen { get; init; }
    }

    /// <summary>
    /// 一次分卷组装判定的**全部输出**。对外只读它一个对象 —— 三处消费方（解前预检 / 删除闸门 /
    /// 可删残留分类）拿到的都是同一份结论，⛔ 不许各自再判一遍。
    /// </summary>
    public sealed class VolumeGroupResolution
    {
        /// <summary>唯一结论。</summary>
        public VolumeGroupVerdict Verdict { get; init; }

        /// <summary>这次是围着哪个文件问的（原样回填，方便调用方对上号）。</summary>
        public string AnchorPath { get; init; } = string.Empty;

        /// <summary>组基名（<c>111.7z</c> / <c>一只顶美</c> / <c>x</c>）；判不出时为空串。</summary>
        public string BaseName { get; init; } = string.Empty;

        /// <summary>已定位的卷（按卷序升序；含靠推定/试开认进来的那些）。</summary>
        public IReadOnlyList<ResolvedVolume> Volumes { get; init; } = Array.Empty<ResolvedVolume>();

        /// <summary>**有证据**的缺卷名（只有文件名）。弱证据的疑缺不写在这里。</summary>
        public IReadOnlyList<string> MissingVolumeNames { get; init; } = Array.Empty<string>();

        /// <summary>**按位置推定**的候选（弱证据，只报不采信），形如「<c>111</c> 推定是第 2 卷」。</summary>
        public IReadOnlyList<string> PositionInferredNotes { get; init; } = Array.Empty<string>();

        /// <summary>证据表（每条证据一行；判不出时也要有，用来说清"依据是什么"）。</summary>
        public IReadOnlyList<VolumeEvidence> Evidence { get; init; } = Array.Empty<VolumeEvidence>();

        /// <summary>给人看的一句话结论 + 依据。</summary>
        public string Reason { get; init; } = string.Empty;

        /// <summary>
        /// **属于这一组的全部文件**（含推定进来的那些）。
        ///
        /// <para>⛔ 这条清单就是"永远不许进可删残留名单"的那个名单 —— 25 GB 那次事故的根因正是
        /// 剩下 5 卷每一卷单看都像"待续解的过程物"。</para>
        /// </summary>
        public IReadOnlyList<string> GroupFilePaths { get; init; } = Array.Empty<string>();

        /// <summary>
        /// **同一卷的名字变体**（`222.zip` 与 `222.zi删除p` 是同一个逻辑卷的两个名字）。
        ///
        /// <para>它们也算组成员（<see cref="IsGroupMember"/> 认它们），但**不占独立卷号** ——
        /// 所以它们既不进 <see cref="Volumes"/>、也不构成"缺了一卷"的证据。
        /// 判据见 <c>ResolveGroup</c> 里"名字变体"那一段。</para>
        /// </summary>
        public IReadOnlyList<string> VariantFilePaths { get; init; } = Array.Empty<string>();

        /// <summary>组里有没有"名字不标准"的成员（靠推定/试开才认进来的，或卷名里夹了垃圾）。</summary>
        public bool HasRenamedVolume { get; init; }

        /// <summary>
        /// **这一组可不可以进"可删的其余物"**（= 允许删除 / 搬走的唯一资格）。
        ///
        /// <para>只有**两条都成立**才行：① 结论是 <see cref="VolumeGroupVerdict.Complete"/>；
        /// ② 每一卷的名字都是 7-Zip 按原名认得的标准卷名（名字不标准 ⇒ 按原名根本打不开 ⇒ 删了就没了）。</para>
        ///
        /// <para>⛔ 其余一切情形（含"判不出"与"弱证据"）都在这里返回 false ——
        /// 兜底一律落在"什么都不做"那一档（AGENTS.md §9.5）。</para>
        /// </summary>
        public bool CanEnterDeletableRestItems { get; init; }

        /// <summary>这个文件是不是这一组的成员（同一份结论，供调用方逐个文件问）。</summary>
        public bool IsGroupMember(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            string full = SafePathHelper.GetFullPathSafe(filePath);

            if (full.Length == 0)
            {
                return false;
            }

            foreach (string member in GroupFilePaths)
            {
                if (string.Equals(member, full, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>目录里的一个文件（判定器的输入事实：路径 + 体积 + 时间 + 枚举次序）。</summary>
    public sealed class VolumeGroupEntry
    {
        /// <summary>完整路径。</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>字节数（量不出时 &lt;= 0）。</summary>
        public long Size { get; init; }

        /// <summary>最后写入时间（用于"与邻居的相对位置"这一条弱证据）。</summary>
        public DateTime LastWriteTimeUtc { get; init; }

        /// <summary>目录枚举次序（0 起；目录自己的顺序不保证稳定，所以只当弱证据）。</summary>
        public int OrderIndex { get; init; }
    }

    /// <summary>一次判定的输入。</summary>
    public sealed class VolumeGroupQuery
    {
        /// <summary>围着哪个文件问（组里的任意一个，包括"改了名的那一个"）。</summary>
        public string AnchorPath { get; init; } = string.Empty;

        /// <summary>
        /// 同目录的事实清单。
        ///
        /// <para>为 null 时判定器**自己列目录**；给了就只用给的那一份（单测靠它摆脱真实磁盘的随机性）。
        /// 给了清单就不再碰文件系统（除了试开那一步）。</para>
        /// </summary>
        public IReadOnlyList<VolumeGroupEntry>? DirectoryEntries { get; init; }

        /// <summary>
        /// 临时物落点：<c>&lt;目标目录&gt;\.ArchiveFixer.work</c>（用户 2026-09-30 口径）。
        ///
        /// <para>⛔ <b>空 = 不试开</b>（不是"另找个地方试"）：工作区只准设在解压的地方 ——
        /// 拿不到目标目录（例如改名那条路）或者它与第一卷不在同一个卷上（硬链接不能跨卷）时，
        /// 试开这一档**一次都不做**，结论如实降成"判不出"。</para>
        /// </summary>
        public string WorkRootDirectory { get; init; } = string.Empty;

        /// <summary>允不允许试开（导入期这种"只想保护、不想调引擎"的场合传 false）。</summary>
        public bool AllowTrialOpen { get; init; } = true;
    }

    /// <summary>
    /// **没试开**的结构化原因。⛔ 消费方只许读这个枚举，不许去比 <see cref="VolumeTrialOutcome.Reason"/> 的中文。
    ///
    /// <para>为什么要分档：只有 <see cref="CrossVolume"/> 会**改变结论**（跨盘 ⇒ 判不出）；
    /// 其余几档都是"这一次没取到证据"，结论照旧由前五条证据给（"没试 ≠ 不成立"）。</para>
    /// </summary>
    public enum VolumeTrialSkipReason
    {
        /// <summary>试过了（或者根本没走到试开这条证据）。</summary>
        None = 0,

        /// <summary>没有可用的解压引擎。</summary>
        NoEngine = 1,

        /// <summary>凑不出可试的顺序（卷号上有洞 —— 缺一卷 7-Zip 必然报 Missing volume）。</summary>
        NoOrderings = 2,

        /// <summary>拿不到合法的试开目录 / 建不出试开目录。</summary>
        NoProbeRoot = 3,

        /// <summary>
        /// **跨盘**：源文件与工作区根不在同一个卷上 —— 而硬链接不能跨卷。
        ///
        /// <para>这一档**必须如实降成"判不出"**：⛔ 不许退到源卷根偷偷开工作区去试
        /// （工作区只准设在解压的地方，在源盘开目录正是不变量 12 禁止的形态；收工删掉也一样 ——
        /// 中途崩掉就在用户盘上留垃圾）。判不出 ⇒ 不删源、不移动源，安全结论一个字不变。</para>
        /// </summary>
        CrossVolume = 4,

        /// <summary>别的意外（第一卷不在、试开请求为空等）。</summary>
        Other = 5,
    }

    /// <summary>一次试开的结论（由 <c>Extraction/VolumeProbeVerifier</c> 落地：硬链接 + 引擎列目录）。</summary>
    public sealed class VolumeTrialOutcome
    {
        /// <summary>顺序成立（引擎真的读到了里面的条目，或认出这一组是"文件名也加密"的归档）。</summary>
        public bool Confirmed { get; init; }

        /// <summary>真的试过（false = 没有可用引擎 / 做不了硬链接 —— 这一档是"没试"，不是"不成立"）。</summary>
        public bool Attempted { get; init; }

        /// <summary>没试的结构化原因（跨盘那一档会改变结论，见 <see cref="VolumeTrialSkipReason"/>）。</summary>
        public VolumeTrialSkipReason SkipReason { get; init; } = VolumeTrialSkipReason.None;

        /// <summary>成立但读不出清单（<c>-mhe</c> / <c>-hp</c>，要正确密码才列得出条目）。</summary>
        public bool NeedsPassword { get; init; }

        /// <summary>成立时的完整卷序（第 1 卷在第一位）。</summary>
        public IReadOnlyList<string> OrderedVolumePaths { get; init; } = Array.Empty<string>();

        /// <summary>试了几种排列。</summary>
        public int Attempts { get; init; }

        /// <summary>给人看的一句话。</summary>
        public string Reason { get; init; } = string.Empty;
    }

    /// <summary>试开请求。</summary>
    public sealed class VolumeTrialRequest
    {
        /// <summary>第 1 卷（按推定的顺序）。</summary>
        public string FirstVolumePath { get; init; } = string.Empty;

        /// <summary>后续卷的几种候选排列（每一组都不含第 1 卷）。</summary>
        public IReadOnlyList<IReadOnlyList<VolumeCandidate>> Orderings { get; init; } =
            Array.Empty<IReadOnlyList<VolumeCandidate>>();

        /// <summary>临时物优先落点（空 = 由试开器自己定）。</summary>
        public string PreferredWorkRootDirectory { get; init; } = string.Empty;
    }

    /// <summary>试开端口（Detection 不引用 Extraction，所以这里只定义端口，实现由调用方注入）。</summary>
    public delegate Task<VolumeTrialOutcome> VolumeTrialOpen(
        VolumeTrialRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// **分卷组装判定器 —— 全项目唯一出口**（用户 2026-09-30 真机事故后要求）。
    ///
    /// <para><b>为什么不能"只看内容"</b>：7z 分卷是**裸切**，元数据只在**最后一卷末尾**，
    /// 第 2 卷起没有任何格式标记。所以"这一堆文件是不是同一次分卷切出来的"只能靠**证据**
    /// （名字 / 体积 / 位置 / 物理同一性 / 试开）回答。本类就是把这组证据收在一起、
    /// 并且**只吐一个结论**的地方。</para>
    ///
    /// <para><b>六条证据与权重</b>（逐条实现，逐条进 <see cref="VolumeGroupResolution.Evidence"/>）：</para>
    /// <list type="bullet">
    /// <item><description><b>基名相等（强）</b>：去卷标记 + 去归档后缀之后名字相同 ——
    /// 「后缀可能不同但是名字一定相同」就是这一条：<c>111.7z.001</c> 的基名段是 <c>111</c>，
    /// 无后缀的 <c>111</c> 也是 <c>111</c>；<c>一只顶美.z删除ip</c> 的基名段是 <c>一只顶美</c>。</description></item>
    /// <item><description><b>卷号连续（强）</b>：转调 <see cref="VolumeGroupDetector"/>（卷名判据的唯一出口），
    /// 洞就是"有证据的缺卷"。</description></item>
    /// <item><description><b>体积关系（强）</b>：除最后一卷外等大、最后一卷 ≤ 其余。⛔ 只对
    /// <c>.00N</c> / <c>.partNN</c> 这两个真·裸切族当硬证据；<c>.z01</c> / <c>.r00</c> 族的首卷天然可能更短，
    /// 那一档只记录、不下结论。</description></item>
    /// <item><description><b>物理同一性（强）</b>：同一目录 / 同一卷根（跨盘拼不出来）；同 FileId ⇒
    /// 一定是同一份，用来把"同一份被数成两卷"挡掉。</description></item>
    /// <item><description><b>位置推定（弱）</b>：没有卷号的候选按体积 + 目录内位置/时间插进序列，
    /// 一律标成"推定"，⛔ 不许拿它单独升级成"完整"。</description></item>
    /// <item><description><b>试开确认（决定性）</b>：按推定的顺序在 <c>.ArchiveFixer.work</c> 里做硬链接
    /// 交给引擎试开：列得出清单 / 报"文件名也加密" ⇒ 组对；报 <c>Missing volume</c> / 打不开 ⇒ 组错。
    /// ⛔ 只读，源文件一个字节都不动。</description></item>
    /// </list>
    ///
    /// <para><b>结论怎么落</b>（可复算，见 <c>docs/分卷组装算法.md</c>）：</para>
    /// <list type="number">
    /// <item><description>试开成立 ⇒ <see cref="VolumeGroupVerdict.Complete"/>（一票定案）。试开试过但不成立 ⇒
    /// 组错；"靠名字就判完整"的那一档因此**降级**，有洞就是"有证据的缺卷"，否则"疑缺"。</description></item>
    /// <item><description>没试开（没引擎 / 跨卷 / 导入期 / 定稿闸门）⇒ 只有在
    /// <b>同目录里一个「基名段相同、体积不大于满卷」的候选都没有</b>时才敢说
    /// <see cref="VolumeGroupVerdict.Complete"/>（名字序号齐全 + 每一卷都是标准卷名）。</description></item>
    /// <item><description>洞在**中间槽**（后面还有卷号更大的卷）⇒ **有证据的缺卷** —— 这一档的规范名带卷号，
    /// 一个没有卷号的文件**不可能**被断言成"就是那一卷"（真案 ①：<c>111.7z.001</c> + <c>111</c> + <c>111.7z.003</c>）。</description></item>
    /// <item><description>洞在**边界槽**（<c>.z01</c>/<c>.r00</c> 族那个没有卷号的本体位），或**候选比名字序号还多**
    /// （疑似续卷 / 末卷被改名）⇒ 只有弱证据 ⇒ <see cref="VolumeGroupVerdict.IncompleteSuspected"/>；试开成立才升到"完整"
    /// （真案 ②：末卷被改成 <c>一只顶美.z删除ip</c>；真案 ③：续卷伪装成 <c>.mp4</c>）。</description></item>
    /// <item><description>其余 ⇒ <see cref="VolumeGroupVerdict.Undetermined"/>，⛔ 判不出 ⇒ 不删源、不移动源。</description></item>
    /// </list>
    ///
    /// <para>本类不引用 WPF；<see cref="VolumeGroupQuery.AllowTrialOpen"/> 为 false 时**一次引擎调用都不做**
    /// （导入期的保护名单要的就是这个）。</para>
    /// </summary>
    public sealed class VolumeGroupResolver
    {
        /// <summary>试开最多试几种排列（试开一次是一次进程调用，不能让它在几十种排列上跑）。</summary>
        public const int MaxTrialOrderings = 8;

        private readonly VolumeTrialOpen? _trialOpen;

        /// <summary>
        /// </summary>
        /// <param name="trialOpen">
        /// 试开端口。传 null ⇒ 判定器只做"名字 + 体积 + 位置 + 物理同一性"这四档（**同步、零引擎调用**），
        /// 试开那一条证据记为"没试"。删除闸门与导入期保护名单就走这一档。
        /// </param>
        public VolumeGroupResolver(VolumeTrialOpen? trialOpen = null)
        {
            _trialOpen = trialOpen;
        }

        /// <summary>
        /// **同步判定**（不试开）。用在哪：定稿时的删除闸门、导入期的保护名单 —— 这两处要么在同步代码里，
        /// 要么明确"不许调引擎"。
        /// </summary>
        public VolumeGroupResolution Resolve(VolumeGroupQuery? query)
        {
            if (query == null || string.IsNullOrWhiteSpace(query.AnchorPath))
            {
                return Undetermined(string.Empty, "没有可判定的输入");
            }

            string anchor = SafePathHelper.GetFullPathSafe(query.AnchorPath);

            if (anchor.Length == 0)
            {
                return Undetermined(string.Empty, "路径形状不合法，取不到名字");
            }

            IReadOnlyList<VolumeGroupEntry> entries = query.DirectoryEntries ?? EnumerateDirectory(anchor);

            if (entries.Count == 0)
            {
                return Undetermined(anchor, "同目录里读不到任何文件（目录不在，或者没有读权限）");
            }

            // ── 命名归组：转调卷名判据的唯一出口 VolumeGroupDetector ──────────────────────
            var candidates = new List<VolumeCandidate>(entries.Count);

            foreach (VolumeGroupEntry entry in entries)
            {
                candidates.Add(new VolumeCandidate { Path = entry.Path, Size = entry.Size });
            }

            IReadOnlyList<VolumeGroup> groups = VolumeGroupDetector.Group(candidates);
            VolumeGroup? group = FindAnchorGroup(groups, anchor);

            if (group == null)
            {
                return Undetermined(
                    anchor,
                    $"{Path.GetFileName(anchor)} 的名字里没有卷标记，同目录也没有和它同基名的分卷组 —— "
                    + "没有证据能说明它属于哪一组（判不出：不删、不移动、不改名）");
            }

            return ResolveGroup(anchor, entries, group);
        }

        /// <summary>
        /// **带试开的判定**。用在哪：解前预检 —— 它要回答的正是"缺卷就别开解，缺哪一卷、为什么这样判"。
        /// 试开端口没注入 / <see cref="VolumeGroupQuery.AllowTrialOpen"/> 为 false 时等价于 <see cref="Resolve"/>。
        /// </summary>
        public async Task<VolumeGroupResolution> ResolveAsync(
            VolumeGroupQuery? query,
            CancellationToken cancellationToken = default)
        {
            VolumeGroupResolution baseline = Resolve(query);

            if (_trialOpen == null || query == null || !query.AllowTrialOpen ||
                baseline.Verdict == VolumeGroupVerdict.Undetermined)
            {
                // 连组都没认出来：没什么可试的（硬链接一组叫不上名字的文件没有意义）。
                return baseline;
            }

            if (baseline.Volumes.Count == 0 || baseline.Volumes[0].Index != 1)
            {
                return AddRow(baseline, Row(
                    VolumeEvidenceKind.TrialOpen,
                    VolumeEvidenceWeight.Decisive,
                    VolumeEvidenceOutcome.Unknown,
                    "试开确认",
                    "第 1 卷还没定位出来，凑不出可试的完整顺序"));
            }

            List<List<VolumeCandidate>> orderings = BuildTrialOrderings(baseline);

            if (orderings.Count == 0)
            {
                return AddRow(baseline, Row(
                    VolumeEvidenceKind.TrialOpen,
                    VolumeEvidenceWeight.Decisive,
                    VolumeEvidenceOutcome.Unknown,
                    "试开确认",
                    "按名字与推定凑不出「1 到 N 一卷不缺」的顺序，没法试开"));
            }

            VolumeTrialOutcome trial;

            try
            {
                trial = await _trialOpen(
                        new VolumeTrialRequest
                        {
                            FirstVolumePath = baseline.Volumes[0].Path,
                            Orderings = orderings,
                            PreferredWorkRootDirectory = query.WorkRootDirectory
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return AddRow(baseline, Row(
                    VolumeEvidenceKind.TrialOpen,
                    VolumeEvidenceWeight.Decisive,
                    VolumeEvidenceOutcome.Unknown,
                    "试开确认",
                    $"试开出错（{ex.GetType().Name}）—— 这一条证据取不到，结论不变"));
            }

            return trial == null ? baseline : ApplyTrialOutcome(baseline, trial);
        }

        /// <summary>把试开的结论并进基准结论（试开是决定性证据，成立就一票升级）。</summary>
        private static VolumeGroupResolution ApplyTrialOutcome(
            VolumeGroupResolution baseline,
            VolumeTrialOutcome trial)
        {
            string detail = trial.Attempted
                ? $"试了 {trial.Attempts} 种排列：{trial.Reason}"
                : IsUnconfirmable(trial.SkipReason)
                    ? $"这一条证据取不到（{DescribeSkip(trial.SkipReason)}）⇒ 结论如实降成「判不出」：{trial.Reason}"
                    : $"没试成（这一档是「没试」，不是「不成立」）：{trial.Reason}";

            VolumeEvidence row = Row(
                VolumeEvidenceKind.TrialOpen,
                VolumeEvidenceWeight.Decisive,
                !trial.Attempted
                    ? VolumeEvidenceOutcome.Unknown
                    : trial.Confirmed ? VolumeEvidenceOutcome.Satisfied : VolumeEvidenceOutcome.NotSatisfied,
                "试开确认",
                detail);

            if (!trial.Attempted)
            {
                /*
                 * ===== 试开做不了 ⇒ **判不出**（用户 2026-09-30 决定，协调者复核）=====
                 *
                 * 两档落在这里（见 <see cref="IsUnconfirmable"/>）：
                 *   · **没有工作区根**（拿不到这一单的目标目录，例如改名那条路）—— 不许另找地方开工作区；
                 *   · **跨盘**（硬链接不能跨卷）—— 不许退到源卷根去试。
                 *
                 * 于是这一档如实降级成 <see cref="VolumeGroupVerdict.Undetermined"/>：
                 *   · 组成员的清单**照旧带出去**（GroupFilePaths 不变）⇒ 下游的保护/删除闸门一个字不放松；
                 *   · CanEnterDeletableRestItems = false ⇒ 不删源、不移动源；
                 *   · 文案如实写"无法确认"，⛔ 绝不许写成"已按试开验证"。
                 */
                if (IsUnconfirmable(trial.SkipReason))
                {
                    return new VolumeGroupResolution
                    {
                        Verdict = VolumeGroupVerdict.Undetermined,
                        AnchorPath = baseline.AnchorPath,
                        BaseName = baseline.BaseName,
                        Volumes = baseline.Volumes,
                        MissingVolumeNames = Array.Empty<string>(),
                        PositionInferredNotes = baseline.PositionInferredNotes,
                        Evidence = Concat(baseline.Evidence, row),
                        Reason = "判不出：" + DescribeSkip(trial.SkipReason) + "。"
                            + "⛔ 判不出 ⇒ 不删源、不移动源、不改名；要确认就得让这一组和它的工作区"
                            + "在同一个盘上、并且有目标目录可以落工作区",
                        GroupFilePaths = baseline.GroupFilePaths,
                        HasRenamedVolume = baseline.HasRenamedVolume,
                        CanEnterDeletableRestItems = false
                    };
                }

                return AddRow(baseline, row);
            }

            if (trial.Confirmed)
            {
                // 试开成立 = 一票定案：把引擎认下来的顺序原样收进结论（含靠推定/改名认进来的那一卷）。
                return new VolumeGroupResolution
                {
                    Verdict = VolumeGroupVerdict.Complete,
                    AnchorPath = baseline.AnchorPath,
                    BaseName = baseline.BaseName,
                    Volumes = RebuildFromTrial(baseline, trial),
                    MissingVolumeNames = Array.Empty<string>(),
                    PositionInferredNotes = baseline.PositionInferredNotes,
                    Evidence = Concat(baseline.Evidence, row),
                    Reason = "完整（试开确认）：按推定的顺序硬链接试开成立 —— "
                        + (trial.NeedsPassword
                            ? "引擎认出这一组是一份「文件名也加密」的归档，给不出密码就读不出清单"
                            : "引擎真的读到了里面的条目")
                        + $"。试了 {trial.Attempts} 种排列。"
                        + (baseline.HasRenamedVolume
                            ? "⚠ 组里有名字不标准的卷，按原名交给 7-Zip 打不开（程序不改你的文件名）。"
                            : string.Empty),
                    GroupFilePaths = baseline.GroupFilePaths,
                    HasRenamedVolume = baseline.HasRenamedVolume,
                    CanEnterDeletableRestItems = Deletable(VolumeGroupVerdict.Complete, baseline.HasRenamedVolume)
                };
            }

            /*
             * 试开**试过但不成立** ⇒ 组错（用户口径："报 Missing volume / 打不开 ⇒ 组错"）。
             * 基准里"靠名字就判完整"的那一档必须因此降级 —— 试开是最高权重的最终判据，
             * 它说不行，就不许再拿名字规律说行。
             */
            bool wasComplete = baseline.Verdict == VolumeGroupVerdict.Complete;

            return new VolumeGroupResolution
            {
                Verdict = wasComplete ? VolumeGroupVerdict.IncompleteSuspected : baseline.Verdict,
                AnchorPath = baseline.AnchorPath,
                BaseName = baseline.BaseName,
                Volumes = baseline.Volumes,
                MissingVolumeNames = baseline.MissingVolumeNames,
                PositionInferredNotes = baseline.PositionInferredNotes,
                Evidence = Concat(baseline.Evidence, row),
                Reason = wasComplete
                    ? "不完整·疑缺卷（弱证据）：名字与序号本来对得上，但按推定顺序试开不成立 —— "
                        + "这一组很可能不止名字上看出来的这些卷（也可能是别的原因打不开，所以只报疑、不动它）"
                    : baseline.Reason + $"；试开也不成立（{trial.Reason}）",
                GroupFilePaths = baseline.GroupFilePaths,
                HasRenamedVolume = baseline.HasRenamedVolume,
                CanEnterDeletableRestItems = false
            };
        }

        /// <summary>把试开认下来的顺序折回 <see cref="ResolvedVolume"/>（名字这一条事实按原名重算）。</summary>
        private static IReadOnlyList<ResolvedVolume> RebuildFromTrial(
            VolumeGroupResolution baseline,
            VolumeTrialOutcome trial)
        {
            var byPath = new Dictionary<string, ResolvedVolume>(StringComparer.OrdinalIgnoreCase);

            foreach (ResolvedVolume volume in baseline.Volumes)
            {
                byPath[volume.Path] = volume;
            }

            var result = new List<ResolvedVolume>();

            foreach (string? path in trial.OrderedVolumePaths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                string full = SafePathHelper.GetFullPathSafe(path);

                if (full.Length == 0 || result.Any(v => string.Equals(v.Path, full, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (byPath.TryGetValue(full, out ResolvedVolume? known))
                {
                    result.Add(new ResolvedVolume
                    {
                        Path = known.Path,
                        Index = result.Count + 1,
                        Size = known.Size,
                        NameCarriesVolumeNumber = known.NameCarriesVolumeNumber,
                        InferredByPosition = known.InferredByPosition,
                        ConfirmedByTrialOpen = true
                    });

                    continue;
                }

                result.Add(new ResolvedVolume
                {
                    Path = full,
                    Index = result.Count + 1,
                    Size = SizeOf(full),
                    NameCarriesVolumeNumber = false,
                    InferredByPosition = false,
                    ConfirmedByTrialOpen = true
                });
            }

            return result.Count > 0 ? result : baseline.Volumes;
        }

        /// <summary>真正干活的那一段：组已经认出来了，逐条取证据、落结论。</summary>
        private static VolumeGroupResolution ResolveGroup(
            string anchor,
            IReadOnlyList<VolumeGroupEntry> entries,
            VolumeGroup group)
        {
            var evidence = new List<VolumeEvidence>();
            string baseName = group.BaseName;
            VolumeNameFamily family = ResolveFamily(group, baseName);

            var located = new List<ResolvedVolume>();
            var locatedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (VolumeCandidate volume in group.Volumes)
            {
                string full = SafePathHelper.GetFullPathSafe(volume.Path);
                int index = VolumeGroupDetector.TryGetVolumeIndex(Path.GetFileName(full)) ?? 0;

                if (index <= 0 || !locatedPaths.Add(full))
                {
                    continue;
                }

                located.Add(new ResolvedVolume
                {
                    Path = full,
                    Index = index,
                    Size = volume.Size,
                    NameCarriesVolumeNumber = true,
                    InferredByPosition = false,
                    ConfirmedByTrialOpen = false
                });
            }

            located.Sort((a, b) => a.Index.CompareTo(b.Index));

            if (located.Count == 0)
            {
                evidence.Add(Row(
                    VolumeEvidenceKind.BaseName,
                    VolumeEvidenceWeight.Strong,
                    VolumeEvidenceOutcome.Unknown,
                    "基名相等",
                    "同目录里没有一卷带着可解析的卷号"));

                return WithEvidence(
                    Undetermined(anchor, "判不出：认出了基名，但一卷的卷号都解析不出来（不删、不移动、不改名）"),
                    evidence,
                    baseName);
            }

            // ── ① 基名相等（强证据）──────────────────────────────────────────────
            string groupStem = StemOf(baseName);
            string anchorStem = StemOf(Path.GetFileName(anchor));
            bool anchorMatchesByStem = string.Equals(anchorStem, groupStem, StringComparison.OrdinalIgnoreCase);

            evidence.Add(Row(
                VolumeEvidenceKind.BaseName,
                VolumeEvidenceWeight.Strong,
                VolumeEvidenceOutcome.Satisfied,
                "基名相等",
                $"组基名 {baseName}（去归档后缀后是 {groupStem}）；锚点 {Path.GetFileName(anchor)} 的基名段是 {anchorStem}"
                + (anchorMatchesByStem ? "，与组基名段相同" : "，靠卷号归进本组")));

            // ── ② 没卷号的候选：按体积 + 位置（弱证据）────────────────────────────
            int maxFound = located[^1].Index;
            long fullSize = located[0].Size;
            var openSlots = new List<int>();

            for (int index = 1; index <= maxFound; index++)
            {
                if (!located.Any(v => v.Index == index))
                {
                    openSlots.Add(index);
                }
            }

            /*
             * 边界槽 = `.z01`/`.r00` 族那个「规范名不带卷号」的本体位（`x.zip` / `x.rar`）。
             * 它天然就是"名字被改坏"落地的地方（真案 ②：一只顶美.z删除ip），所以这一档的候选算弱证据。
             */
            bool boundarySlotOpen = family is VolumeNameFamily.ZipSpanned or VolumeNameFamily.RarOld
                && openSlots.Contains(1);

            var unrecognized = new List<VolumeGroupEntry>();

            /*
             * **同一卷的名字变体**（用户 2026-09-27 真机）。
             *
             * 现场：暂存目录里同时有 `222.zip` 与 `222.zi删除p` —— 它们是**同一个逻辑卷的两个名字**
             * （`zi删除p` 就是 `zip` 被塞了两个中文字），而不是"第 1 卷 + 一个不认识的兄弟"。
             * 老判据只看"基名段相同 + 体积对得上"，于是把后者当成"没有卷号的候选"，
             * 推进边界槽 1，再据此写出**自相矛盾**的那句话：
             * 「名字上看缺 `222.zip`；同目录的 `222.zi删除p` 按体积 + 位置推定是第 1 卷」
             * —— 组里那一位就是 `222.zip`，它却报"缺"。整层定稿因此作废，用户的产物一个字节没落地。
             *
             * 判据：候选"去掉杂质之后的后缀"若与该槽**规范名逐字相等**（`222.zi删除p` → `222.zip`、
             * `222.zscip` → `222.zip`），它就是那一卷的名字变体：
             *   · 算组成员（保护名单照旧，⛔ 更不会进可删过程物）；
             *   · **不占独立卷号、不进推断清单** —— 组齐不齐由真实在位的那些卷回答。
             */
            var variantPaths = new List<string>();
            bool variantHasDisguisedName = false;

            foreach (VolumeGroupEntry entry in entries)
            {
                string full = SafePathHelper.GetFullPathSafe(entry.Path);

                if (locatedPaths.Contains(full) ||
                    !string.Equals(StemOf(Path.GetFileName(full)), groupStem, StringComparison.OrdinalIgnoreCase) ||
                    !LooksLikeVolumeCandidate(entry.Size, fullSize))
                {
                    continue;
                }

                if (TryMatchVolumeVariant(Path.GetFileName(full), located, out bool variantIsDisguised))
                {
                    variantPaths.Add(full);

                    /*
                     * 同号的那个名字**不是这一族的规范名** ⇒ 整组算"组里有名字不标准的卷"。
                     *
                     * 为什么必须这样（用户 2026-09-27 真机）：`222.zip` 与 `222.zi删除p` 是同一个逻辑卷的
                     * 两个名字，可**只有一份**是引擎认得的那个（7-Zip 按原名只认 `.zip`）。真机里源包被改名
                     * 成 `222.zscip.zip`（见 `VolumeNameRepair`：目标名被占就不改），所以盘上那个
                     * `222.zi删除p` 很可能就是**没被改到名的另一卷** —— 它名字不标准，整组就不许进
                     * 可删的其余物（25 GB 那次的同一条红线）。
                     *
                     * ⛔ 反过来，同号两个名字**都标准**时（`x.7z.001` 与 `x.7z.001副本` 这种）不算 ——
                     * 那一档是"同一个文件被列了两次"，按规范名办事没有任何歧义。
                     */
                    if (variantIsDisguised)
                    {
                        variantHasDisguisedName = true;
                    }

                    continue;
                }

                unrecognized.Add(entry);
            }

            /*
             * 多余的候选（槽不够塞）⇒ 名字序号后面**可能还有卷**：
             * 这就是"伪装成 .mp4 / .apk 的续卷"（真案 ③）与"末卷被改名成别的后缀"落地的地方。
             * 每多一个候选就多开一个虚拟槽（卷序接着最大值往下排）。
             */
            var fillSlots = new List<int>(openSlots);

            for (int k = 1; k <= unrecognized.Count - openSlots.Count; k++)
            {
                fillSlots.Add(maxFound + k);
            }

            var inferred = new List<ResolvedVolume>();
            var inferredNotes = new List<string>();
            var remaining = new List<int>(fillSlots);

            foreach (VolumeGroupEntry entry in RankCandidates(unrecognized))
            {
                if (remaining.Count == 0)
                {
                    break;
                }

                int slot = PickSlot(remaining, family);
                remaining.Remove(slot);

                string note = $"{Path.GetFileName(entry.Path)}（{FormatBytes(entry.Size)}）按体积 + 位置推定是第 {slot} 卷";

                if (slot != 1 || !boundarySlotOpen)
                {
                    note += " —— ⛔ 它的名字里没有任何卷号，所以只当推定、不当已找到的卷（程序不改你的文件名）";
                }

                inferredNotes.Add(note);

                inferred.Add(new ResolvedVolume
                {
                    Path = SafePathHelper.GetFullPathSafe(entry.Path),
                    Index = slot,
                    Size = entry.Size,
                    NameCarriesVolumeNumber = false,
                    InferredByPosition = true,
                    ConfirmedByTrialOpen = false
                });
            }

            evidence.Add(Row(
                VolumeEvidenceKind.PositionInference,
                VolumeEvidenceWeight.Weak,
                inferred.Count > 0 ? VolumeEvidenceOutcome.Satisfied : VolumeEvidenceOutcome.Unknown,
                "位置推定（弱）",
                unrecognized.Count == 0
                    ? "同目录里没有「基名段相同、名字不带卷号」的候选"
                    : inferredNotes.Count > 0
                        ? string.Join("；", inferredNotes)
                        : $"{unrecognized.Count} 个体积对得上的候选一个槽都分不到（只报不用）"));

            // ── ③ 卷号连续（强证据）──────────────────────────────────────────────
            var missingNames = openSlots
                .Select(index => CanonicalNameFor(family, baseName, index))
                .ToList();

            evidence.Add(Row(
                VolumeEvidenceKind.Sequence,
                VolumeEvidenceWeight.Strong,
                openSlots.Count == 0 ? VolumeEvidenceOutcome.Satisfied : VolumeEvidenceOutcome.NotSatisfied,
                "卷号连续",
                openSlots.Count == 0
                    ? $"第 1..{maxFound.ToString(CultureInfo.InvariantCulture)} 卷一个不缺"
                    : $"第 1..{maxFound.ToString(CultureInfo.InvariantCulture)} 卷之间缺 "
                        + string.Join("、", missingNames)
                        + (group.MissingVolumeNames.Count > 0
                            ? $"（卷名判据的说法：{string.Join("、", group.MissingVolumeNames)}）"
                            : string.Empty)));

            // ── ④ 体积关系（强证据）──────────────────────────────────────────────
            (bool sizeRegular, string sizeDetail) = CheckSizePattern(located);
            bool sizeIsHardEvidence = family is VolumeNameFamily.Numeric or VolumeNameFamily.PartNumbered;

            evidence.Add(Row(
                VolumeEvidenceKind.SizePattern,
                VolumeEvidenceWeight.Strong,
                sizeDetail.Length == 0
                    ? VolumeEvidenceOutcome.Unknown
                    : sizeRegular ? VolumeEvidenceOutcome.Satisfied : VolumeEvidenceOutcome.NotSatisfied,
                "体积关系",
                sizeDetail.Length == 0
                    ? "量不出体积（文件读不到）"
                    : sizeDetail + (sizeIsHardEvidence
                        ? string.Empty
                        : "（这一族的首卷天然可能更短，所以这一条只记录、不当硬证据）")));

            // ── ⑤ 物理同一性（强证据）────────────────────────────────────────────
            (bool samePlace, string placeDetail) = CheckPhysicalIdentity(located);

            evidence.Add(Row(
                VolumeEvidenceKind.PhysicalIdentity,
                VolumeEvidenceWeight.Strong,
                samePlace ? VolumeEvidenceOutcome.Satisfied : VolumeEvidenceOutcome.NotSatisfied,
                "物理同一性",
                placeDetail));

            /*
             * ── ⑥ 试开确认（决定性证据）──────────────────────────────────────────
             *
             * 同步这一档**不试开**（定稿闸门在同步代码里；导入期明确不许调引擎），
             * 但**这一行必须在**：判据表要能一眼看出"试开这一条取到了没有"。
             * ⛔ 记 <see cref="VolumeEvidenceOutcome.Unknown"/>（没试），绝不记成"不成立"——
             * 那是两件完全不同的事。真试过之后由 ResolveAsync 把这一行换掉。
             */
            evidence.Add(Row(
                VolumeEvidenceKind.TrialOpen,
                VolumeEvidenceWeight.Decisive,
                VolumeEvidenceOutcome.Unknown,
                "试开确认",
                "这一次没有试开（同步判定：定稿闸门与导入期的保护名单都不调引擎）"));

            // ── 结论 ─────────────────────────────────────────────────────────────
            var allVolumes = new List<ResolvedVolume>(located);

            foreach (ResolvedVolume volume in inferred)
            {
                if (!allVolumes.Any(v => v.Index == volume.Index))
                {
                    allVolumes.Add(volume);
                }
            }

            allVolumes.Sort((a, b) => a.Index.CompareTo(b.Index));

            bool hasRenamedVolume = variantHasDisguisedName
                || allVolumes.Any(v => !v.NameCarriesVolumeNumber)
                || group.Volumes.Any(v => !IsCanonicalVolumeName(Path.GetFileName(v.Path), baseName));

            var groupPaths = allVolumes
                .Select(v => v.Path)
                .Where(p => p.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!samePlace)
            {
                return WithEvidence(
                    Build(
                        VolumeGroupVerdict.Undetermined,
                        "判不出：" + placeDetail + " —— 位置这一条证据都不成立，不敢下任何结论（不删、不移动、不改名）",
                        allVolumes,
                        Array.Empty<string>(),
                        inferredNotes,
                        hasRenamedVolume),
                    evidence,
                    baseName);
            }

            /*
             * 中间槽有洞 ⇒ **有证据的缺卷**。
             * 这一档的规范名带卷号，一个没有卷号的文件不可能被断言成"就是那一卷"
             * （真案 ①：111.7z.001 + 111 + 111.7z.003 ⇒ 判缺第 2 卷，并把 111 的推定如实说出来）。
             */
            var middleHoles = openSlots.Where(index => !(boundarySlotOpen && index == 1)).ToList();

            if (middleHoles.Count > 0)
            {
                var middleNames = middleHoles.Select(index => CanonicalNameFor(family, baseName, index)).ToList();

                string extra = inferredNotes.Count > 0
                    ? "；同目录的 " + string.Join("、", inferredNotes)
                    : string.Empty;

                return WithEvidence(
                    Build(
                        VolumeGroupVerdict.IncompleteMissingVolume,
                        $"不完整·缺第 {string.Join("、", middleHoles)} 卷（有证据）：卷号 1.."
                        + maxFound.ToString(CultureInfo.InvariantCulture)
                        + " 之间有洞，缺 " + string.Join("、", middleNames)
                        + "。依据：名字里的卷号本身（强证据）" + extra,
                        allVolumes,
                        middleNames,
                        inferredNotes,
                        hasRenamedVolume),
                    evidence,
                    baseName);
            }

            if (remaining.Count > 0 || (openSlots.Count > 0 && inferred.Count > 0))
            {
                /*
                 * 边界槽（.z01/.r00 族那个没有卷号的本体位）—— 真案 ② 就落在这里。
                 * 名字段对得上、体积也对得上，但名字里没有卷号 ⇒ **弱证据** ⇒ 只报"疑缺"，
                 * ⛔ 不许据此说完整（组一旦算完整就会进可删的其余物）。试开成立才升到"完整"。
                 */
                string candidate = inferredNotes.Count > 0
                    ? "同目录的 " + string.Join("、", inferredNotes)
                        + " —— 体积与位置都对得上，但名字里没有卷号，所以只当推定、不当已找到的卷。"
                    : "同目录里也没有「名字段相同、体积对得上」的候选。";

                return WithEvidence(
                    Build(
                        VolumeGroupVerdict.IncompleteSuspected,
                        "不完整·疑缺卷（弱证据）：名字上看缺 " + string.Join("、", missingNames)
                        + "；" + candidate
                        + "要确认只能靠试开（有可用引擎时程序会自己试）。⛔ 这一档只报不删、不动你的任何文件",
                        allVolumes,
                        Array.Empty<string>(),
                        inferredNotes,
                        hasRenamedVolume),
                    evidence,
                    baseName);
            }

            if (inferred.Count > 0)
            {
                /*
                 * 名字上看不出的位子被等大的候选占了（真案 ③：伪装成 .mp4 的续卷）。
                 * 没试开就只到"疑"这一档 —— 认错组比不认糟得多，而"完整"会进可删的其余物。
                 */
                return WithEvidence(
                    Build(
                        VolumeGroupVerdict.IncompleteSuspected,
                        "不完整·疑缺卷（弱证据）：名字序号看不出后面还有卷，但同目录有与满卷等大、"
                        + "基名段相同的候选（" + string.Join("、", inferredNotes)
                        + "）—— 它很可能就是续卷，也可能不是。要确认只能靠试开。⛔ 这一档只报不删、不动你的任何文件",
                        allVolumes,
                        Array.Empty<string>(),
                        inferredNotes,
                        hasRenamedVolume),
                    evidence,
                    baseName);
            }

            if (sizeIsHardEvidence && !sizeRegular && sizeDetail.Length > 0)
            {
                return WithEvidence(
                    Build(
                        VolumeGroupVerdict.IncompleteSuspected,
                        "不完整·疑缺卷（弱证据）：名字序号连续，但体积规律与裸切对不上 —— " + sizeDetail
                        + "。⛔ 这一档只报不删、不动你的任何文件",
                        allVolumes,
                        Array.Empty<string>(),
                        inferredNotes,
                        hasRenamedVolume),
                    evidence,
                    baseName);
            }

            return WithEvidence(
                Build(
                    VolumeGroupVerdict.Complete,
                    "完整：卷号 1.." + maxFound.ToString(CultureInfo.InvariantCulture)
                    + " 连续无洞，每一卷的名字都是标准卷名"
                    + (hasRenamedVolume ? "（但组里有名字不标准的卷，按原名 7-Zip 打不开）" : string.Empty),
                    allVolumes,
                    Array.Empty<string>(),
                    inferredNotes,
                    hasRenamedVolume,
                    variantPaths),
                evidence,
                baseName);
        }

        /// <summary>按结论拼最终对象（<see cref="VolumeGroupResolution.CanEnterDeletableRestItems"/> 只在这里算一次）。</summary>
        private static VolumeGroupResolution Build(
            VolumeGroupVerdict verdict,
            string reason,
            IReadOnlyList<ResolvedVolume> volumes,
            IReadOnlyList<string> missingNames,
            IReadOnlyList<string> inferredNotes,
            bool hasRenamedVolume,
            IReadOnlyList<string>? variantPaths = null) => new()
            {
                Verdict = verdict,
                Volumes = volumes,
                MissingVolumeNames = missingNames,
                PositionInferredNotes = inferredNotes,
                Reason = reason,
                GroupFilePaths = volumes
                    .Select(v => v.Path)
                    .Concat(variantPaths ?? Array.Empty<string>())
                    .Where(p => p.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                VariantFilePaths = variantPaths ?? Array.Empty<string>(),
                HasRenamedVolume = hasRenamedVolume,

                /*
                 * ⛔ 可删资格 = 结论"完整" **且** 每一卷名字都标准，两条缺一不可。
                 * 名字不标准 ⇒ 7-Zip 按原名根本打不开 ⇒ 删了就是不可逆的数据丢失（25 GB 那次）。
                 */
                CanEnterDeletableRestItems = Deletable(verdict, hasRenamedVolume)
            };

        /// <summary>
        /// **"这一组可不可以进可删的其余物"的唯一算法**（AGENTS §9.5：同一件事只有一个出口）。
        ///
        /// <para>只有两条都成立才放行：① 结论是 <see cref="VolumeGroupVerdict.Complete"/>；
        /// ② 组里没有名字不标准的卷。⛔ 其余一切情形（含"判不出"、含弱证据）一律 false ——
        /// 兜底落在"什么都不做"那一档。</para>
        ///
        /// <para>放在这里当唯一出口，是因为试开那一档（<see cref="ApplyTrialOutcome"/>）也会落结论；
        /// 两处各写一遍 `verdict == Complete &amp;&amp; !hasRenamedVolume` 迟早会走岔。</para>
        /// </summary>
        private static bool Deletable(VolumeGroupVerdict verdict, bool hasRenamedVolume) =>
            verdict == VolumeGroupVerdict.Complete && !hasRenamedVolume;

        /// <summary>
        /// 这一档"没试开"是不是**原理上做不了**（不是我这次没取到证据）。
        ///
        /// <para>做不了 ⇒ 只能如实报"无法确认" ⇒ 结论降成 <see cref="VolumeGroupVerdict.Undetermined"/>
        /// （⛔ 不许拿名字/体积那几条去顶"完整"，那正是 25 GB 那次的形状）。两档都源自用户红线
        /// 「工作区就设在解压的地方，这就完全不存在跨盘的操作」：</para>
        /// <list type="bullet">
        /// <item><description><b>没有工作区根</b>（<see cref="VolumeTrialSkipReason.NoProbeRoot"/>）——
        /// 拿不到目标目录，而工作区只准设在解压的地方，⛔ 不许另找地方开一个；</description></item>
        /// <item><description><b>跨盘</b>（<see cref="VolumeTrialSkipReason.CrossVolume"/>）——
        /// 硬链接不能跨卷，⛔ 不许退到源卷根去试。这一条是**原理限制**，不是偷懒。</description></item>
        /// </list>
        ///
        /// <para>其余几档（没引擎 / 凑不出顺序）照旧"这一次没取到证据，结论不变" —— 没试 ≠ 不成立。</para>
        /// </summary>
        private static bool IsUnconfirmable(VolumeTrialSkipReason reason) =>
            reason is VolumeTrialSkipReason.CrossVolume or VolumeTrialSkipReason.NoProbeRoot;

        /// <summary>"没试开"的档说成给人看的一句话（⛔ 判据不许比这句话，判据只读枚举）。</summary>
        private static string DescribeSkip(VolumeTrialSkipReason reason) => reason switch
        {
            VolumeTrialSkipReason.CrossVolume => "跨盘无法试开，整卷是否齐全无法确认",
            VolumeTrialSkipReason.NoProbeRoot => "没有工作区根（拿不到目标目录），不许另找地方试开，整卷是否齐全无法确认",
            _ => "试开这一条证据取不到，整卷是否齐全无法确认"
        };

        // ────────────────────────────────────────────────────────────────────────
        // 证据各条的实现
        // ────────────────────────────────────────────────────────────────────────

        /// <summary>体积关系：除最后一卷外等大 + 最后一卷 ≤ 其余。</summary>
        private static (bool Regular, string Detail) CheckSizePattern(IReadOnlyList<ResolvedVolume> located)
        {
            if (located.Count == 0 || located.Any(v => v.Size <= 0))
            {
                return (false, string.Empty);
            }

            if (located.Count == 1)
            {
                return (true, $"只定位到 1 卷（{FormatBytes(located[0].Size)}），没有可比的对象");
            }

            long full = located[0].Size;

            for (int i = 0; i < located.Count - 1; i++)
            {
                if (located[i].Size != full)
                {
                    return (false, $"第 {located[i].Index} 卷 {FormatBytes(located[i].Size)} 与第 1 卷 "
                        + $"{FormatBytes(full)} 不等（裸切应当等大）");
                }
            }

            if (located[^1].Size > full)
            {
                return (false, $"最后一卷 {FormatBytes(located[^1].Size)} 比其余卷 {FormatBytes(full)} 还大（裸切不可能）");
            }

            return (true, $"第 1..{located[^1].Index - 1} 卷都是 {FormatBytes(full)}，最后一卷 {FormatBytes(located[^1].Size)}");
        }

        /// <summary>物理同一性：同一目录 + 同一卷根；同 FileId ⇒ 同一份（硬链接 / 同一个文件的两个名字）。</summary>
        private static (bool SamePlace, string Detail) CheckPhysicalIdentity(IReadOnlyList<ResolvedVolume> located)
        {
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var identities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int linkNoted = 0;

            foreach (ResolvedVolume volume in located)
            {
                try
                {
                    string? directory = Path.GetDirectoryName(volume.Path);
                    string root = Path.GetPathRoot(volume.Path) ?? string.Empty;

                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        directories.Add(Path.GetFullPath(directory));
                    }

                    if (root.Length > 0)
                    {
                        roots.Add(root);
                    }
                }
                catch
                {
                    // 路径形状不合法只影响"同一目录"这个判断，不影响别的证据。
                }

                if (!TryGetFileId(volume.Path, out uint serial, out ulong index, out uint links))
                {
                    continue;
                }

                string key = serial.ToString("X8", CultureInfo.InvariantCulture)
                    + ":" + index.ToString("X16", CultureInfo.InvariantCulture);

                if (identities.TryGetValue(key, out string? other))
                {
                    return (false, $"{Path.GetFileName(volume.Path)} 与 {Path.GetFileName(other)} 是同一个文件"
                        + "（同一个 FileId：硬链接或同一个文件的两个名字）—— 一份数据不能算成两卷");
                }

                identities[key] = volume.Path;

                if (links > 1)
                {
                    linkNoted++;
                }
            }

            if (directories.Count > 1)
            {
                return (false, $"这几卷不在同一个目录里（{directories.Count} 个目录）—— "
                    + "跨目录凭什么认定是同一组还没想清楚，⛔ 不凭名字像就拼");
            }

            if (roots.Count > 1)
            {
                return (false, $"这几卷跨了 {roots.Count} 个卷（{string.Join("、", roots)}）—— "
                    + "跨卷做不了硬链接，装配不了，也没法试开");
            }

            string note = linkNoted > 0
                ? $"；其中 {linkNoted} 卷的硬链接数大于 1（磁盘上另有名字指向同一份数据，但 FileId 各不相同）"
                : string.Empty;

            return (true, $"同一个目录、同一个卷根（{string.Join("、", roots)}），各卷 FileId 互不相同{note}");
        }

        // ────────────────────────────────────────────────────────────────────────
        // 辅助
        // ────────────────────────────────────────────────────────────────────────

        /// <summary>候选排序：先等大的（裸切续卷），再按目录内位置、时间、名字（都只当弱证据的排序依据）。</summary>
        private static IEnumerable<VolumeGroupEntry> RankCandidates(IReadOnlyList<VolumeGroupEntry> candidates) =>
            candidates
                .OrderByDescending(e => e.Size)
                .ThenBy(e => e.OrderIndex)
                .ThenBy(e => e.LastWriteTimeUtc)
                .ThenBy(e => Path.GetFileName(e.Path), StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 给一个候选挑槽。
        ///
        /// <para><c>.z01</c>/<c>.r00</c> 族里那个**不带卷号的本体位**优先给它 ——
        /// 那正是"改坏了名字"最自然的落地处（真案 ②）。其余情况按卷序从小到大填。</para>
        /// </summary>
        private static int PickSlot(IReadOnlyList<int> slots, VolumeNameFamily family)
        {
            List<int> ordered = slots.OrderBy(i => i).ToList();

            if (family is VolumeNameFamily.ZipSpanned or VolumeNameFamily.RarOld && ordered.Contains(1))
            {
                return 1;
            }

            return ordered[0];
        }

        /// <summary>第 <paramref name="index"/> 卷在**这一族里**的标准名字（补缺卷名用）。</summary>
        private static string CanonicalNameFor(VolumeNameFamily family, string baseName, int index)
        {
            switch (family)
            {
                case VolumeNameFamily.ZipSpanned when index == 1:
                case VolumeNameFamily.RarOld when index == 1:
                    return baseName + (family == VolumeNameFamily.ZipSpanned ? ".zip" : ".rar");

                case VolumeNameFamily.ZipSpanned:
                    return baseName + ".z" + (index - 1).ToString("D2", CultureInfo.InvariantCulture);

                case VolumeNameFamily.RarOld:
                    return baseName + ".r" + (index - 2).ToString("D2", CultureInfo.InvariantCulture);

                case VolumeNameFamily.PartNumbered:
                    return baseName + ".part" + index.ToString(CultureInfo.InvariantCulture) + ".rar";

                default:
                    return baseName + "." + index.ToString("D3", CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// 这一卷的名字是不是"7-Zip 按原名认得的标准卷名"。
        ///
        /// <para>⛔ 卷标记段里**一点垃圾都不许有**：<c>x.7z.001删除</c> 与 <c>111</c> 都不算 ——
        /// 它们按原名交给 7-Zip 就是打不开，删了就是不可逆的数据丢失。</para>
        /// </summary>
        private static bool IsCanonicalVolumeName(string fileName, string baseName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            if (string.Equals(fileName, baseName + ".zip", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, baseName + ".rar", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string[] parts = fileName.Split('.');

            if (parts.Length < 2)
            {
                return false;
            }

            // x.part1.rar：卷标记在倒数第二段
            if (parts.Length >= 3 &&
                parts[^1].Equals("rar", StringComparison.OrdinalIgnoreCase) &&
                IsCleanVolumeSegment(parts[^2]))
            {
                return true;
            }

            return IsCleanVolumeSegment(parts[^1]);
        }

        /// <summary>这一段是不是"整段就是卷标记、后面一点垃圾都没有"。</summary>
        private static bool IsCleanVolumeSegment(string segment) =>
            ExtensionHelper.TrySplitVolumeSegment(segment, out string mark, out string junk)
            && junk.Length == 0
            && mark.Length > 0
            && mark.Equals(segment, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 这个名字是不是**已在位的那一卷的另一个名字**（用户 2026-09-27 真机：`222.zip` 与 `222.zi删除p`）。
        ///
        /// <para>判据只有一条：把"被塞了杂质的后缀"还原（唯一出口
        /// <see cref="ExtensionHelper.TryRecoverDisguisedArchiveBody"/>），还原结果与某一卷**规范名逐字相等**
        /// ⇒ 它就是那一卷的名字变体，**不占独立卷号、也不构成"缺了一卷"的证据**
        /// （老判据把它当"没有卷号的候选"推定进边界槽，于是同一句话里既说"缺 `222.zip`"、
        /// 又说那个文件"就是第 1 卷"，整层定稿作废）。</para>
        ///
        /// <para>⛔ 变体**不会**让那一卷变成"可以删"：它进 <c>GroupFilePaths</c> 与
        /// <c>VariantFilePaths</c>，而可删资格仍由磁盘上的真名字回答（<see cref="IsCanonicalVolumeName"/>）
        /// —— 名字不标准就一个字节都不许删。</para>
        /// </summary>
        private static bool TryMatchVolumeVariant(
            string? fileName,
            IReadOnlyList<ResolvedVolume> located,
            out bool disguisedName)
        {
            disguisedName = false;

            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            // ⛔ 只喂**末尾那一段**（`222.zi删除p` 的 `zi删除p`）：还原函数回答的是"这一段是什么后缀"，
            //    整条文件名进去只会把它当成一个含点的名字，永远还原不出来（写成这样踩过一次）。
            string segment = Path.GetExtension(fileName).TrimStart('.');

            /*
             * ⛔ 只有"归档本体那一档"（不带卷标记的名字）才走这条：带卷标记的照旧走老路 ——
             * 那一档的名字变体（`x.7z.001删除`）本来就是"缺卷证据"的来源，动它等于拆红线。
             */
            if (ExtensionHelper.IsVolumeSegment(segment))
            {
                return false;
            }

            if (!ExtensionHelper.TryRecoverDisguisedArchiveBody(segment, out string suffix, out _))
            {
                return false;
            }

            /*
             * ⛔ 只认**本体那一族**（`zip` / `rar`）：它们的卷号 1 就是"名字里带本体后缀"的那一份，
             * 所以"同基名段 + 同后缀"只可能指同一个逻辑卷。其余后缀（`7z` / `tar` / 数字族…）的本体
             * 是"卷号 1"，而 `x.7z.002` 这种**带卷标记**的名字本来就不走这条（上面已经挡掉），
             * 万一漏进来也不是"另一个名字"，不许当变体（认错组比不认糟得多）。
             */
            if (!suffix.Equals("zip", StringComparison.OrdinalIgnoreCase)
                && !suffix.Equals("rar", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            foreach (ResolvedVolume volume in located)
            {
                string volumeName = Path.GetFileName(volume.Path);
                string volumeStem = StemOf(volumeName);

                if (!string.Equals(volumeStem, StemOf(fileName), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                /*
                 * 只比"本体那一档"的规范名（`x.zip` / `x.rar`）：还原出来的后缀是 `zip` 时，
                 * 只有 `…zip` 这一位算它的家；`x.z01` / `x.7z.001` 各有各的族，⛔ 不许跨族认亲
                 * （跨族认亲会让"另一套命名的第一卷"被误判成"同名的变体"，那比不认糟得多）。
                 *
                 * ⚠ 在位的这一卷**自己的名字也可能不标准**（真机里那一卷叫 `222.zi删除p`）——
                 * 那也照样认：判据是"两个文件是同一个卷号上的两个名字"，不是"在位那个必须标准"。
                 * 它们两个都留在保护名单里，可删资格仍由磁盘上的真名字回答（都不标准 ⇒ 一个都不删）。
                 */
                if (volumeName.Equals(volumeStem + "." + suffix, StringComparison.OrdinalIgnoreCase))
                {
                    // 这个名字本身是不是这一族的规范写法（`222.zip` 是、`222.zi删除p` 不是）。
                    disguisedName = !string.Equals(fileName, volumeStem + "." + suffix, StringComparison.Ordinal);
                    return true;
                }
            }

            return false;
        }

        /// <summary>这个文件的"基名段"：去掉最后一段后缀。无后缀时就是原名本身。</summary>
        private static string StemOf(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return string.Empty;
            }

            string stem = Path.GetFileNameWithoutExtension(fileName);

            return stem.Length == 0 ? fileName : stem;
        }

        /// <summary>
        /// 这个候选"像不像一卷"：裸切 ⇒ 与满卷**等大**；边界槽（<c>.z01</c>/<c>.r00</c> 族那个没卷号的本体位）
        /// 允许更短。⛔ 体积不搭的一律不当候选 —— 这是把"同目录的说明文件 / 封面"挡在外面的唯一一道闸。
        /// </summary>
        private static bool LooksLikeVolumeCandidate(long size, long fullSize)
        {
            /*
             * 基名段已经相等（调用方筛过）之后，体积只要 0 < size ≤ 满卷 就算候选 ——
             * 裸切的续卷与满卷**等大**，而**最后一卷必然更短**，所以"更短的"恰恰是
             * "末卷被改名成别的后缀"最常见的形状。
             *
             * ⛔ 比满卷**还大**的一律不当候选：裸切切不出比满卷更大的片，那是同目录的另一份东西。
             *
             * ⚠ 代价写在明处：这一条宽判据会让"同目录一份同基名的说明文件"也算候选，
             * 于是整组只到「疑缺卷」这一档、定稿计划作废（**什么都不搬、不删**）。
             * 这个取舍是故意的 —— 认错组比不认糟得多：漏掉一个被改名的末卷，它会被当成内容物搬走，
             * 而其余卷进了可删的其余物（叠加「空间不足」模式的永久删除就是不可逆的数据丢失）。
             * 要确认只能靠试开（VolumeEvidenceKind.TrialOpen）。
             */
            return size > 0 && fullSize > 0 && size <= fullSize;
        }

        /// <summary>组名族（决定卷序怎么算、缺失名怎么补、哪个位子的规范名不带卷号）。</summary>
        private static VolumeNameFamily ResolveFamily(VolumeGroup group, string baseName)
        {
            foreach (VolumeCandidate volume in group.Volumes)
            {
                string? first = VolumeGroupDetector.TryGetFirstVolumeName(Path.GetFileName(volume.Path));

                if (string.IsNullOrWhiteSpace(first))
                {
                    continue;
                }

                if (first.Equals(baseName + ".zip", StringComparison.OrdinalIgnoreCase))
                {
                    return VolumeNameFamily.ZipSpanned;
                }

                if (first.Equals(baseName + ".rar", StringComparison.OrdinalIgnoreCase))
                {
                    return VolumeNameFamily.RarOld;
                }

                if (first.StartsWith(baseName + ".part", StringComparison.OrdinalIgnoreCase))
                {
                    return VolumeNameFamily.PartNumbered;
                }

                return VolumeNameFamily.Numeric;
            }

            return VolumeNameFamily.Numeric;
        }

        /// <summary>从归组结果里找出锚点所在的那一组；锚点自己没卷号时按"基名段相同"反向找。</summary>
        private static VolumeGroup? FindAnchorGroup(IReadOnlyList<VolumeGroup> groups, string anchor)
        {
            foreach (VolumeGroup group in groups)
            {
                foreach (VolumeCandidate volume in group.Volumes)
                {
                    if (string.Equals(
                        SafePathHelper.GetFullPathSafe(volume.Path),
                        anchor,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return group;
                    }
                }
            }

            /*
             * 锚点自己不是规范卷名（`111` / `一只顶美.z删除ip` / `电影.mp4`）——
             * 那就按"基名段相同"去认门：这正是「后缀可能不同但是名字一定相同」那一条。
             * ⛔ 只在**同目录**里找（跨目录凭什么认定是同一组还没想清楚）。
             */
            string anchorStem = StemOf(Path.GetFileName(anchor));

            if (anchorStem.Length == 0)
            {
                return null;
            }

            foreach (VolumeGroup group in groups)
            {
                if (string.Equals(StemOf(group.BaseName), anchorStem, StringComparison.OrdinalIgnoreCase))
                {
                    return group;
                }
            }

            return null;
        }

        /// <summary>目录里的文件事实（判定器自己去列）。读不到就是空清单（调用方据此报"判不出"）。</summary>
        private static IReadOnlyList<VolumeGroupEntry> EnumerateDirectory(string anchor)
        {
            var result = new List<VolumeGroupEntry>();

            try
            {
                string? directory = Path.GetDirectoryName(anchor);

                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    return result;
                }

                int order = 0;

                foreach (string path in Directory.EnumerateFiles(directory))
                {
                    DateTime written = DateTime.MinValue;

                    try
                    {
                        written = File.GetLastWriteTimeUtc(path);
                    }
                    catch
                    {
                        // 时间取不到只影响"与邻居的相对位置"这一条弱证据。
                    }

                    result.Add(new VolumeGroupEntry
                    {
                        Path = SafePathHelper.GetFullPathSafe(path),
                        Size = SizeOf(path),
                        LastWriteTimeUtc = written,
                        OrderIndex = order++
                    });
                }
            }
            catch
            {
                // 读不到目录 ⇒ 空清单 ⇒ 判不出（⛔ 不猜）。
            }

            return result;
        }

        /// <summary>试开要试的几种排列（第 1 卷由 <see cref="VolumeTrialRequest.FirstVolumePath"/> 单独给）。</summary>
        private static List<List<VolumeCandidate>> BuildTrialOrderings(VolumeGroupResolution baseline)
        {
            var result = new List<List<VolumeCandidate>>();
            var byIndex = new Dictionary<int, ResolvedVolume>();

            foreach (ResolvedVolume volume in baseline.Volumes)
            {
                if (!byIndex.ContainsKey(volume.Index))
                {
                    byIndex[volume.Index] = volume;
                }
            }

            if (byIndex.Count <= 1)
            {
                return result;
            }

            int max = byIndex.Keys.Max();

            // 只有"1..max 一个不缺"才凑得出可试的完整顺序（缺一卷 7-Zip 必然报 Missing volume）。
            for (int index = 1; index <= max; index++)
            {
                if (!byIndex.ContainsKey(index))
                {
                    return result;
                }
            }

            var ordered = new List<VolumeCandidate>();

            for (int index = 2; index <= max; index++)
            {
                ordered.Add(new VolumeCandidate { Path = byIndex[index].Path, Size = byIndex[index].Size });
            }

            result.Add(ordered);

            /*
             * 排列的变体：把"靠推定才认进来的"那些卷**互换位置**再试 ——
             * 位置上分不清谁是谁的时候，让引擎来分。上限 MaxTrialOrderings：试开一次是一次进程调用。
             */
            var swapable = new List<int>();

            for (int i = 0; i < ordered.Count; i++)
            {
                if (byIndex[i + 2].InferredByPosition)
                {
                    swapable.Add(i);
                }
            }

            for (int i = 0; i < swapable.Count && result.Count < MaxTrialOrderings; i++)
            {
                for (int j = i + 1; j < swapable.Count && result.Count < MaxTrialOrderings; j++)
                {
                    var variant = new List<VolumeCandidate>(ordered);
                    (variant[swapable[i]], variant[swapable[j]]) = (variant[swapable[j]], variant[swapable[i]]);
                    result.Add(variant);
                }
            }

            return result;
        }

        private static VolumeGroupResolution Undetermined(string anchor, string reason) => new()
        {
            Verdict = VolumeGroupVerdict.Undetermined,
            AnchorPath = anchor,
            Reason = reason,
            CanEnterDeletableRestItems = false
        };

        private static VolumeEvidence Row(
            VolumeEvidenceKind kind,
            VolumeEvidenceWeight weight,
            VolumeEvidenceOutcome outcome,
            string title,
            string detail) => new()
            {
                Kind = kind,
                Weight = weight,
                Outcome = outcome,
                Title = title,
                Detail = detail
            };

        private static VolumeGroupResolution Copy(
            VolumeGroupResolution source,
            IReadOnlyList<VolumeEvidence>? evidence = null,
            string? baseName = null) => new()
            {
                Verdict = source.Verdict,
                AnchorPath = source.AnchorPath,
                BaseName = baseName ?? source.BaseName,
                Volumes = source.Volumes,
                MissingVolumeNames = source.MissingVolumeNames,
                PositionInferredNotes = source.PositionInferredNotes,
                Evidence = evidence ?? source.Evidence,
                Reason = source.Reason,
                GroupFilePaths = source.GroupFilePaths,
                HasRenamedVolume = source.HasRenamedVolume,
                CanEnterDeletableRestItems = source.CanEnterDeletableRestItems
            };

        private static VolumeGroupResolution WithEvidence(
            VolumeGroupResolution resolution,
            IReadOnlyList<VolumeEvidence> evidence,
            string baseName) => Copy(resolution, evidence, baseName);

        private static VolumeGroupResolution AddRow(VolumeGroupResolution resolution, VolumeEvidence row) =>
            Copy(resolution, Concat(resolution.Evidence, row));

        /// <summary>
        /// 把一条证据并进证据表：**同一种证据只留一行**（先加的那一行是"没试"的占位，
        /// 真试过之后由试开那一档替换掉）—— 证据表要么六行、要么更少，⛔ 不许同一件事出现两行。
        /// </summary>
        private static IReadOnlyList<VolumeEvidence> Concat(IReadOnlyList<VolumeEvidence> source, VolumeEvidence row)
        {
            var result = new List<VolumeEvidence>();

            foreach (VolumeEvidence existing in source)
            {
                if (existing.Kind != row.Kind)
                {
                    result.Add(existing);
                }
            }

            result.Add(row);

            return result;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0)
            {
                return "量不出体积";
            }

            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;

            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return value.ToString(unit == 0 ? "0" : "0.##", CultureInfo.InvariantCulture) + units[unit];
        }

        private static long SizeOf(string path)
        {
            try
            {
                return string.IsNullOrWhiteSpace(path) ? 0 : new FileInfo(path).Length;
            }
            catch
            {
                return 0;
            }
        }

        private static bool TryGetFileId(string path, out uint volumeSerial, out ulong fileIndex, out uint linkCount)
        {
            volumeSerial = 0;
            fileIndex = 0;
            linkCount = 0;

            IntPtr handle = IntPtr.Zero;

            try
            {
                handle = CreateFileW(
                    path,
                    FileReadAttributes,
                    FileShareRead | FileShareWrite | FileShareDelete,
                    IntPtr.Zero,
                    OpenExisting,
                    FileFlagBackupSemantics,
                    IntPtr.Zero);

                if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                {
                    return false;
                }

                if (!GetFileInformationByHandle(handle, out ByHandleFileInformation info))
                {
                    return false;
                }

                volumeSerial = info.VolumeSerialNumber;
                fileIndex = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
                linkCount = info.NumberOfLinks;

                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (handle != IntPtr.Zero && handle != new IntPtr(-1))
                {
                    CloseHandle(handle);
                }
            }
        }

        private const uint FileReadAttributes = 0x0080;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint FileShareDelete = 0x00000004;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(IntPtr hFile, out ByHandleFileInformation lpFileInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        /// <summary>组名族（与 <see cref="VolumeGroupDetector"/> 内部那一套一一对应；此处只用来判"哪个位子的规范名不带卷号"）。</summary>
        private enum VolumeNameFamily
        {
            /// <summary>末尾纯数字段（<c>volume.7z.001</c> / <c>archive.001</c>）：数字就是卷序。</summary>
            Numeric = 0,

            /// <summary>zip 分卷（<c>x.zip</c> + <c>x.z01</c> …）：本体是第 1 卷，而且它的名字**不带卷号**。</summary>
            ZipSpanned = 1,

            /// <summary>rar 老式分卷（<c>x.rar</c> + <c>x.r00</c> …）：同上。</summary>
            RarOld = 2,

            /// <summary>rar 新式分卷（<c>x.partN.rar</c>）：N 就是卷序。</summary>
            PartNumbered = 3
        }
    }
}
