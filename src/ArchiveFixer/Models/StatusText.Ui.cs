namespace ArchiveFixer.Models
{
    // StatusText 的一部分（partial）：界面提示、导入语义、解压前提醒、一键处理确认、设置页与功能框详细说明。
    // 成员名、取值与主文件 StatusText.cs 逐字一致；按域拆文件只是为了单文件可读，不是新类型。
    public static partial class StatusText
    {
        // ================================================================
        // 提示文案（**不是状态**）
        // ================================================================
        //
        // ⚠ 下面这一档是**提示文案**，不是 Status 值 —— 别按 AGENTS.md §7 的"三件套"照搬：
        //    · 它**不进状态机**：没有任何代码拿它跟 task.Status 比；
        //    · 它**不参与配色**：StatusToBrushConverter 一行都不用改；
        //    · 它**不参与统计**：TaskSummaryService 的分桶（成功/失败/跳过…）与它无关。
        //    新增一条真正的**状态**（例如"疑似卡住"）才需要同时改那三处；
        //    这里只是"把一句话放进唯一的文案来源里"，免得又散落回中文字面量。

        /// <summary>
        /// 引擎很久没有任何输出时的提示语（默认阈值 90 秒，见
        /// <c>EngineOutputActivityMonitor.DefaultStallThreshold</c>）。
        ///
        /// <para>
        /// 它对应的是用户反复抱怨的"卡死"：界面只有"处理中/完成"两态时，长时间零输出看起来就是死了。
        /// 落在任务上时**只提示、绝不改结论** —— 任务的成败仍由引擎退出码与错误分类决定，
        /// 也不杀进程（要不要中止由用户点「取消当前」决定，不变量 9 / 6）。
        /// 有新的进度或引擎输出时，协调器会把它清回空串。
        /// </para>
        ///
        /// <para>
        /// 它原来临时住在 <c>ArchiveTask.NoResponseHintText</c>（上一批"不许改 StatusText"授权下的权宜之计），
        /// 2026-09-22 归位到这里：<c>ArchiveTask</c> 只保留字段本身。
        /// </para>
        /// </summary>
        public const string LongTimeNoResponse = "长时间无响应";

        /// <summary>
        /// 任务列表上方那条蓝色提示的正文：**界面与命令提示共用的唯一一句话**。
        ///
        /// <para>
        /// 为什么必须共用：2026-09-22 真机验收抓到的缺陷是"提示说一套、代码做一套" ——
        /// 蓝字写着「移除选中」等命令只作用于**勾选**，而清理类命令（删除其余物 / 清理空文件夹）
        /// 实际看的是 DataGrid 的**当前行**（<c>MainViewModel.SelectedTask</c>）。
        /// 把这句话放进唯一的文案来源，XAML 用 <c>{x:Static}</c> 引用、命令提示由
        /// <see cref="NoCheckedTaskPromptFormat"/> 构造，两边就不可能再各写一套。
        /// </para>
        /// <para>
        /// 作用域口径（与 <c>MainViewModel.ResolveCleanupTargets</c> 的实际行为一一对应）：
        /// <b>一律只认勾选</b> —— 一个都没勾就什么都不做，只提示先勾选。
        /// ⛔ 用户 2026-09-24 第 12 条明确否掉了"一个都没勾时退回当前点中的那一行"这条兜底
        /// （原话："你只需要操作我选中的文件，其他的不用管"），**不要再加回来**。
        /// </para>
        /// </summary>
        public const string SelectionScopeHint =
            "「一键处理 / 只解压 / 智能修正 / 移除选中 / 删除其余物 / 清理空文件夹」一律只认勾选（最左侧一列）；" +
            "一个都没勾就什么都不做，只提示你先勾选。" +
            "右键菜单只作用于当前这一行；「清空列表」是整表操作（与勾选无关）。";

        /// <summary>
        /// "一个都没勾"时的唯一提示模板（<c>{0}</c> = 命令名，<c>{1}</c> = 列表里有多少个任务）。
        ///
        /// <para>用户 2026-09-24 第 12 条亲自定的口径：**只提示、什么都不做**。整句以
        /// "没有勾选任何任务"开头（用户原话里的说法），后半句说清两件事 ——
        /// 这些命令只处理勾选的、以及怎么勾。用它替代原来的 <c>PickTaskPromptFormat</c>
        /// （那句写着"请先勾选或点中一个任务"，教的正是已经被否掉的兜底）。</para>
        ///
        /// <para>用 <c>static readonly</c>（而不是 <c>const</c>）只因为拼了
        /// <see cref="Environment.NewLine"/>；它仍然只在这一个地方定义。</para>
        /// </summary>
        public static readonly string NoCheckedTaskPromptFormat =
            "没有勾选任何任务，已取消「{0}」（一个文件都没动）。" + Environment.NewLine + Environment.NewLine +
            "列表里有 {1} 个任务，当前一个都没勾。这个命令只处理你勾选的任务：" +
            "在最左侧一列勾上要处理的那些，再点一次（在列表上按 Ctrl+A 可以全选）。";

        /// <summary>配套的日志行（提示框与日志说同一件事，事后排查不会对不上）。</summary>
        public const string NoCheckedTaskLogFormat =
            "没有勾选任何任务：本次一个任务都不处理（列表里没有勾选项，已取消）。";

        /// <summary>列表本来就是空的时候的那句提示（与"没勾选"是两回事，别混）。</summary>
        public const string TaskListEmptyPrompt =
            "任务列表是空的。先点「添加文件 / 添加文件夹」把要处理的包加进来。";

        // ================================================================
        // 导入语义：替换 vs 追加（用户 2026-09-24 第 12 条补拍）
        // ================================================================
        //
        // ⚠ 这一组是**界面提示的唯一来源**：菜单项 ToolTip 用 {x:Static} 引用、
        //    导入日志由 ScanCoordinator 构造。**必须写清"要往现有列表里加，
        //    用『追加到列表』"** —— 这是用户拍板"添加 = 清空重来"时明确要求写进界面的那句话。

        /// <summary>「添加文件 / 添加文件夹」的界面提示（ToolTip + 日志共用）。</summary>
        public const string AddReplacesListHint =
            "添加 = 先清空整张任务列表再加入你这次选的内容（替换语义）。" +
            "要往现有列表里加，用「文件 → 追加到列表」。";

        /// <summary>「追加到列表」的界面提示。</summary>
        public const string AppendToListHint =
            "追加 = 保留列表里现有的任务，只把你这次选的内容加到末尾（与「添加」的替换语义相反）。";

        /// <summary>
        /// 导入日志：**替换**语义（<c>{0}</c> = "，已清掉原有 N 个任务"或空串）。
        /// 日志里也必须说清这次用的是哪一种语义 —— 用户事后要能回答"我上一批怎么不见了"。
        /// </summary>
        public const string ImportReplaceLogFormat =
            "开始导入路径（替换语义：先清空整张表再添加{0}）。要往现有列表里加，用「文件 → 追加到列表」。";

        /// <summary>导入日志：**追加**语义。</summary>
        public const string ImportAppendLogFormat =
            "开始导入路径（追加语义：保留列表里现有的任务，新任务加到末尾）。";

        /// <summary>导入日志：清掉了多少个旧任务（拼在 <see cref="ImportReplaceLogFormat"/> 里）。</summary>
        public const string ImportReplacedTasksTextFormat = "，已清掉原有 {0} 个任务";

        /// <summary>导入日志：新增了多少个任务。</summary>
        public const string ImportFinishedLogFormat = "导入完成，新增任务 {0} 个。";

        // ================================================================
        // 解压前的提醒（无用物 / 无可用密码）—— 用户 2026-09-22 需求第 8 条
        // ================================================================
        //
        // ⚠ 这一组是**弹窗正文 + 日志行 + 文档**的唯一措辞来源，而且三段话的顺序不能改：
        //    ① 这些文件是什么（打包者附带的说明 / 网址 / 工具 / 广告）→
        //    ② 本程序不会动它们（不删、不改名、不搬走）→
        //    ③ 解压完成后由用户自己判断要不要删。
        //    漏掉 ② 就会出现"程序要去删我的文件"这种与事实相反的暗示；
        //    漏掉 ③ 用户会以为必须当场做决定。

        /// <summary>提醒框的标题。</summary>
        public const string JunkReminderTitle = "解压前的提醒";

        /// <summary>主按钮：照常处理这一批。</summary>
        public const string JunkReminderYesText = "继续处理";

        /// <summary>次按钮：先不处理（回到列表，任务与源包一个字节都不会动）。</summary>
        public const string JunkReminderNoText = "先不处理";

        /// <summary>
        /// 可选项位：本次运行内不再弹这个提醒。
        /// **只存在内存里的一个标记**（<c>ExtractionCoordinator</c> 的实例字段），
        /// 绝不写进设置文件 —— 用户勾的是"这次别再烦我"，不是"以后永远别提醒"。
        /// </summary>
        public const string JunkReminderOptionText = "本次运行不再提示这类提醒";

        /// <summary>开场一句：先把性质说清（不是错误），免得用户以为出事了。</summary>
        public const string JunkReminderIntro = "这次解压动手之前，先说两件事（都不是错误，也不影响解压本身）：";

        /// <summary>① 段标题：源目录里那些"可能是打包者附带的文件"。</summary>
        public const string JunkReminderJunkHeader =
            "① 这些源目录里有一些文件，很可能是打包者附带的说明 / 网址 / 工具 / 广告之类的诱饵"
            + "（本程序只按文件名 + 魔数判了个大概，不保证它们真的没用）：";

        /// <summary>① 段结尾：**程序不会动它们** + 解压完由用户自己判断（用户点名要写清的两件事）。</summary>
        public const string JunkReminderJunkFooter =
            "上面这些文件本程序一个都不会动（不删、不改名、不搬走）；"
            + "解压完成后你可以自己看一眼，再决定要不要删。";

        /// <summary>① 段：这次一共认出多少个（列出来的最多 10 条，其余只报个数）。</summary>
        public const string JunkReminderJunkCountFormat = "这次一共认出 {0} 个：";

        /// <summary>无用物还有多少个没列出来（上限见 <c>SourceJunkScanner.MaxReportedItems</c>）。</summary>
        public const string JunkReminderJunkMoreFormat = "  …还有 {0} 个（无用物最多列 10 条）";

        /// <summary>撞到扫描上限（每个目录 2000 个文件 / 魔数体检预算）时如实说明，不假装扫全了。</summary>
        public const string JunkReminderTruncatedNote = "  （源目录里的文件太多，本次只核对了前一部分）";

        /// <summary>② 段标题：需要密码、但当前一个可用候选都没有的包。</summary>
        public const string JunkReminderPasswordHeaderFormat =
            "② 本批有 {0} 个包需要密码，但当前一个可用候选都没有"
            + "（密码本为空 / 映射式没命中 / 没设统一密码）：";

        /// <summary>② 段：说清继续会发生什么（"即使密码本已预加载也可能没覆盖到"的那种包）。</summary>
        public const string JunkReminderPasswordOutcome =
            "它们大概率会以「密码错误」或「达到密码尝试上限」结束 —— 既不是成功，也不是文件损坏。";

        /// <summary>② 段：没列出来的包还有多少个（列表最长 10 个）。</summary>
        public const string JunkReminderPasswordMoreFormat = "  …还有 {0} 个（最多列 10 个）";

        /// <summary>② 段：给出口（两个按钮各自会怎样 + 去哪儿补密码）。</summary>
        public const string JunkReminderPasswordExit =
            "两条路：① 点「" + JunkReminderNoText + "」回到列表，把它们从勾选里去掉（或者先补好密码）再重跑；"
            + "② 点「" + JunkReminderYesText + "」照常开始 —— 程序紧接着还会问你要不要手动输一个密码，"
            + "也可以先去菜单「导入密码本…」把它补进去。";

        /// <summary>批首那条日志的骨架：**两段结论都落进日志**（数量 + 前几条名字）。</summary>
        public const string JunkReminderLogFormat = "解压前提醒：① 无用物 {0}；② 无可用密码的包 {1}。";

        /// <summary>某一段"有 N 个，例如 …"的写法（喂给 <see cref="JunkReminderLogFormat"/>）。</summary>
        public const string JunkReminderLogFoundFormat = "{0} 个（例如 {1}）";

        /// <summary>某一段一个都没有。</summary>
        public const string JunkReminderLogNoneText = "无";

        /// <summary>
        /// 无界面宿主（单测 / 控制台宿主）时的那条日志。
        ///
        /// <para>它解释的是"为什么没人点过、这一批却照常跑了"：这条提醒的降级方向是
        /// **继续**（见 <c>DialogService.ShowReminderConfirm</c>），与破坏性确认的降级方向
        /// （false = 取消）正好相反 —— 否则所有无界面管线都会被这条纯提示拦住。</para>
        /// </summary>
        public const string JunkReminderNoHostLog =
            "当前宿主没有界面：解压前的提醒不弹窗、按「" + JunkReminderYesText + "」放行（只写日志）。";

        /// <summary>用户勾了「本次运行不再提示」。</summary>
        public const string JunkReminderOptionLog =
            "已记下「" + JunkReminderOptionText + "」：本次运行内不再弹这个提醒（只在内存里，不写设置文件）。";

        /// <summary>用户在提醒里选了「先不处理」：这一批没有开始。</summary>
        public const string JunkReminderDeclinedLog =
            "已按「" + JunkReminderNoText + "」处理：这一批没有开始（任务、源包、输出目录一个字节都没动）。";

        // ================================================================
        // 一键处理的**那一个**确认框（用户 2026-09-24 第 17 条）
        // ================================================================
        //
        // 用户原话："现在规定点击完一键处理，就只能有一个弹窗提醒，而且这个可以选中以后不弹出……
        // 弹窗里面的东西改为『内容物会生成在什么地方，而且其余物是否自动删除』……
        // 为什么我一键处理还没有开始确认，你的进度条就开始动了"
        //
        // ⚠ 这一组是那个框的**唯一措辞来源**（AGENTS.md §7）。三条纪律：
        //    ① 正文只回答他点名的那两件事（内容物落点 / 其余物是否自动删除），其余进折叠区；
        //    ② 「以后不再询问」是**落盘的**设置（SkipOneClickConfirm），界面上留开关能再打开；
        //    ③ 无用物与"没有可用密码"两段**并进这一个框**（不再弹第二个），措辞仍引用
        //       上面 JunkReminder* 那一组里的事实，不另写一套判据。

        /// <summary>确认框标题（← 旧面板叫「一键处理 · 本次选项」）。</summary>
        public const string OneClickConfirmTitle = "一键处理 · 确认";

        /// <summary>第一件事：内容物会生成在什么地方。</summary>
        public const string OneClickConfirmDestinationLabel = "内容物会生成在：";

        /// <summary>第二件事：最后一层过程物（仅失败 / 部分完成）。</summary>
        public const string OneClickConfirmRestLabel = "最后一层过程物（仅失败时）：";

        /// <summary>其余物这一档：删除操作=彻底删除 → 失败/部分完成后彻底删掉（不可恢复）。</summary>
        public const string OneClickConfirmRestAutoDelete = "彻底删除（不可恢复）";

        /// <summary>其余物这一档：删除操作=留下（默认）。</summary>
        public const string OneClickConfirmRestKeep = "留下（默认）";

        /// <summary>第三行（短）：源包怎么处理。</summary>
        public const string OneClickConfirmSourceLabel = "源包：";

        /// <summary>
        /// 确认框里那条**常驻提醒**：程序不会判断"内容物里该不该有压缩包"（用户 2026-09-26 要求）。
        ///
        /// <para>他的原话（我按他的意思整理成界面上能站得住的一段）："我们没有压缩包内容识别操作，
        /// 很有可能您最终想要得到的内容物里面含不该解开的压缩文件，请您仔细判别，
        /// 并在解压方式里面调节一次解压的检测次数"。</para>
        ///
        /// <para>⚠ 为什么必须写在**动手之前**：嵌套只看②「解压方式」→「嵌套与覆盖」里的最大嵌套层数
        /// （默认 7）；层数够深时，本来想留着的那几个内层包会被一路解开。
        /// 程序不去猜“这个包该不该有内层包”，但必须在动手前把这件事说清、并指出去哪儿调。</para>
        /// </summary>
        public const string OneClickConfirmNestedCaveat =
            "⚠ 这一步不会判断「内容物里该不该有压缩包」：如果最终想要的内容物里面本来就放着压缩包，"
            + "程序会按②「解压方式」页 →「嵌套与覆盖」里的最大嵌套层数继续解，它们会被一起解开。"
            + "请先看清这个包；不想展开时，到那一栏把层数调小，或用「内容物保留关键词」把内层包挡掉。";

        /// <summary>可选项位：以后不再询问。</summary>
        public const string OneClickConfirmSuppressText = "以后不再询问，按当前设置直接开始（可在⑥「提醒」页再打开）";

        /// <summary>折叠区标题：要改就展开（默认收起 —— 用户嫌旧面板啰嗦）。</summary>
        public const string OneClickConfirmExpanderHeader = "本次改一下（落点 / 源包操作 / 删除操作）";

        /// <summary>多包时那一行的后缀：每个包各建一个同名子文件夹。</summary>
        public const string OneClickConfirmMultiPerArchiveFormat = "（本批 {0} 个包，每个包各建一个同名子文件夹）";

        // ================================================================
        // 工作区残留（用户 2026-09-24 第 22 条）
        // ================================================================
        //
        // 起因：用户实测在 <程序目录>\data\work 下攒了 10 个目录 / 5.7 GB，问"为什么失败后你会留下这个残留"。
        // 答案有两半，两半都要写进界面文案：
        //   ① **保留是故意的**：失败 / 取消时工作区里是用户唯一的一份产物线索（不变量 12/13），
        //      自动删掉是不可逆的错误；
        //   ② **但以前只写一行日志**（没体积、没入口），等于没告诉用户 —— 这一组文案把它补成"看得见、点得动"。

        /// <summary>启动 / 刷新时那条日志（几个 + 共多大 + 在哪）。</summary>
        public const string WorkspaceLeftoverLogFormat = "发现 {0} 个工作区残留（上次失败 / 取消留下的，共 {1}），位置：{2}";

        /// <summary>
        /// 紧跟其后的一句：为什么不自动删、去哪儿清。
        ///
        /// <para>⚠ 2026-09-25 第 25 条之后，"怎么会留下东西"只有两种可能（默认档一个都不留），
        /// 这句话必须把两种都说出来 —— 否则用户会以为"程序明明说失败不留残留，怎么又冒出来了"。</para>
        /// </summary>
        public const string WorkspaceLeftoverHintLog =
            "程序不会自动删它们（只可能是这两种来路：打开了「失败时保留中间产物」，"
            + "或者上一次被强杀 / 断电来不及收尾）；要清就在③「清理方式」页点「清理工作区」，删前会再确认一次。";

        /// <summary>界面上那一行（常驻提示；没有残留时整行不显示）。</summary>
        public const string WorkspaceLeftoverBannerFormat = "工作区残留：{0} 个目录，共 {1}";

        /// <summary>清理确认框的标题。</summary>
        public const string WorkspaceCleanupConfirmTitle = "清理工作区";

        /// <summary>确认框正文（几个 + 共多大）。</summary>
        public const string WorkspaceCleanupConfirmFormat = "要删掉这 {0} 个工作区目录（共 {1}）吗？";

        /// <summary>确认框明细区的小标题。</summary>
        public const string WorkspaceCleanupConfirmDetailHeader = "会被删掉的目录（都在工作区根目录之下）：";

        /// <summary>确认框明细区的收尾：说清不动什么 + 不可还原。</summary>
        public const string WorkspaceCleanupConfirmDetailFooter =
            "删掉之后无法还原；工作区根目录下的散文件、日志、密码列表、设置一律不碰。";

        /// <summary>清理之后的日志（删掉几个 / 失败几个 / 释放多少）。</summary>
        public const string WorkspaceCleanupResultLogFormat = "工作区清理：删掉 {0} 个、失败 {1} 个，释放 {2}。";

        /// <summary>没有可清理的残留时的提示（用户点了按钮但确实没什么可清）。</summary>
        public const string WorkspaceCleanupNothingText = "现在没有可清理的工作区残留。";

        // ================================================================
        // ①「任务」页的「输出位置」那一格（用户 2026-09-25 第 27 条）
        // ================================================================
        //
        // 用户原话："输出的指定位置可以放在主界面进行选择，这个没有问题，选项卡里面的也可以留着，
        // 在添加文件夹和全选中间还有那么多的位置，如果选择解压到位置名字太长，可以简写……
        // 反正我要求的就是我们最好能够看到完整的解压地址"。
        //
        // 落点：①页主操作条「添加文件夹」与「全选」之间那一格（Grid 的星号列），显示 + 选择 + 一个开关。
        // 真值只有一份：MainViewModel.SelectedOutputDirectory（= Settings.CustomOutputDirectory）
        // 与 Settings.ExtractToOriginalDirectory —— 与②「解压方式」页是同一个值，两边同时变。
        // 长路径中间省略（Helpers/PathMiddleEllipsis），**完整路径在 ToolTip 与右键菜单里**。

        /// <summary>那一格的前缀。</summary>
        public const string OutputLocationLabel = "输出位置：";

        /// <summary>选位置按钮（与②页那个「选择」同一个动作、同一份值）。</summary>
        public const string OutputLocationChooseButtonText = "选择…";

        /// <summary>开关：勾上 = 不指定统一位置（产物落在每个包自己所在的目录）。</summary>
        public const string OutputLocationFollowsArchiveLabel = "未指定位置";

        /// <summary>它的提示（说清两件事：落在哪、与②页是同一个设置项）。</summary>
        public const string OutputLocationFollowsArchiveHint =
            "勾上 = 产物落在每个包自己所在的目录（与②「解压方式」页的落点是同一个设置项）。"
            + "取消勾选后点「选择…」挑一个统一的位置；两处改哪一处，另一处立刻跟着变。";

        /// <summary>未指定位置时那一行的显示（不是空白，必须说清东西会落在哪）。</summary>
        public const string OutputLocationUnspecifiedText = "（未指定：产物落在每个包自己的目录）";

        /// <summary>取消勾选但还没挑目录时的显示（说清下一步点哪里）。</summary>
        public const string OutputLocationNotChosenText = "（还没选位置 —— 点「选择…」挑一个目录）";

        /// <summary>完整路径的 ToolTip（界面上显示的是中间省略过的，这里给全文）。</summary>
        public const string OutputLocationToolTipFormat = "完整路径：{0}";

        /// <summary>右键菜单：把完整路径（不省略）复制走。</summary>
        public const string OutputLocationCopyMenuText = "复制完整路径";

        /// <summary>复制成功 / 没有可复制内容的两条日志。</summary>
        public const string OutputLocationCopyLogFormat = "已复制输出位置：{0}";

        /// <summary>没有指定位置时点复制：如实说清，不假装复制成功。</summary>
        public const string OutputLocationCopyNothingLog = "现在是「未指定位置」，没有可复制的输出路径。";

        /// <summary>①页那个开关打开时的一条日志（说清东西会落在哪，不让人自己猜）。</summary>
        public const string OutputLocationSwitchedToOriginalLog =
            "输出位置：未指定 —— 产物落在每个包自己所在的目录。";

        /// <summary>①页那个开关关掉时的一条日志（提醒下一步点哪里，否则落点会变成"没填路径的指定位置"）。</summary>
        public const string OutputLocationSwitchedToCustomLog =
            "输出位置：改为指定位置（请点「选择…」挑一个目录）。";

        // ================================================================
        // 手动档「解压到当前文件夹」（用户 2026-09-27 拍板）
        // ================================================================
        //
        // 用户原话："再加一个解压到当前文件夹的功能（WinRAR 右键那种）"。
        // 语义 = `111\222.rar` → `111\内容物`：**不建包名那一层**，内容物直接落在源包所在的那个目录。
        //
        // ⚠ 三条边界（措辞在这里，判据在 ExtractionCoordinator._extractIntoSourceFolderThisRun）：
        //    ① **只有这一个按钮**会摊平 —— 一键处理 / 继续解 / 批量一律套包名那一层（用户红线）；
        //    ② 摊平更容易撞名，同名照走当前冲突档（用户明确接受这个代价，不是我们替他兜的底）；
        //    ③ 它是个动作而不是设置项：不进 appsettings.json，关掉程序就没了。

        /// <summary>①页「手动操作」里那个按钮的文案（与 WinRAR 右键同一句话，用户点名要这个语义）。</summary>
        public const string ExtractIntoSourceFolderText = "解压到当前文件夹";

        /// <summary>按钮的 ToolTip：一句话说清它会少一层、以及撞名怎么办。</summary>
        public const string ExtractIntoSourceFolderToolTip =
            "只对这次点击生效：内容物不建包名那一层，直接落在压缩包自己所在的那个文件夹里"
            + "（111\\222.rar → 111\\内容物）。同名文件 / 文件夹按当前的「解压覆盖策略」处理。"
            + "一键处理不会这么做 —— 那条路永远套一层包名文件夹。";

        /// <summary>点了按钮之后写进日志的那条提醒（事后能回答"这批为什么没有包名那一层"）。</summary>
        public const string ExtractIntoSourceFolderReminderLog =
            "本次按「解压到当前文件夹」执行：内容物不建包名那一层，直接落在源包所在目录"
            + "（111\\222.rar → 111\\内容物）；同名冲突按当前覆盖策略处理。"
            + "这个选择只对本次点击有效，没有写进设置。";

        // ================================================================
        // 「继续解」（用户 2026-09-24 第 16 条追加：一键解到尽头 + 硬上限 10 层 + 到顶一键继续）
        // ================================================================

        /// <summary>到顶时那个按钮的文案（带还剩几个内层包）。</summary>
        public const string ContinueOneClickTextFormat = "继续解（还有 {0} 个内层包）";

        /// <summary>到顶时列表上方那一行提示（与按钮同一份事实）。</summary>
        public const string ContinueOneClickHintFormat =
            "已达到每批 {0} 层的上限，还剩 {1} 个内层包没解（已经勾好）—— 点「继续解」接着解。";

        // ================================================================
        // 导入时的无用物处理 + 列表里删无用物
        // ================================================================
        //
        // 用户 2026-09-24 第 15 条要的是"列表要能删无用物"；2026-09-28 他把时机收得更死：
        // 原话「为什么不在检测到的时候就直接移除」「当选择文件的时候，移除无用物的弹窗，没有弹出来
        // 我还以为你又没弄好，但是在点击一键处理之后，他一出来，但是在我点击之前他一直还是勾选着，
        // 你为什么要这样」——于是**导入一完成就自动把无用物移出任务列表**，
        // ⛔ 那条「导入后提醒」的弹窗已经退休（相关文案一并删除，别再加回来）。
        //
        // 与 §9.7「解压前的提醒」仍是**同一个判据**（SourceJunkScanner）；程序对无用物依旧
        // **一个都不动**（不删/不改名/不搬），"移出列表"动的只是任务列表。
        //
        // ⚠ 2026-10-04 用户真机：「要删除的是第二个你已经停止显示的，就是底层自动会做的」
        // ⇒ ③页那一格（`SettingsRemindJunkAfterImportLabel/Hint`）**连同两条文案一起删掉**：
        // 一个"写着已停用、改了什么都不影响"的开关对用户只是噪声。
        // ⛔ `AppSettings.RemindJunkAfterImport` 属性**留着**（不动设置序列化：用户盘上那份
        // appsettings.json 里这个键照旧读得进来，只是安静忽略）；⛔ 那句"已把 N 个无用物移出列表"
        // 的**日志保留**（用户：「提示还是要有的」）。

        /// <summary>
        /// 导入完成时**自动**把无用物从任务列表里移出（用户 2026-09-28）。
        /// <c>{0}</c> = 移掉几个，<c>{1}</c> = 前几个名字（最多 5 个）。
        /// </summary>
        public const string ImportJunkRemovedLogFormat =
            "导入完成：已自动把 {0} 个无用物从任务列表里移出（它们对解压没用；源文件一个字节都没动）：{1}";

        /// <summary>
        /// 无用物自动剔除的通用日志模板（**保留给未指定模板的调用方**）。
        ///
        /// <para>⚠ 2026-11-01：一键处理开工时**不再**剔无用物（用户口径：只对文件夹导入做一次）。
        /// 现在唯一的自动剔除调用点是文件夹导入，它显式传 <see cref="ImportJunkRemovedLogFormat"/>。</para>
        /// <c>{0}</c> = 移掉几个，<c>{1}</c> = 前几个名字（最多 5 个）。
        /// </summary>
        public const string OneClickJunkRemovedLogFormat =
            "一键处理：先把 {0} 个无用物从列表里移掉（它们对解压没用；源文件一个字节都没动）：{1}";

        /// <summary>列表里手动移除任务（右键 / 「移除勾选的」）。</summary>
        public const string RemoveTasksLogFormat = "已从任务列表里移除 {0} 个（源文件一个字节都没动）。";

        /// <summary>一个都没勾时点「移除勾选的」。</summary>
        public const string RemoveCheckedTasksNoneText = "没有勾选任何任务，没有可移除的。";

        /// <summary>③ 页「工作区残留」那一组的标题。</summary>
        public const string WorkspaceLeftoverGroupHeader = "工作区残留（失败 / 取消留下的中间产物）";

        /// <summary>③ 页「工作区残留」那一组的说明（为什么默认一个都不留、什么情况下才会看见东西）。</summary>
        public const string WorkspaceLeftoverGroupHint =
            "默认档下只有两种情况会留下东西：① 你在下面打开了「失败时保留中间产物」（排查用）；"
            + "② 程序被强杀 / 断电，来不及收尾。这里列出它们占了多少空间，确认之后可以一次清掉 —— "
            + "只删工作区根目录下的那些任务目录，日志、密码、设置一律不碰。";

        /// <summary>② 「解压方式」页那个开关的文案（第 17 条：一键处理的那一个确认框可关）。</summary>
        public const string SettingsSkipOneClickConfirmLabel = "一键处理前不再弹确认框（直接按当前设置开始）";

        /// <summary>它的说明。</summary>
        public const string SettingsSkipOneClickConfirmHint =
            "默认关（每次都弹一个确认框，正文是「内容物会生成在什么地方」+「其余物是否自动删除」）。"
            + "勾上之后一键处理直接开始，一个框都不弹；这里可以随时取消勾选，把确认框要回来。";

        // ── ④「密码」页：记住密码列表 + 已记住的密码本 ──

        /// <summary>设置里那个开关的文案。**必须写明"加密存在程序目录 + 跟 Windows 账号无关"**。</summary>
        public const string SettingsRememberPasswordListLabel = "记住密码列表（加密保存在程序目录，跟 Windows 账号无关）";

        /// <summary>
        /// 开关下面那行副作用说明（用户 2026-09-24 要求：代价与关闭后的行为都要写在界面上）。
        /// </summary>
        public const string SettingsRememberPasswordListHint =
            "开：列表的内容、顺序、启用状态、手动添加的条目会一起加密存到 程序目录\\data\\password-list.dat，"
            + "下次启动原样恢复。用的是 Windows 自带的 DPAPI 机器范围加密 —— 跟 Windows 账号无关（同机换谁登录都能用、不弹框），"
            + "代价是：同机任何本机用户都可能解开它；换机器或重装系统解不开，那时程序会忽略并提示，"
            + "可以用「写回密码本…」把列表带走。关：既不写也不读这个文件，重启回到纯内存（磁盘上已有的那份不会被删）。";

        /// <summary>"已记住的密码本"那一组的标题。</summary>
        public const string SettingsRememberedBooksTitle = "已记住的密码本（启动时按这个顺序逐本合并）";

        /// <summary>一本都没记住时的说明。</summary>
        public const string SettingsRememberedBooksEmptyText = "还没有记住任何密码本 —— 启动时不会自动加载任何密码本。";

        /// <summary>"已记住的密码本"那一组的说明。</summary>
        public const string SettingsRememberedBooksHint =
            "这些文件是你自己点过「导入密码本」的那些。启动时按这个顺序逐本读取，只补列表里还没有的密码；"
            + "移除一项只是不再自动加载它，磁盘上的文件一个字节都不会动。";

        /// <summary>移除一本的按钮文案。</summary>
        public const string SettingsRememberedBooksRemoveButtonText = "移除";

        /// <summary>移除一本的按钮提示。</summary>
        public const string SettingsRememberedBooksRemoveButtonHint =
            "不再自动加载这一本（文件本身不会被删、不会被改）。";

        /// <summary>设置里"记住密码列表"关着时，这一组的降级说明。</summary>
        public const string SettingsRememberedBooksDisabledText =
            "「记住密码列表」关着：这份清单这次不生效（密码列表窗口里的改动不会跨重启保留）。";

        // ── 清理与删除页：解压前的提醒开关（用户 2026-09-24 第 15 条的界面入口） ──

        /// <summary>
        /// 「解压前提醒无用物」这个开关的标签。
        ///
        /// <para>
        /// 提醒本身早就有了（AGENTS.md §9.7：每次解压前列出可能的无用物与"没有可用密码"的包），
        /// 但一直只能在弹窗里勾"本次运行不再提示"（只记内存）。用户第 15 条要求它可关，
        /// 于是补上这个设置项 —— 关掉就是**不再扫、不再弹**。
        /// </para>
        ///
        /// <para>⚠ 2026-10-04 用户真机：「你这个提醒里面第一个是什么意思，我现在打勾，是开还是关，
        /// 我自己都看不懂」⇒ 标签里**明写勾选的方向**（勾上 = 开启提醒），⛔ 不许再只写一个名词短语
        /// 让用户自己猜 —— 复选框既能表示"开启某功能"也能表示"以后不再提醒"，光看标签猜不出来。</para>
        /// </summary>
        public const string SettingsRemindBeforeExtractLabel =
            "解压前提醒可能的无用物与没有可用密码的包（勾上 = 开启提醒）";

        /// <summary>开关下面那一行说明（**第一句就说清这个勾是开还是关**，再说什么被提醒、提醒出现在哪）。
        ///
        /// <para>⚠ 2026-10-05 用户真机追问：「那弹窗提示呢，还是你只有在日志里面输出」—— 他勾上了却
        /// 只看到日志，于是读成"这个勾没用"。事实是**提醒能弹框的场合只有一档**：手动「只解压」且这一批
        /// 真有东西要提醒；一键处理的提醒**并进那唯一的确认框**（用户 2026-09-24 第 17 条："只能有一个
        /// 弹窗"），勾过「以后不再询问」（**落盘记住的**）时一个框都不弹。⇒ 用户拍板**只改说明、行为一个字
        /// 不动**：说明里必须点明"提醒出现在哪"，⛔ 不许让用户靠试。⚠ 这一档与 `ExtractionCoordinator.
        /// ConfirmBatchRemindersAsync` / `OneClickCoordinator` 里那三条分支逐条对应，改行为时同步改这里。</para>
        /// </summary>
        public const string SettingsRemindBeforeExtractHint =
            "这个勾就是开关：勾上 = 每次解压前先提醒一次；取消 = 不提醒（连那一遍扫描都不做）。"
            + "提醒的内容：源目录里「看起来像打包者附带的说明 / 网址 / 工具」的文件，"
            + "以及「一个可用密码都没有」的包 —— 只是列出来让你先看一眼再决定，"
            + "这两类提醒都不影响解压本身，程序也不会去动那些文件。默认勾上。"
            + "提醒出现在哪：手动「只解压」时弹一个提醒框（这一批真有东西要提醒才弹）；"
            + "一键处理时不单独弹，内容并进那唯一的确认框；"
            + "若在确认框上勾过「以后不再询问」，就一个框都不弹，只在日志里列出提醒内容。";

        /// <summary>关掉提醒时写一条日志：事后能判断"这次为什么没弹提醒"。</summary>
        public const string RemindBeforeExtractDisabledLog =
            "设置里关掉了「解压前提醒」：本次跳过无用物 / 无可用密码的扫描与提醒。";

        // ── 关于 / 版本 / 许可（帮助 → 关于 与⑥设置页共用） ──

        /// <summary>
        /// 「关于」里的许可那一段。
        ///
        /// <para>
        /// 必须与 LICENSE 的第三方组件声明同口径：内置 7-Zip 是 LGPL（+ unRAR 限制条款）、
        /// 内置 UnRAR 是 RARLAB freeware（允许随包分发），而 <c>Rar.exe</c>/<c>WinRAR.exe</c>
        /// 是共享软件、**绝不随包分发**（AGENTS.md §3.1）。用户问"这东西能不能商用 / 能不能带走"时，
        /// 答案就在这里，不必去翻仓库。
        /// </para>
        /// </summary>
        public const string AboutLicenseText =
            "许可：本程序 MIT。" + "\r\n"
            + "内置 7-Zip（LGPL + unRAR 限制条款）、内置 UnRAR（RARLAB freeware，许可明确允许随包分发）。" + "\r\n"
            + "Rar.exe / WinRAR.exe 是共享软件：程序只检测与调用你自己装的那一份，绝不复制、绝不随包分发，" + "\r\n"
            + "也绝不会拿 7-Zip 假装做出 .rar。";

        // ================================================================ 功能框「展示框」的详细说明
        //
        // 用户 2026-10-14 最终设计：「所有说明性文字，详细的说明文字全部写入选项卡说明里面和在每一个
        // 功能框最下面有一个展示框，点击才能详细展示，平时的功能介绍只能简洁的文字介绍」。
        //
        // ⛔ 规则：页面上只留简洁的一句；凡是"要解释清楚才敢用"的那几段，一律搬到这里，
        //    由 `Views/Controls/BoxDetail.xaml` 渲染（默认收起）。⛔ 文案只有这一处，XAML 里不许再抄。

        /// <summary>① 源包操作那一框的展示框标题。</summary>
        public const string BoxDetailSourceHandlingHeader = "详细说明：源包什么时候才会被动";

        /// <summary>① 源包操作：三条同时成立才动源包（原先是常驻在页面上的那段）。</summary>
        public const string BoxDetailSourceHandlingDetail =
            "只有「内容物已定稿 + 输出校验通过 + 未取消」三条同时成立才动源包；失败 / 部分完成 / 取消时源包一律原地不动。";

        /// <summary>③ 手工清理那一框的展示框标题。</summary>
        public const string BoxDetailManualCleanupHeader = "详细说明：手工删除的默认档是什么";

        /// <summary>③ 手工清理：默认档只是确认框的默认值（原先是常驻的那段）。</summary>
        public const string BoxDetailManualCleanupDetail =
            "「手工删除时的默认」只是确认框的默认值，不是「跳过确认」；回收站不可用时（网络盘 / 策略禁用）一律不删并说明原因，绝不会因此降级成彻底删除。";

        /// <summary>② 并发与空间那一框的展示框标题。</summary>
        public const string BoxDetailSpaceGateHeader = "详细说明：空间门怎么算";

        /// <summary>② 并发与空间：空间门的算法与"取不到可用空间"时怎么办（原先是常驻的那段）。</summary>
        public const string BoxDetailSpaceGateDetail =
            "空间门：每个任务启动前都按「源包 + 内容物 + 其余物增量 + 512 MiB 余量」核算；不够就不启动，并写明需要多少、可用多少、差多少。取不到目标盘可用空间时「不拦」，但会在日志里明说取不到。";

        /// <summary>⑤ 打包那一框的展示框标题。</summary>
        public const string BoxDetailPackingConfirmHeader = "详细说明：点「打包…」之后会发生什么";

        /// <summary>⑤ 打包：确认窗里问什么、记不记得住（原先是常驻的那段）。</summary>
        public const string BoxDetailPackingConfirmDetail =
            "点「打包…」还会弹一个小窗：确认原包操作 + 其余物操作（都会记住，下次照上次的来；落点在上面选）。";

        /// <summary>⑤ 打包密码那一框的展示框标题。</summary>
        public const string BoxDetailPackingPasswordHeader = "详细说明：本次打包密码会不会进密码本";

        /// <summary>⑤ 打包：密码只用于本次打包，避免用户误以为会同步到 passwords.txt。</summary>
        public const string BoxDetailPackingPasswordDetail =
            "本次输入的打包密码只用于这次打包，不写进 passwords.txt，也不自动加到④「密码」页的列表里。"
            + "需要下次继续使用，请在打包前到④「密码」页添加；「从密码列表选…」只是把列表里的一条复制到这个框。";

        /// <summary>⑤ 打包步骤那一框的展示框标题。</summary>
        public const string BoxDetailPackingStepsHeader = "详细说明：打包流程分哪几步";

        /// <summary>⑤ 打包：四步走法 + 每步的产物与校验（原先是常驻的那段）。</summary>
        public const string BoxDetailPackingStepsDetail =
            "1）切分卷：把源里的东西用内置 7-Zip 打成加密分卷（-mhe 连文件名一起加密；内容 <1 GiB 切 2 卷、≥1 GiB 切 3 卷，前面每卷装满、最后一卷是零头）。\n"
            + "2）分卷就地落盘：分卷直接落在上面那个落点目录里（不再套一层同名文件夹），名字 = 源名 + .7z.001/.002…，撞名自动让位成 源名(1).7z.001。\n"
            + "3）做外层容器：把上面那几个分卷压成一个带密码的 rar（本机没装 WinRAR 时自动用 7z，产物就是 .7z）；分卷在归档里是「顶层」的，解出来就是这几个分卷。\n"
            + "4）校验 + 收尾：列一遍外层容器里的条目、与磁盘上的分卷逐个核对（对不上不显示成功）；通过之后才按你选的两档处理原包与其余物（其余物 = 那几个分卷文件，默认彻底删除）。失败 / 取消一个字节都不动。";

        /// <summary>④ 单任务密码那一框的展示框标题。</summary>
        public const string BoxDetailPerTaskPasswordHeader = "详细说明：单个包要用别的密码怎么办";

        /// <summary>④ 密码：单任务密码走哪两个入口（原先是常驻的那段）。</summary>
        public const string BoxDetailPerTaskPasswordDetail =
            "单个包要用别的密码：开始前程序会问一次「要不要手动输一个密码」（只对本次运行有效、不落盘），也可以在「密码列表管理」里把它单独加一条。";

        /// <summary>⑥ 启动时行为那一框的展示框标题。</summary>
        public const string BoxDetailStartupBehaviorHeader = "详细说明：这两项各自管什么";

        /// <summary>⑥ 启动时行为：两项各自的后果（原文逐字，只把两段并进一个展示框）。</summary>
        public const string BoxDetailStartupBehaviorDetail =
            "关掉之后：下次启动不再把上次的输出目录填回「当前输出位置」。注意输出位置在「任务」页（「输出位置：… 选择…」和「未指定位置」那颗开关）与「解压方式」页都能改，两处写的是同一份设置；这一项管的是「启动时要不要沿用上次那个自定义根」。\n"
            + "开着：批量解压时还让得开手，前台程序不会被拖卡（代价是纯后台跑时慢一点）。它不影响并发数（并发由程序自动决定）。";

        /// <summary>⑥ 扫描与识别那一框的展示框标题。</summary>
        public const string BoxDetailScanAndIdentifyHeader = "详细说明：改名冲突时会发生什么";

        /// <summary>⑥ 扫描与识别：改名一定会先预览、询问档在两处都真的会问（原文逐字）。</summary>
        public const string BoxDetailScanAndIdentifyDetail =
            "改名一定会先弹预览，改完由你确认才落盘。「询问」在两处都真的会问：解压落位弹一次聚合询问；改名冲突在预览表里逐条确认（没选就按保守档自动重命名，绝不覆盖）。";
        /// <summary>⑥ 安全上限那一框的展示框标题。</summary>
        public const string BoxDetailBudgetHeader = "详细说明：这四条上限拦的是什么";

        /// <summary>⑥ 安全上限：四条只拒绝解压、不删东西；另有两道独立闸门（原先是常驻的那段）。</summary>
        public const string BoxDetailBudgetDetail =
            "这四条只「拒绝解压」，不会删任何东西；超过上限时失败清单会写清是哪个上限拦的。另有两道独立的闸门照旧生效：目标盘空间不够就不启动，解压过程中按累计大小设限。";

    }
}