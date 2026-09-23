namespace ArchiveFixer.Models
{
    /// <summary>
    /// 任务状态、操作、后缀状态、密码状态等界面文本的统一定义。
    /// 所有状态字符串统一从这里引用，避免各处散落字面量不一致。
    /// </summary>
    public static class StatusText
    {
        // 进度文本
        public const string ProgressWaiting = "-";
        public const string ProgressProcessing = "处理中";
        public const string ProgressCompleted = "完成";
        public const string ProgressFailed = "失败";
        public const string ProgressSkipped = "跳过";
        public const string ProgressCancelled = "已取消";

        // 操作
        public const string OpWaiting = "等待";
        public const string OpScan = "扫描";
        public const string OpRename = "改名";
        public const string OpTest = "测试";
        public const string OpExtract = "解压";
        public const string OpSkip = "跳过";
        public const string OpCancel = "取消";
        public const string OpPrepare = "准备";

        // 任务状态
        public const string WaitingScan = "等待扫描";
        public const string Scanning = "扫描中";
        public const string Recognized = "已识别";
        public const string UnknownFormat = "格式未知";
        public const string RenameSuccess = "改名成功";
        public const string RenameFailed = "改名失败";
        public const string Renaming = "改名中";
        public const string WaitingRename = "等待改名";
        public const string TestPassed = "测试通过";
        public const string TestFailed = "测试失败";
        public const string Testing = "测试中";
        public const string WaitingTest = "等待测试";
        public const string ExtractSuccess = "解压成功";
        public const string ExtractFailed = "解压失败";
        public const string Extracting = "解压中";
        public const string WaitingExtract = "等待解压";
        public const string WrongPassword = "密码错误";
        public const string Corrupted = "文件损坏";
        public const string AccessDenied = "权限不足";
        public const string OutputConflict = "输出路径冲突";
        public const string VolumeMissing = "分卷缺失";
        public const string PathTooLong = "路径过长";
        public const string UnknownError = "未知错误";
        public const string SevenZipMissing = "7z不存在";

        /// <summary>
        /// **没有可用的解压引擎**（7-Zip 与 UnRAR 一个都没找到）。
        ///
        /// <para>
        /// 为什么要与 <see cref="SevenZipMissing"/>（「7z不存在」）分开：后者的判据是"某个具体引擎/文件
        /// 不在"，而这一条是"<b>任一引擎都不可用</b>"（<c>EngineRouter.IsAvailable</c>）。
        /// 默认优先级是 UnRAR → 7-Zip，用户完全可能只是缺 UnRAR、或者把内置件关掉了 ——
        /// 这时给他看「7z不存在」是**指错方向**：他会去修一个本来没问题的 7z 目录，
        /// 而真正该做的是"两者任装其一"。
        /// </para>
        /// <para>
        /// 失败类状态：配色与统计必须与既有失败口径一致（见 <c>StatusToBrushConverter</c> 的错误色、
        /// <c>TaskSummaryService</c> 的"解压失败"桶）——按 AGENTS.md §7 三处同改。
        /// </para>
        /// </summary>
        public const string NoEngineAvailable = "没有可用的解压引擎";

        /// <summary>
        /// 达到密码尝试上限（AGENTS.md §9.2：每层、每任务、每批次都要有尝试上限）。
        ///
        /// **不是"密码错误"**：候选密码根本还没试完就按硬上限停了，
        /// 包本身可能完全正常，只是正确密码排在候选表更靠后的位置。
        /// 两者混为一谈时，用户会去反复核对密码本，而真正该做的是补上密码或调大上限。
        /// </summary>
        public const string PasswordAttemptLimitReached = "达到密码尝试上限";

        /// <summary>
        /// **磁盘空间不足**（用户 2026-09-22 需求第 1 条：空间不够就**不启动**该任务）。
        ///
        /// <para>
        /// 它与"解压失败"分开的理由：解压失败是"这个包有问题"，而它连**开始都没开始** ——
        /// 用户要做的事完全不同（清空间 / 换盘 / 用危险模式），把两者混在一起，
        /// 用户会先去怀疑包坏了。<c>ErrorMessage</c> 里一定带具体数字（需要 X、可用 Y、差 Z）与建议动作。
        /// </para>
        /// <para>
        /// 失败类状态：配色与统计必须与既有失败口径一致（见 <c>StatusToBrushConverter</c> 的错误色、
        /// <c>TaskSummaryService</c> 的"解压失败"桶）——按 AGENTS.md §7 三处同改。
        /// </para>
        /// </summary>
        public const string DiskSpaceInsufficient = "磁盘空间不足";

        public const string Cancelled = "已取消";
        public const string Skipped = "已跳过";
        public const string Overwritten = "已覆盖";
        public const string Success = "成功";
        public const string NotArchive = "非压缩包";

        /// <summary>
        /// 部分完成：解出来了一部分，但因为上限 / 密码 / 损坏停在中途。
        /// **不得当成成功**（AGENTS.md §6 第 6 条），单独一类用户才知道还要不要接着弄。
        /// </summary>
        public const string PartiallyCompleted = "部分完成";

        /// <summary>
        /// 归档**加密了文件名**（RAR <c>-hp</c> / 7z <c>-mhe</c>）：连条目名都读不出来，
        /// 所以"里面有什么、缺没缺"当前**无法判定**。
        ///
        /// 为什么必须单独一类：这种包以前会落到"密码错误 / 文件损坏"里，给用户的结论是**错的** ——
        /// 包可能完全正常，只是需要正确密码。反过来它也**不能算成功**（不变量 6）。
        ///
        /// 判定只在"列目录失败"时成立（见 <c>SevenZipOutputParser.LooksLikeEncryptedHeaders</c>）：
        /// 我们自己产出的内层 <c>-mhe</c> 分卷在给了正确密码时列目录是成功的，
        /// 所以正常可解的加密包不会被误判成这个状态。
        /// </summary>
        public const string EncryptedHeaders = "文件名已加密";

        // 后缀状态
        public const string NotChecked = "未检测";
        public const string ExtensionNormal = "后缀正常";
        public const string ExtensionMissing = "后缀缺失";
        public const string ExtensionMismatch = "后缀不匹配";
        public const string ExtensionMultiFake = "多重后缀疑似伪装";

        /// <summary>分卷文件的后缀：<c>xxx.7z.001</c>。它不是伪装，单独一类（设计.md §七）。</summary>
        public const string ExtensionVolume = "分卷后缀";

        /// <summary>
        /// 内嵌归档：文件本身不是压缩包，尾部却拼着一整个 ZIP（前面是视频等正常数据）。
        ///
        /// 后缀一栏给这一类，是为了让用户一眼看出"**不要改后缀**"：
        /// ZIP 的内部偏移相对它自己，而前置数据远超 7-Zip 的容忍上限（实测 8 MiB），
        /// 所以改成 <c>.zip</c> 之后 7z 仍然打不开，改名的唯一效果是让用户以为已经修好了。
        /// 真正要做的是按偏移把尾部那段取出来。
        /// </summary>
        public const string ExtensionEmbedded = "内嵌归档";

        // 密码状态
        public const string PasswordCorrect = "密码正确";
        public const string PasswordNotNeeded = "不需要密码";
        public const string PasswordNeed = "需要密码";

        // 改名预览状态
        public const string RenameReady = "可改名";
        public const string RenameCannot = "无法改名";
        public const string RenameWillSkip = "将跳过";
        public const string TargetExists = "目标已存在";
        public const string WillAutoRename = "将自动重命名";

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
        /// 实际看的是 DataGrid 的**当前行**（<c>MainViewModel.SelectedTask</c>）：
        /// 用户明明勾了任务，点「删除其余物…」却收到"请先在列表里选中一个任务"，
        /// 只能靠手动点一下那一行才work。把这句话放进唯一的文案来源，XAML 用
        /// <c>{x:Static}</c> 引用、命令提示由 <see cref="PickTaskPromptFormat"/> 构造，
        /// 两边就不可能再各写一套。
        /// </para>
        /// <para>
        /// 作用域口径（与 <c>MainViewModel.ResolveCleanupTargets</c> 的实际行为一一对应）：
        /// 勾选为准 → 一个都没勾时退化为当前行 → 两者都没有才提示。
        /// </para>
        /// </summary>
        public const string SelectionScopeHint =
            "「一键处理 / 智能修正 / 移除选中 / 删除其余物 / 清理空文件夹」以勾选为准（最左侧一列）；" +
            "一个都没勾时按当前点中的那一行办；两者都没有会提示你先选一个。" +
            "右键菜单只作用于当前这一行；「清空列表」是整表操作（与勾选无关）。";

        /// <summary>
        /// "两者都没有"时的提示模板（<c>{0}</c> = 命令名，例如「删除其余物」）。
        ///
        /// 措辞必须与 <see cref="SelectionScopeHint"/> 同一套词（"勾选" / "点中"），
        /// 否则用户会以为勾选没用 —— 那正是这次要修的缺陷。
        /// </summary>
        public const string PickTaskPromptFormat = "请先勾选或点中一个任务，再执行「{0}」。";

        // ================================================================
        // 危险模式（红按钮）：文案与风险描述
        // ================================================================
        //
        // ⚠ 这一组是**界面文案的唯一来源**：确认框正文、设置窗口的开关说明、
        //    自测通过后的提示都引用它，`docs/使用说明.md` 的「空间不够怎么办」一节照抄这份措辞。
        //    以前那种"确认框写一套、设置里写一套、文档里再写一套"的形态，改一处漏两处，
        //    而这里漏掉的每一句都是**用户拿不可逆操作换来的知情权**。

        /// <summary>这个模式在界面上的名字（按钮 / 开关 / 日志里都用它）。</summary>
        public const string DangerModeName = "危险模式 · 边解边彻底删其余物";

        /// <summary>确认框里"这是什么"的那一段。</summary>
        public const string DangerModeSummary =
            "并行解压，每个任务在「内容物已定稿并按落点策略排好 + 输出校验通过 + 未取消」之后，" +
            "立刻把它自己的「其余物」（源包 + 中间件）彻底删除（不进回收站），于是净占用基本不变 —— " +
            "这就是它能解决「空间不够」的原因。";

        /// <summary>
        /// 风险四条（用户 2026-09-22 逐条要求写清）。顺序就是他给的顺序，**不要重排**。
        /// </summary>
        public static readonly string[] DangerModeRiskLines =
        {
            "① 源包会被永久删除（不进回收站、无法还原，只能重新下载）。",

            "② 删除发生在「内容物已排好且校验通过」之后，但校验不等于你确认过内容 —— "
            + "程序只保证该解出来的都解出来了，不保证内容就是你想要的那一份。",

            "③ 中途断电 / 蓝屏 / 程序被强杀时，可能停在「源包已删、内容物未完成」的状态："
            + "那时这一份只能重新下载。",

            "④ 只在你确实没有空间时才用它；正常情况请用默认档（其余物移入回收站，可还原）。"
        };

        /// <summary>风险四条连成一段（确认框正文用）。</summary>
        public static readonly string DangerModeRisks = string.Join(Environment.NewLine, DangerModeRiskLines);

        /// <summary>
        /// 自测协议（用户原话：「拿几个文件先测试一遍，比如并行解压是五个，你就要拿十个文件做这样的测试，
        /// 测试成功才能告知用户可以一试，但风险还是有的」）。
        /// </summary>
        public const string DangerModeSelfTestProtocolText =
            "开启之前会先跑一次自测：拿「并发数 × 2」个文件（并发 5 → 10 个文件）真的跑一遍这个模式，" +
            "逐个检查「解压成功 + 输出校验通过 + 其余物按预期被彻底删除 + 空间曲线符合预期」。";

        /// <summary>自测自己也是不可逆的：它删的就是那几个文件的源包 —— 必须提前说清。</summary>
        public const string DangerModeSelfTestWarning =
            "⚠ 自测本身就会永久删除这几个文件的源包（这正是要验证的动作），不可撤销。" +
            "自测会从需求最小的文件开始挑样本。";

        /// <summary>自测通过之后才说这句话（用户原话的落点）。</summary>
        public const string DangerModeCanTry =
            "自测通过：可以一试，但风险还是有的 —— 源包会被永久删除，无法还原。";

        /// <summary>没有自测凭证时不许开启。</summary>
        public const string DangerModeNeedsSelfTest =
            "危险模式必须先通过一次自测才能开启（在设置里点「跑自测并开启…」）。" +
            "跳过自测就等于跳过了唯一一次「先拿几个文件试试」的机会。";

        /// <summary>
        /// 自测凭证**盖不住当前并发档**时的说明（凭证是并发 N 的，现在调到 M &gt; N）。
        ///
        /// <para>为什么要有这一条：协议要求"拿并发数 × 2 个文件真跑一遍"，
        /// 跑出来的结论只对它跑过的那一档成立。档位调高之后再拿旧凭证开这个模式，
        /// 等于**没测过就用了**，而这条路上的代价是不可逆的（源包永久删除）。
        /// 所以档位超出凭证覆盖范围时**本批不生效**（一个字节都不删），并在这里说清两条出路。</para>
        /// </summary>
        public const string DangerModeNotCoveredBySelfTest =
            "危险模式当前不生效：自测凭证只覆盖它跑过的那一档并发。" +
            "要么把「最大并发解压数」调回不超过凭证里的档位（本批立刻恢复生效），" +
            "要么在新档位下重新跑一次自测（会再永久删除一批样本源包，不可撤销）。";
    }
}
