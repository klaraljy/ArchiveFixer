namespace ArchiveFixer.Models
{
    /// <summary>
    /// 任务状态、操作、后缀状态、密码状态等界面文本的统一定义。
    /// 所有状态字符串统一从这里引用，避免各处散落字面量不一致。
    /// </summary>
    /// <remarks>
    /// 按域拆在 partial 文件里（类名 / 命名空间 / 成员名 / 取值都没改）：
    /// StatusText.Tasks.cs、StatusText.Extraction.cs、StatusText.Volume.cs、StatusText.Password.cs、
    /// StatusText.Diagnostic.cs、StatusText.Recursion.cs、StatusText.Ui.cs、StatusText.Packing.cs。
    /// </remarks>
    public static partial class StatusText
    {
        /// <summary>
        /// 主窗口标题（左上角那一行）。两件事在这里合流：
        /// ①**反馈方式写在标题上**（用户 2026-09-26："现在将应用左上角的应用名可以改一下，
        /// 在后面加上『遇到问题反馈给作者，发邮件 klaraljy0617@outlook.com』"）；
        /// ②**标题里不带版本号**（同一天他要求删掉"第十一版"这几个字）——
        /// 版本只在「帮助 → 关于」里报（那份读的是程序集版本，不靠手写字面量）。
        ///
        /// <para>⛔ 邮箱是**唯一**要改的地方：About 那一块也引用 <see cref="FeedbackEmail"/>，
        /// 不许在别处再写一遍字面量。</para>
        /// </summary>
        /// <summary>最新版本下载页（标题与「关于」共用一份）。</summary>
        public const string ReleaseDownloadUrl = "https://github.com/klaraljy/ArchiveFixer/releases";

        public const string AppWindowTitle =
            "ArchiveFixer - 批量压缩包识别与解压工具　｜　遇到问题请反馈给作者（发邮件 klaraljy0617@outlook.com）"
            + "　｜　最新版本下载：" + ReleaseDownloadUrl;

        /// <summary>作者的反馈邮箱（标题与「关于」共用一份）。</summary>
        public const string FeedbackEmail = "klaraljy0617@outlook.com";

        // 进度文本
        public const string ProgressWaiting = "-";
        public const string ProgressProcessing = "处理中";
        public const string ProgressCompleted = "完成";
        public const string ProgressFailed = "失败";
        public const string ProgressSkipped = "跳过";
        public const string ProgressCancelled = "已取消";

        /// <summary>进度详情里的剩余时间后缀；{0} = 已格式化的 mm:ss / h:mm:ss。</summary>
        public const string ProgressRemainingSuffixFormat = " · 剩余 {0}";

        /// <summary>托盘悬停提示的进度格式；{0} = 百分比，{1} = 可空的剩余时间后缀。</summary>
        public const string TrayProgressFormat = "ArchiveFixer {0}%{1}";

        /// <summary>主窗口底栏的进度格式；{0} = 当前任务/阶段，{1} = 百分比，{2} = 可空的剩余时间后缀。</summary>
        public const string BottomProgressFormat = "正在处理：{0} · {1}%{2}";

        /// <summary>主窗口底栏：在处理、但还没有可用百分比。</summary>
        public const string BottomBusyText = "正在处理…";

        /// <summary>托盘悬停提示：只有“在处理”但没有可用百分比。</summary>
        public const string TrayBusyText = "ArchiveFixer 处理中";

        /// <summary>托盘悬停提示：空闲。</summary>
        public const string TrayIdleText = "ArchiveFixer";

        // 操作
        public const string OpWaiting = "等待";
        public const string OpScan = "扫描";
        public const string OpRename = "改名";
        public const string OpTest = "测试";
        public const string OpExtract = "解压";
        public const string OpSkip = "跳过";
        public const string OpCancel = "取消";
        public const string OpPrepare = "准备";

    }
}