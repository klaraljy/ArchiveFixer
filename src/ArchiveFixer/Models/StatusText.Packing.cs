namespace ArchiveFixer.Models;

// StatusText 的一部分（partial）：打包（⑤「打包」选项卡）。
// 成员名、取值与主文件 StatusText.cs 逐字一致；按域拆文件只是为了单文件可读，不是新类型。
public static partial class StatusText
{
    // ================================================================
    // 打包（⑤「打包」选项卡）
    // ================================================================
    //
    // ⚠ 这一组是**打包功能的文案唯一来源**（AGENTS.md §7）：窗口标题 / 步骤行 / 结论 /
    //    没有 Rar.exe 时的那段话都从这儿引，日志与失败原因也引同一份，
    //    免得"界面说一套、日志记一套"。
    //
    // 用户 2026-09-22 需求第 10 条：文件夹 A 的内容 → 7z 加密分卷 → 放进文件夹 B →
    //    B 压成带密码的 rar → 结果 = 压缩包 B。设计见 docs/打包功能.md。

    /// <summary>功能名（菜单项、窗口标题、日志里都用它）。</summary>
    public const string PackName = "打包为加密压缩包";

    /// <summary>第一步之前的那一步：校验落点、算空间。</summary>
    public const string PackStepPreparing = "准备：校验落点、核算空间";

    /// <summary>第一步：7z 加密分卷。</summary>
    public const string PackStepVolumes = "步骤 1/2：生成 7z 加密分卷";

    /// <summary>第二步（外层容器 = rar）：生成外层加密 rar。</summary>
    public const string PackStepRar = "步骤 2/2：生成外层加密 rar";

    /// <summary>第二步（外层容器 = 7z）：生成外层加密 7z。</summary>
    public const string PackStepSevenZipOuter = "步骤 2/2：生成外层加密 7z";

    /// <summary>第三步（外层容器 = rar）：核对产物。</summary>
    public const string PackStepVerify = "校验产物：列出 rar 条目、核对分卷数";

    /// <summary>第三步（外层容器 = 7z）：核对产物。</summary>
    public const string PackStepVerifySevenZip = "校验产物：列出 7z 条目、核对分卷数";

    /// <summary>成功（外层容器做成了）。</summary>
    public const string PackSuccess = "打包成功";

    /// <summary>失败。</summary>
    public const string PackFailed = "打包失败";

    /// <summary>取消。</summary>
    public const string PackCancelled = "打包已取消";

    /// <summary>产物校验不通过（不变量 6：对不上就不显示成功）。</summary>
    public const string PackVerifyFailed = "产物校验不通过";

    /// <summary>按要求没有做外层容器（结果就是 B 里的 7z 分卷）。</summary>
    public const string PackPartialVolumesOnly = "只做了 7z 分卷（按要求不做外层容器）";

    /// <summary>
    /// ⛔ 2026-09-26 第 46 条：这里原来的 <c>PackNeedRar</c>（"这一步需要本机已安装 WinRAR"）
    /// 与 <c>PackThreeWaysOut</c>（三条出路，让用户自己去⑤页换容器）**两条都已删除** ——
    /// 没装 WinRAR 时打包会**自动改用 7z 外层**，而"外层容器"那个控件也已经不在界面上了。
    /// 现在"没有 Rar.exe"那句话的唯一来源是 <c>ToolLocator.DescribeNoRarAvailable()</c>
    /// （⑤页状态格 / 日志 / 失败原因都引它）。
    /// </summary>

    // ── 外层容器三选一（用户 2026-09-23 决定；界面、日志、失败原因共用同一份说法） ──

    /// <summary>外层容器 = rar 的完整说法（含许可边界）。</summary>
    public const string PackOuterRarText =
        "外层容器 rar：把这几个 7z 分卷压成一个带密码的 .rar（需要本机已安装的 WinRAR：程序只检测与调用它，绝不随包分发）";

    /// <summary>外层容器 = 7z 的完整说法（**无需额外安装**这句是关键信息）。</summary>
    public const string PackOuterSevenZipText =
        "外层容器 7z：把这几个 7z 分卷压成一个带密码的 .7z（-mhe 连文件名一起加密）—— 无需额外安装（7-Zip 是 LGPL，随程序分发）";

    /// <summary>外层容器 = 不做。</summary>
    public const string PackOuterNoneText =
        "不做外层容器：结果就是落点目录里的那几个 7z 加密分卷（不需要 Rar.exe，也不需要多一份空间）";

    /// <summary>外层容器 rar 的短名（日志 / 摘要行）。</summary>
    public const string PackOuterRarShort = "rar";

    /// <summary>外层容器 7z 的短名。</summary>
    public const string PackOuterSevenZipShort = "7z";

    /// <summary>不做外层的短名。</summary>
    public const string PackOuterNoneShort = "不做外层";

    /// <summary>结果区提醒：那个装分卷的文件夹还在时，用户可以自己看、也可以自己删。</summary>
    public const string PackKeepFolderHint =
        "「其余物」（落点目录里的那几个 7z 分卷文件）按你选的档留着了 —— 可以先检查分卷，确认没问题之后自己删掉它们就行。";

    // ── 设置界面：自选 Rar.exe 路径（用户 2026-09-23 决定） ──

    /// <summary>
    /// 设置里那一格的标签。**必须写明许可边界**：这是用户自己装的 WinRAR 里的那一份，
    /// 程序只检测与调用，绝不随包分发（WinRAR 是共享软件）。
    /// </summary>
    public const string SettingsRarExePathLabel = "Rar.exe 路径：";

    /// <summary>设置里那一格留空时的含义（提示文案的后半句）。</summary>
    public const string SettingsRarExePathEmptyHint =
        "留空 = 自动用本机已装 WinRAR 目录里的那一份。";

    /// <summary>
    /// 设置里那一格的完整说明（**许可边界写在这里，不写"路径"了事**）。
    /// 界面提示、校验失败时的"改法"、以及日志都引它。
    /// </summary>
    public const string SettingsRarExePathHint =
        "这是你自己安装 / 下载的 WinRAR 里的 Rar.exe；本程序只检测与调用，绝不随包分发（WinRAR 是共享软件）。"
        + SettingsRarExePathEmptyHint;

    /// <summary>工具状态那一行的前缀（说明"当前用的是哪一份"）。</summary>
    public const string SettingsRarExePathStatusPrefix = "当前 Rar.exe：";

    // ── 设置界面：「推荐另外装一个 WinRAR（可选）」+ 官方下载页（用户 2026-10-05 拍板） ──
    //
    // 起因：真机上一个 WinZip AES 的 ZIP，内置 7-Zip 26.03 对**正确**的密码报「Wrong password」，
    // 用户自己用 WinRAR 十秒就打开了（现场见 AGENTS.md §11.5 最后一条）。于是程序多了一条
    // 「候选试完仍失败 ⇒ 换本机装的 WinRAR 再试同一批候选」的兜底 —— 而那一条**得有 WinRAR 才跑得起来**。
    // 这一格就是把"为什么值得装、程序绝不捆绑、不改他的设置"如实讲清楚的地方。

    /// <summary>那一格的标题（**可选**写在标题里，用户一眼看得懂；⛔ 不许写成"必须安装"）。</summary>
    public const string SettingsWinRarRecommendLabel = "推荐另外装一个 WinRAR（可选）";

    /// <summary>那一格的说明全文（为什么值得装 + 许可边界 + 不装会怎样）。</summary>
    public const string SettingsWinRarRecommendHint =
        "有些加密 ZIP（WinZip AES 那一类）7-Zip 打不开、WinRAR 却能打开：本程序在主引擎把密码候选试完之后，"
        + "会自动换本机装的 WinRAR 把同一批候选再试一遍 —— 只有装了它，这一档兜底才跑得起来。"
        + "WinRAR 是共享软件：本程序只检测与调用你自己装的那一份，绝不捆绑、绝不随包分发，"
        + "也不改你的 WinRAR 设置（调用时按参数走，不受界面里那些选项影响）。不装也能用，只是少这一档兜底。";

    /// <summary>按钮文案：打开发布方官网的下载页（⛔ 只指路，不下载、不代装、不捆绑）。</summary>
    public const string SettingsWinRarDownloadButtonText = "打开 WinRAR 官方下载页";

    /// <summary>打开成功那一行反馈（下载与安装都由用户自己在浏览器里做，程序不代劳）。</summary>
    public const string SettingsWinRarDownloadOpenedMessage =
        "已在浏览器里打开 WinRAR 官方下载页（rarlab.com）。装好之后程序会自动找到它，不需要在这里填路径。";

    /// <summary>打不开浏览器时如实说清（⛔ 不静默失败）。<c>{0}</c> = 失败原因。</summary>
    public const string SettingsWinRarDownloadFailedFormat =
        "打不开浏览器（{0}）。请自己访问 WinRAR 官网 rarlab.com 的下载页。";

}
