using System;
using System.Collections.Generic;
using System.Linq;
using ArchiveFixer.Models;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 一条**特定解压规则**在定稿规划上产生的效果（机器可判，别去比中文文案）。
    ///
    /// <para>
    /// 为什么要有这个枚举、而不是让 <see cref="ResultFinalizer"/> 自己去认 Id：
    /// 注册表（<see cref="SpecialExtractionRules"/>）只提供**描述**（Id / 中文名 / 一句说明），
    /// "这条规则怎么实现"由管线按 Id 分派到这里的一个效果上 ——
    /// 以后加规则 = **加一条描述 + 加一个效果（以及它的实现）+ 加一个分派分支**，
    /// 界面骨架一个字都不用动（用户 2026-09-24 原话："以后可能会经常加的东西"）。
    /// </para>
    /// </summary>
    public enum SpecialExtractionEffect
    {
        /// <summary>没有生效的规则（总开关关着 / 一条都没开 / Id 认不出）：行为与以前逐字相同。</summary>
        None = 0,

        /// <summary>
        /// 「每个包只留一层内容」（<see cref="SpecialExtractionRules.SingleContentLayer"/>）：
        /// **包名那一层永远保留**，把它里面多套的那一层去掉 —— 判定表里"要套的那一层"不再套，
        /// 内容物直接落在成品目录里（<c>222\1111\内容物</c>）。
        ///
        /// <para>
        /// 只在"确实是一条单链"时才塌：包内不超过一个内容文件夹（含多层单链）或只有内容文件。
        /// 出现多个并列文件夹 / 多个分支时不塌（按原判定表保守套一层，并写一条 WARN 说明原因）。
        /// 多个包共用同一个成品目录时同样不塌（那会把几个包的内容物混在一起），也写 WARN。
        /// </para>
        /// </summary>
        SingleContentLayer = 1
    }

    /// <summary>
    /// 一条特定解压规则的**描述**（注册表里的元素）。纯数据，不含任何实现。
    /// </summary>
    public sealed class SpecialExtractionRule
    {
        /// <summary>稳定 Id（落盘用，写进 <see cref="AppSettings.SpecialExtractionRules"/>；改它等于让旧配置失效）。</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>中文名（进界面与文档，例如「每个包只留一层内容」）。</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>一句说明（进界面与文档：这条规则做了什么、什么时候不生效）。</summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>
        /// 新配置里默认是否勾上。
        ///
        /// <para>
        /// ⚠ 它**只**影响 <see cref="AppSettings.CreateDefault"/>（以及"旧配置里根本没有这个字段"那种情形），
        /// **不**参与 <see cref="SpecialExtractionRules.Normalize"/> —— 否则用户勾掉全部规则之后，
        /// 下一次读设置又会被"补"回来，"关掉全部 = 与现在完全一样"就成了一句空话。
        /// </para>
        /// </summary>
        public bool EnabledByDefault { get; init; }
    }

    /// <summary>
    /// 特定解压规则的**注册表**（规格 <c>docs/输出与整理模型.md</c> §3.5）。
    ///
    /// <para>
    /// 用户 2026-09-24 原话："我是想在一键解压旁边弄一个特定解压开关……在解压方式里面就可以去添加
    /// 一个特定解压这一栏，**也就是以后可能会经常加的东西，因为每个人的特定的解压方式不同**"。
    /// </para>
    ///
    /// <para>
    /// 所以这一栏是**按注册表渲染**的：界面（①页开关的 ToolTip、②页那一栏）只认这里的描述，
    /// 不写死任何一条规则。**加第二条规则要做三件事**：
    /// </para>
    /// <list type="number">
    /// <item><description>在下面 <see cref="Rules"/> 的初始化里加一条 <see cref="SpecialExtractionRule"/>（Id / 名称 / 说明）；</description></item>
    /// <item><description>在 <see cref="SpecialExtractionEffect"/> 里加它对应的效果，并在
    /// <see cref="ResolveEffect"/> 里加一行按 Id 的分派；</description></item>
    /// <item><description>在管线里实现那个效果（例如 <see cref="ResultFinalizer"/> 里加它那个形态的落法）。</description></item>
    /// </list>
    /// <para>
    /// ⛔ 界面骨架（<c>Views/Tabs/TaskTab.xaml</c> 的开关、<c>Views/Tabs/ExtractionTab.xaml</c> 的列表）
    /// **一个字都不用改** —— 有测试钉住这一点（塞一条假描述进注册表 → 两边的列表里自动多一条）。
    /// </para>
    /// <para>
    /// 本类**纯逻辑、不引用 WPF**（分层铁律）：界面文案里的规则名与说明从这里取，
    /// 而界面骨架与日志模板的措辞仍在 <see cref="StatusText"/>（AGENTS.md §7）。
    /// </para>
    /// </summary>
    public static class SpecialExtractionRules
    {
        /// <summary>第一条规则的 Id：每个包只留一层内容。</summary>
        public const string SingleContentLayer = "SingleContentLayer";

        /// <summary>注册表的锁（<see cref="RegisterForTesting"/> 会临时改它，读的地方都拿它）。</summary>
        private static readonly object Gate = new();

        /// <summary>
        /// 全部已注册的规则（**声明顺序 = 界面上的顺序**）。
        ///
        /// <para>⚠ 只允许在下面这一个地方追加；别处一律通过 <see cref="All"/> 读快照。</para>
        /// </summary>
        private static readonly List<SpecialExtractionRule> Rules = new()
        {
            new SpecialExtractionRule
            {
                Id = SingleContentLayer,
                Name = "每个包只留一层内容",
                Description =
                    "包名那一层永远保留（222\\1111\\），把它里面多套的那一层去掉：包内只有一条单链（一个内容文件夹，"
                    + "或者只有内容文件）时，内容物直接落在包名层里 —— 1111.rar\\内容文件夹\\内容物 → 222\\1111\\内容物。"
                    + "包内有多个并列文件夹、或几个包共用同一个成品目录时不塌，按原判定表套一层并写一条 WARN 说明原因。"
                    + "其余物位置不变（仍在 222\\1111\\其余物\\），同名冲突仍按设置里的冲突档自动改名，绝不覆盖。",
                EnabledByDefault = true
            }
        };

        /// <summary>全部已注册规则的快照（顺序 = 声明顺序）。</summary>
        public static IReadOnlyList<SpecialExtractionRule> All
        {
            get
            {
                lock (Gate)
                {
                    return Rules.ToList();
                }
            }
        }

        /// <summary>新配置里默认勾上的规则 Id（顺序 = 声明顺序）。</summary>
        public static IReadOnlyList<string> DefaultEnabledIds
        {
            get
            {
                lock (Gate)
                {
                    return Rules.Where(rule => rule.EnabledByDefault).Select(rule => rule.Id).ToList();
                }
            }
        }

        /// <summary>按 Id 找一条规则（认大小写差异，去空白）；认不出返回 null。</summary>
        public static SpecialExtractionRule? Find(string? id)
        {
            string canonical = Canonicalize(id);

            if (canonical.Length == 0)
            {
                return null;
            }

            lock (Gate)
            {
                return Rules.FirstOrDefault(rule => string.Equals(rule.Id, canonical, StringComparison.Ordinal));
            }
        }

        /// <summary>这个 Id 认不认得。</summary>
        public static bool IsKnown(string? id) => Find(id) != null;

        /// <summary>
        /// 把用户手改的写法映射回注册表里的规范写法（认大小写差异）；认不出就返回去掉空白后的原样值。
        ///
        /// <para>与 <c>EngineIds.Canonicalize</c> 同一口径：设置文件是人可读可改的，
        /// 大小写不同不该被当成另一条规则（那会让"改了没反应"）。</para>
        /// </summary>
        public static string Canonicalize(string? id)
        {
            string trimmed = id?.Trim() ?? string.Empty;

            if (trimmed.Length == 0)
            {
                return string.Empty;
            }

            lock (Gate)
            {
                foreach (SpecialExtractionRule rule in Rules)
                {
                    if (string.Equals(rule.Id, trimmed, StringComparison.OrdinalIgnoreCase))
                    {
                        return rule.Id;
                    }
                }
            }

            return trimmed;
        }

        /// <summary>
        /// 归一化"启用的规则 Id 清单"（设置层与运行时共用这一份口径，**唯一实现**）：
        /// 去掉空白项、**丢掉认不出的 Id**、按不区分大小写去重、统一成注册表里的规范写法，
        /// 最后按**注册表的声明顺序**排（界面上的顺序 = 落盘的顺序 = 文档里的顺序，三处不会分叉）。
        ///
        /// <para>
        /// <paramref name="ids"/> 为 <c>null</c>（旧配置里根本没有这个字段）→ 取
        /// <see cref="DefaultEnabledIds"/>；为**空列表**（用户把规则全关掉了）→ 保持空。
        /// 这个区分很要紧：不区分的话，"关掉全部 = 与现在完全一样"就再也存不住
        /// （每次读设置都把默认规则补回来，用户以为关掉了、其实还在按它跑）。
        /// </para>
        /// <para>
        /// ⛔ 刻意**不**照抄 <c>EngineIds.Normalize</c> 的"把漏掉的补回列表末尾"：引擎列表那是
        /// "排序"，没排过的引擎仍然可用；规则列表是**开关**，补回来就是替用户做决定。
        /// </para>
        /// </summary>
        public static List<string> Normalize(IEnumerable<string>? ids)
        {
            if (ids == null)
            {
                return DefaultEnabledIds.ToList();
            }

            var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string? raw in ids)
            {
                string canonical = Canonicalize(raw);

                if (canonical.Length > 0)
                {
                    requested.Add(canonical);
                }
            }

            lock (Gate)
            {
                return Rules.Where(rule => requested.Contains(rule.Id)).Select(rule => rule.Id).ToList();
            }
        }

        /// <summary>清单里这条规则是不是开着（认大小写差异）。</summary>
        public static bool IsEnabled(IEnumerable<string>? ids, string? ruleId)
        {
            string canonical = Canonicalize(ruleId);

            if (canonical.Length == 0 || ids == null)
            {
                return false;
            }

            foreach (string? id in ids)
            {
                if (string.Equals(Canonicalize(id), canonical, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>清单里真正启用的规则（按注册表顺序；认不出的 Id 自动丢掉）。</summary>
        public static IReadOnlyList<SpecialExtractionRule> ResolveEnabled(IEnumerable<string>? ids)
        {
            List<string> normalized = Normalize(ids);

            if (normalized.Count == 0)
            {
                return Array.Empty<SpecialExtractionRule>();
            }

            lock (Gate)
            {
                return Rules.Where(rule => normalized.Contains(rule.Id, StringComparer.Ordinal)).ToList();
            }
        }

        /// <summary>
        /// **按 Id 分派**：清单里开着的规则合起来在定稿规划上产生哪个效果（规格 §3.5 的实现分派点）。
        ///
        /// <para>多条形如"叠加"的规则将来要在这里定优先级 —— 现在只有一条，直接返回它的效果；
        /// 认不出的 Id 在第 ① 步就被丢掉了，所以这里只需要认识注册表里那几个常量。</para>
        /// </summary>
        public static SpecialExtractionEffect ResolveEffect(IEnumerable<string>? ids)
        {
            if (IsEnabled(ids, SingleContentLayer))
            {
                return SpecialExtractionEffect.SingleContentLayer;
            }

            return SpecialExtractionEffect.None;
        }

        /// <summary>
        /// 规则中文名串联（界面 / 日志 / 任务详情共用一份；没有启用的返回空串）。
        /// </summary>
        public static string DescribeEnabledNames(IEnumerable<string>? ids, string separator = "、")
        {
            return string.Join(separator, ResolveEnabled(ids).Select(rule => rule.Name));
        }

        /// <summary>
        /// **只给测试用**：临时往注册表里塞一条规则，返回的令牌 <c>Dispose</c> 时移除。
        ///
        /// <para>
        /// 为什么要留这个口子：用户 2026-09-24 的硬要求是"以后加规则不动界面骨架"，
        /// 而这句话只能靠"塞一条假描述进去，看界面绑定出来的列表里是不是自动多了一条"来证明。
        /// 用 <c>IDisposable</c> 而不是 <c>Add/Remove</c> 两个方法：测试中途抛异常也不会把假规则
        /// 留在注册表里影响别的用例（本方法只允许在测试里调）。
        /// </para>
        /// </summary>
        internal static IDisposable RegisterForTesting(SpecialExtractionRule rule)
        {
            if (rule == null || string.IsNullOrWhiteSpace(rule.Id))
            {
                throw new ArgumentException("假规则必须有 Id", nameof(rule));
            }

            lock (Gate)
            {
                Rules.Add(rule);
            }

            return new Registration(rule.Id);
        }

        private sealed class Registration : IDisposable
        {
            private readonly string _id;

            public Registration(string id)
            {
                _id = id;
            }

            public void Dispose()
            {
                lock (Gate)
                {
                    Rules.RemoveAll(rule => string.Equals(rule.Id, _id, StringComparison.Ordinal));
                }
            }
        }
    }

    /// <summary>
    /// **这一次跑批**用的特定解压快照（不可变）：总开关 + 启用的规则 + 它在定稿规划上的效果。
    ///
    /// <para>
    /// 为什么要一个快照对象而不是每次现读设置：设置是用户随时可改的，一次一键处理会跨很多任务、
    /// 很多轮（续解最多 10 层），跑到一半改设置必须**不影响这一批**（与"本次选项"快照同一口径）。
    /// 另外日志 / 任务详情 / 确认框三处要引用**同一份**结论，不能让它们各自再读一遍设置。
    /// </para>
    /// </summary>
    public sealed class SpecialExtractionPlan
    {
        /// <summary>总开关（①页那个开关；关着时规则清单一律不参与运算）。</summary>
        public bool Enabled { get; init; }

        /// <summary>这批生效的效果（<see cref="SpecialExtractionEffect.None"/> = 与以前逐字相同）。</summary>
        public SpecialExtractionEffect Effect { get; init; } = SpecialExtractionEffect.None;

        /// <summary>这批真正启用的规则（已按注册表顺序；总开关关着时为空）。</summary>
        public IReadOnlyList<SpecialExtractionRule> Rules { get; init; } = Array.Empty<SpecialExtractionRule>();

        /// <summary>什么都没开（默认，也是"与以前逐字相同"的那一档）。</summary>
        public static SpecialExtractionPlan Off { get; } = new();

        /// <summary>这批是不是真的会按某条规则去跑。</summary>
        public bool IsActive => Enabled && Effect != SpecialExtractionEffect.None;

        /// <summary>启用规则的中文名串联（空串 = 一条都没开）。</summary>
        public string RuleNames => string.Join("、", Rules.Select(rule => rule.Name));

        /// <summary>
        /// 按设置造快照（**唯一入口**：总开关 + 规则清单两处口径只在这里合起来）。
        ///
        /// <para>⚠ 只读设置，一个字都不写回去。</para>
        /// </summary>
        public static SpecialExtractionPlan FromSettings(AppSettings? settings)
        {
            if (settings == null || !settings.UseSpecialExtraction)
            {
                return Off;
            }

            IReadOnlyList<SpecialExtractionRule> enabled =
                SpecialExtractionRules.ResolveEnabled(settings.SpecialExtractionRules);

            return new SpecialExtractionPlan
            {
                Enabled = true,
                Effect = SpecialExtractionRules.ResolveEffect(settings.SpecialExtractionRules),
                Rules = enabled
            };
        }

        /// <summary>
        /// 一句话说清"这次按哪条特定规则跑"（任务详情 / 失败清单 / 日志共用；没开返回空串）。
        /// </summary>
        public string Describe()
        {
            return IsActive
                ? string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.SpecialExtractionNoteFormat,
                    RuleNames)
                : string.Empty;
        }

        /// <summary>
        /// 「这条规则这次没按它走 —— 因为几个包共用同一个成品目录」的那一条 WARN。
        ///
        /// <para>⚠ **绝不静默**：用户开了规则却看到老样子，必须能从日志里读到"为什么"。</para>
        /// </summary>
        public string DescribeSkipSharedRoot(string sharedDestination)
        {
            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.SpecialExtractionSkippedSharedRootFormat,
                FirstRuleName(),
                string.IsNullOrWhiteSpace(sharedDestination) ? "（同一个成品目录）" : sharedDestination);
        }

        /// <summary>「这条规则这次没按它走 —— 因为包内有多个并列文件夹」的那一条 WARN。</summary>
        public string DescribeSkipBranch(int folderCount, string sampleNames)
        {
            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.SpecialExtractionSkippedBranchFormat,
                FirstRuleName(),
                folderCount,
                string.IsNullOrWhiteSpace(sampleNames) ? "…" : sampleNames);
        }

        /// <summary>规则一个都没解析出来时的兜底名字（正常路径上不会走到）。</summary>
        private string FirstRuleName() =>
            Rules.Count > 0 ? Rules[0].Name : StatusText.SpecialExtractionName;
    }
}
