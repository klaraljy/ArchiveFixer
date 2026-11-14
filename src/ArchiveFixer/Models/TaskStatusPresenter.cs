using System;

namespace ArchiveFixer.Models;

/// <summary>
/// 「这一行给用户看什么」的**唯一投影**：状态文字 / 状态配色键 / 密码状态列 / 检测格式列 / 错误信息列。
///
/// <para>从 <see cref="ArchiveTask"/> 抽出的协作类（「绑定状态」那一块）：这些列都是**计算属性**
/// （读机器状态 + 组判决 + 分卷事实 + 进度 + 变换链），⛔ 一个都不许反过来当判据 ——
/// 删除闸门 / 统计 / 名单只读 <see cref="ArchiveTask.Outcome"/> 与机器字段。</para>
///
/// <para>⛔ 只改显示、不动账：本类不写任何任务字段、不发通知、不重跑归组、不比对中文文案；
/// 输入是任务当场那份只读快照（<see cref="TaskStatusSnapshot"/>），输出是那一格该显示的文字 / 配色键。</para>
///
/// <para><b>失败模型</b>：全部是纯字符串拼接与枚举比较，没有 IO、没有异常路径；判不出组判决时
/// 按"没成组"这一档说老口径（与拆分前逐字相同）。</para>
///
/// <para>生命周期：纯静态、无状态；每次求值重新取一份快照（与拆分前"每列各自读字段"同一时点语义）。</para>
/// </summary>
internal static class TaskStatusPresenter
{
    /// <summary>
    /// 求值那一刻任务上的**只读事实快照**（⛔ 不含任何可写字段 / 对象引用，
    /// 这样显示公式可以被逐字搬过来而不把任务本身传进来）。
    /// </summary>
    internal readonly record struct TaskStatusSnapshot(
        string Status,
        bool HasUserFacingFile,
        string SettledWithGroupUnitName,
        bool OwnRunSucceededBeforeGroupSettlement,
        bool VolumeNameAutoRenamed,
        bool IsVolumeDeficitDeferred,
        bool HasLiveProgress,
        int ProgressPercent,
        int LiveLayer,
        bool HasResponsivenessHint,
        string ResponsivenessHint,
        TaskOutcome Outcome,
        bool HasContentGroup,
        bool IsContentGroupProcessTask,
        bool FinalContentVerified,
        FinalContentState ContentGroupFinalState,
        string TransformationText);

    /// <summary>
    /// ①页「状态」那一格显示的文字：**不新增列**，把百分比紧凑地并进状态文案。
    ///
    /// <para>例：<c>解压中 45%</c>；有长时间无响应提示时是 <c>解压中 45% · 长时间无响应</c>。
    /// 任务一收尾就退回纯状态文案（见 <see cref="ArchiveTask.HasLiveProgress"/>）。</para>
    /// <para>⚠ 它**不新造任何状态字符串**：前缀永远是既有的 <see cref="ArchiveTask.Status"/> 值
    /// （来自 <c>StatusText</c> 常量），这里只做拼接，所以统计与配色（都看机器状态）完全不受影响。</para>
    /// </summary>
    internal static string ComposeStatusText(in TaskStatusSnapshot snapshot)
    {
        /*
         * 组内过程任务不重复显示成最终成功；组最终内容未证也不许显示“解压成功”。
         * 只改显示，不动 Status/Outcome（删除闸门/统计仍读机器字段）。
         */
        if (snapshot.HasContentGroup)
        {
            if (snapshot.IsContentGroupProcessTask)
            {
                return StatusText.ContentGroupProcessTask;
            }

            if (!snapshot.FinalContentVerified)
            {
                return snapshot.ContentGroupFinalState == FinalContentState.Missing
                    ? StatusText.ContentGroupMissing
                    : StatusText.ContentGroupPartial;
            }
        }

        /*
         * ===== 口径①（用户 2026-10-06 拍板）：一行只讲"你导进来的那个文件" =====
         *
         * 批末会有一行被"借去"当**解开这一组的那个单元**（起点被改写到入口包上，
         * 见 <see cref="UserFileIdentity"/> 的说明）。那一行顶上写的是你的文件，
         * 状态却写"解压成功" —— 读起来像"你这一片被单独解成功了"。⇒ 这一档改说
         * **这一组**（显示文案而已，`Status` 一个字不动 ⇒ 判据 / 名单 / 配色全不受影响）。
         */
        string text = snapshot.HasUserFacingFile && snapshot.Status == StatusText.ExtractSuccess
            ? StatusText.ExtractSuccessAsGroup
            : snapshot.Status;

        /*
         * ===== 「这一片已经被整组接手」的行不许永远停在「已跳过」=====
         *
         * 用户 2026-10-07 原话：「列表里面最下面的两个，从来没有变化」—— `111.rar` 与
         * `111(2)_.zip` 两行各自只吐出一片、那片被接进整组解开了，可它们的状态格一直写着
         * 「已跳过 100%」，读起来像"程序什么都没干"。⇒ 有那条**结构化事实**
         * （<see cref="TaskStatusSnapshot.SettledWithGroupUnitName"/> 非空 = 收场那一刻记下的"是谁把整组解开的"）
         * 时，状态格如实说这件事。⛔ `Status` / `Outcome` 一个字不动 ⇒ 统计、名单、配色不受影响。
         */
        if (snapshot.SettledWithGroupUnitName.Length > 0 && snapshot.Status == StatusText.Skipped)
        {
            /*
             * ⚠ 2026-10-10（用户拍板「两句都写」）：这一单自己那一趟**也**跑成了的时候
             * （<see cref="TaskStatusSnapshot.OwnRunSucceededBeforeGroupSettlement"/>，收场那一刻抄下来的事实），
             * 只写后半句就把"你自己那份其实解成了"吃掉了 —— 两个事实都写。
             * ⛔ 措辞不另造：前半句复用 `StatusText.ExtractSuccess` 的说法，后半句复用原来那一句。
             */
            text = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                snapshot.OwnRunSucceededBeforeGroupSettlement
                    ? StatusText.ExtractSucceededThenPieceSettledWithGroupFormat
                    : StatusText.PieceSettledWithGroupFormat,
                snapshot.SettledWithGroupUnitName);
        }

        /*
         * ===== 批中间撞上缺卷的那一档：**先跳过，不许把结论摆出来** =====
         *
         * 用户 2026-10-08 口径：「没有到最后一步都是先跳过」——缺的那几片完全可能被同一批
         * 别的包解出来（真机 `111.z01` 就是这样补上的），批末那一站才该下结论。
         *
         * ⚠ 这一档**连「部分完成」也要盖掉**（用户 2026-10-08 第二句：「批中间连部分完成也不许显示，
         * 一律跳过（等最后判）」）：递归停在缺卷、但前面已经解出过东西时，机器状态就是「部分完成」——
         * 那个结论同样是**下早了**（缺的那几片可能被同批别单补上）。
         *
         * ⛔ `Status` / `Outcome` 一个字不动（发布链、其余物、删除闸门读的都是它）——
         * 这里只把**显示**改说成"跳过（缺卷，等批末再判）"；批末定稿时协调器把事实位清掉，
         * 那一格随即回到真结论。
         */
        if (snapshot.IsVolumeDeficitDeferred &&
            (snapshot.Status == StatusText.VolumeMissing || snapshot.Status == StatusText.PartiallyCompleted))
        {
            text = StatusText.VolumeDeficitPendingText;
        }

        /*
         * 百分比什么时候显示（用户 2026-09-27 真机："上面怎么都显示解压成功 99%，
         * 连一个 100% 的都没有"）：
         *
         * · 正在跑（HasLiveProgress）→ 显示实时百分比；
         * · **已经成功** → 一律显示 100%。
         *   为什么成功这一档要**写死 100**、而不是读存下来的百分比：
         *   ① 引擎报的最后一帧常常只到 99（`Everything is Ok` 之前那一次）；
         *   ② 收尾有好几条路（MarkSuccess / 定稿收尾 / 各分支早退），其中有的会
         *      `ClearProgress()` 把百分比清成 -1 —— 读字段就会一会儿 99、一会儿没有。
         *   成功的判据是**机器终态**（Outcome），不是那个瞬时字段，所以这里按终态说话。
         *
         * ⛔ 失败 / 部分完成 / 取消**不显示**百分比：配上 100% 会自相矛盾
         *   （"解压失败 100%"读起来像成功了，正是不变量 6 最忌讳的那种形态）。
         */
        int? percent = snapshot.HasLiveProgress
            ? snapshot.ProgressPercent
            : snapshot.Outcome == TaskOutcome.Succeeded ? 100 : null;

        /*
         * 续解链的第几层说在括号里（用户 2026-10-07：「111.z03 显示解压了三遍 100%」）：
         * 递归每开一层都会补一帧 0%（否则进度条会停在上一层的 100%），行里看不出"换层了"
         * ⇒ 同一行就像被解了三遍。层号是从引擎进度那一帧里**结构化**传上来的
         * （<see cref="ArchiveTask.ApplyProgress"/> 的 layer），⛔ 不去解析条目文字。
         */
        if (snapshot.HasLiveProgress && snapshot.LiveLayer > 0)
        {
            text += string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.LiveLayerSuffixFormat,
                snapshot.LiveLayer);
        }

        if (percent.HasValue)
        {
            text += $" {percent.Value}%";
        }

        if (snapshot.HasResponsivenessHint)
        {
            text += $" · {snapshot.ResponsivenessHint}";
        }

        /*
         * "改过名"和"这一单没解成"是两件事（用户 2026-09-28 真机："界面上显示跳过，
         * 日志上也显示跳过，但是确实是改好了名字了，这是什么情况"）。
         * 分卷名是**解压前**自动改好的；解压这单照样可能跳过 / 失败 —— 说清楚，别让人以为白改了。
         */
        if (snapshot.VolumeNameAutoRenamed)
        {
            text += "（已改回标准名）";
        }

        /*
         * 最后把**这一行经历过的变换**接上（用户 2026-10-07：「我们最后想看到 111.rar 通过解压缩
         * 两层变换到 111.zip，用户如果看到你什么都不动就会以为你什么都没改变」）——
         * 链本身只由 <see cref="TransformationChain"/> 从程序记录里拼，⛔ 这里不编内容。
         */
        if (snapshot.TransformationText.Length > 0)
        {
            text += StatusText.TransformationChainSeparator + snapshot.TransformationText;
        }

        return text;
    }

    /// <summary>
    /// **状态那一格配色读的键**（⛔ 不是新状态常量）：默认就是 <see cref="ArchiveTask.Status"/>；
    /// 组判决 / "缺卷待批末判"那几档换成中性 / 组那一档
    /// —— 用户 2026-10-08 点名的错：「为什么跳过要标红，这是非常错误的行为，这到了最后一步才判断的」。
    ///
    /// <para>为什么要有它：那一格的**文字**读 <see cref="ArchiveTask.StatusDisplayText"/>（批中间说"跳过"），
    /// 而**颜色**原先绑的是机器状态 ⇒ 待判那一行说着"跳过"却顶着红色。
    /// 现在两处（<c>TaskTab.xaml</c> / <c>TaskDetailWindow.xaml</c>）都绑这个键，⛔ 不再各绑各的。</para>
    /// </summary>
    internal static string ResolveColorKey(in TaskStatusSnapshot snapshot)
    {
        if (snapshot.HasContentGroup)
        {
            if (snapshot.IsContentGroupProcessTask)
            {
                return StatusText.Skipped;
            }

            if (!snapshot.FinalContentVerified)
            {
                return snapshot.ContentGroupFinalState == FinalContentState.Missing
                    ? StatusText.ExtractFailed
                    : StatusText.PartiallyCompleted;
            }
        }

        return snapshot.IsVolumeDeficitDeferred ? StatusText.Skipped : snapshot.Status;
    }

    /// <summary>
    /// 列表“密码状态”列的显示值：在原始密码状态上补“跟班卷 / 缺卷 / 不适用”，
    /// 避免没有一个任务尝试过密码时整列都停在“未检测”。
    /// </summary>
    internal static string ComposePasswordStatus(
        bool isVolumeGroupFollower,
        bool isVolumeDeficitDeferred,
        string status,
        string passwordStatus)
    {
        if (isVolumeGroupFollower)
        {
            return "跟班卷";
        }

        if (isVolumeDeficitDeferred || string.Equals(status, StatusText.VolumeMissing, StringComparison.Ordinal))
        {
            return StatusText.PasswordNotApplicable;
        }

        if (string.Equals(passwordStatus, StatusText.NotChecked, StringComparison.Ordinal)
            && string.Equals(status, StatusText.Skipped, StringComparison.Ordinal))
        {
            return StatusText.PasswordNotApplicable;
        }

        return passwordStatus;
    }

    /// <summary>
    /// 「检测格式」那一列**给用户看的**说法（用户 2026-09-28：续卷显示 Unknown 会读成"没认出来"）。
    ///
    /// <para>续卷（<c>x.7z.002</c>）是裸切块、**没有文件头魔数**，所以内容格式本来就判不了 ——
    /// 那是"物理上判不了"，不是"我们不认识"。后缀状态那一列才是结论（分卷后缀）。
    /// 这里只改**显示**：格式 Unknown + 后缀状态是分卷 → 说「分卷续卷（无文件头）」。
    /// 日志里照旧写 Unknown（机器事实不改），免得排查时对不上。</para>
    /// </summary>
    internal static string ComposeDetectedFormat(string detectedFormat, string extensionStatus) =>
        string.Equals(detectedFormat, "Unknown", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(extensionStatus, StatusText.ExtensionVolume, StringComparison.Ordinal)
            ? "分卷续卷（无文件头）"
            : detectedFormat;

    /// <summary>
    /// 「错误信息」那一列显示什么：**被借去当单元的那一行不显示单元的内部过程详情**
    /// （用户 2026-10-07 原话：「列表里面还是显示 111.z03 解压了三次」—— 他看到的正是那一格里的
    /// 「已完成 3 层递归解压…」）。
    ///
    /// <para>口径①：这一行属于**你导入的那个文件**，它的结论就是"这一组解压成功"；
    /// 递归分几层、产物从哪个工作区搬出来，属于**单元的过程详情**，⛔ 不该占用户这一行
    /// （要看全过程去①页「详情 / 右键复制任务信息」或日志）。⛔ 只改显示，<c>ErrorMessage</c> 一个字不动。</para>
    /// </summary>
    internal static string ComposeErrorMessage(string errorMessage, bool hasUserFacingFile, string status) =>
        hasUserFacingFile && status == StatusText.ExtractSuccess
            ? string.Empty
            : errorMessage;
}
