using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;


namespace ArchiveFixer.ViewModels
{
    /// <summary>双面文件（视频+尾部归档）直解（第 12 块，同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        /// <summary>
        /// 内嵌 ZIP 直读的**执行**那一步（用户 2026-09-24 需求第 6 条）。
        ///
        /// <para>只做三件事：把状态标成"解压中"、在后台线程流式解出条目、把结论交给
        /// <see cref="PostProcessSuccessAsync"/>（**同一个**收尾：校验 / 定稿 / 归集 / 其余物 / 源包处理）。
        /// 所以直读与"抠取 + 7z"两条路在用户眼里是同一套状态与同一套结论。</para>
        ///
        /// <para>取消原样抛出去：<c>ProcessExtractTaskAsync</c> 会落成「已取消」（不变量 6），
        /// 而直读器在抛之前已经把它写出去的东西全删掉了 —— 不留半成品、也不发布。</para>
        /// </summary>
        private async Task RunEmbeddedZipDirectExtractionAsync(
            ArchiveTask task,
            EmbeddedZipProbeResult plan,
            IReadOnlyList<string> passwordCandidates,
            ArchiveListResult? knownList,
            string stageDirectory,
            string outputRedirectNote,
            bool sharedOutputRoot,
            bool oneClickRun,
            ProgressHeartbeatStep.Sink progressSink,
            CancellationToken cancellationToken)
        {
            task.Operation = StatusText.OpExtract;
            task.Status = StatusText.Extracting;
            task.ProgressText = StatusText.ProgressProcessing;
            task.LastUpdatedTime = DateTime.Now;

            AppendLog(
                "INFO",
                $"{task.LogName}：开始 ZIP 直读解压（{plan.List?.FileCount ?? 0} 个文件 / " +
                $"{TaskSpaceEstimate.FormatSize(plan.TotalBytes)}，不生成等大临时副本）。");

            var progress = new DirectReadProgressBridge(progressSink);

            /*
             * 掐一下表（用户 2026-09-25 第 38 条："为什么要直读这么久…有没有什么办法改进"）。
             *
             * 直读 = 从源文件把这一段**读出来再写到成品**（等于一次同盘拷贝），所以它天然是**磁盘速度**：
             * 实测他那块 H 盘顺序读写只有 18–22 MB/s（同一个 473 MB 文件在 H: 内复制一次 24.9 秒），
             * 442 MB 的直读花 20 秒正好等于这个速度 —— **瓶颈在盘，不在读取器**。
             * 把实测速度写进日志，用户一眼就能判断"是盘慢还是程序慢"，不用猜。
             */
            var directReadWatch = System.Diagnostics.Stopwatch.StartNew();

            EmbeddedZipExtractResult extract = await Task.Run(
                () => EmbeddedZipStreamExtractor.Extract(
                    task.CurrentPath,
                    task.EmbeddedArchiveOffset,
                    task.EmbeddedArchiveEnd,
                    stageDirectory,
                    progress,
                    cancellationToken,
                    // 直读与 7z 两条路共用同一套上限（AGENTS.md §9.6"口径不许分叉"）：
                    // 传 null 会让直读退回硬编码那一份，用户在设置里调大的值就只对 7z 生效。
                    budgetOptions: BudgetLimits,
                    passwords: passwordCandidates),
                cancellationToken);

            directReadWatch.Stop();

            if (!extract.Success)
            {
                /*
                 * 直读失败（结构在两次探查之间变了 / 盘写不进去 / 解出来的字节数与清单不符）：
                 * **不回落**。回落要把几百 MB 到 2 GB 再拷一份，而失败原因（磁盘、权限、数据损坏）
                 * 拷一遍照样存在 —— 那只会让用户多等一场、还多占一份空间。如实报失败。
                 */
                task.Status = StatusText.ExtractFailed;
                task.ErrorMessage = extract.Message;
                task.LastUpdatedTime = DateTime.Now;

                AppendLog("ERROR", $"{task.LogName}：ZIP 直读失败 —— {extract.Message}");
                return;
            }

            AppendLog(
                "INFO",
                $"{task.LogName}：ZIP 直读完成 —— {extract.Message}"
                + $"；用时 {directReadWatch.Elapsed.TotalSeconds:0.0} 秒 ≈ {DescribeThroughput(extract.WrittenBytes, directReadWatch.Elapsed)}"
                + "（直读就是「读出来再写进去」的一次拷贝，速度上限由这块盘决定）");

            /*
             * 命名类警告（保留名 / 结尾点或空格 / 备用数据流）：直读这条路**照常落盘**，
             * 只把"哪些名字会被 Windows 改写"如实写一行（用户 2026-10-24：日志要详细）。
             */
            if (extract.NamingSummary.Length > 0)
            {
                AppendLog("WARN", $"{task.LogName}：{extract.NamingSummary}");
            }

            task.Status = StatusText.ExtractSuccess;
            /*
             * 密码状态按**实际发生的事**写：加密包是被内置读取器用候选密码解开的，
             * 写成"不需要密码"会让报告与失败清单误导人（不变量 14 的同一口径）。
             */
            task.PasswordStatus = extract.WasEncrypted ? StatusText.PasswordCorrect : StatusText.PasswordNotNeeded;
            task.ErrorMessage = string.Empty;

            /*
             * 溯源（不变量 14）：这一单不是 7-Zip 解的，报告里不能写成 7-Zip。
             * 身份已经在探查那一步盖进引擎门面（RememberEmbeddedZipDirectRead），
             * 这里再把"谁干的、干出多少"落到任务自己的字段上。
             */
            task.EngineVerdict =
                $"{EmbeddedZipStreamExtractor.ReaderDisplayName} 读出 {extract.FileCount} 个文件 / {extract.WrittenBytes} 字节" +
                "（未调用 7-Zip，未生成临时副本）" +
                (extract.WasEncrypted
                    ? $"；密码：第 {extract.ResolvedPasswordIndex} 个候选用{ZipAesCrypto.DescribeEncoding(extract.ResolvedPasswordEncoding)}解开"
                    : string.Empty);

            bool conclusionStands = await PostProcessSuccessAsync(
                task,
                task.CurrentPath,
                string.Empty,
                stageDirectory,
                outputRedirectNote,
                sharedOutputRoot,
                oneClickRun,
                cancellationToken,
                knownList);

            if (conclusionStands && task.Status == StatusText.ExtractSuccess)
            {
                AppendLog("INFO", $"解压成功：{task.FileName} -> {task.OutputPath}");
            }
        }

        /// <summary>
        /// 内嵌归档抠出来之后落在哪：<c>&lt;work&gt;\&lt;taskId&gt;\&lt;包基名&gt;.zip</c>。
        ///
        /// 三条刻意的选择：
        /// ① 放工作区（<see cref="PathService.WorkDirectory"/>）而不是源目录旁边 ——
        ///    中间产物不得写进源目录（AGENTS.md §6 第 12 条）。抠出来的这一段只是为了能解压，
        ///    不是用户要的东西；定稿时它会被归入 <c>其余物</c>（契约 §3.2），取消/失败时留在工作区；
        /// ② 文件名沿用**源文件的包基名**，不改成随机临时名 ——
        ///    密码本"名称:密码"的映射匹配用的就是包基名，随机名会让本来能命中的密码全部落空，
        ///    用户看到的现象会是"同一个包以前能解开，现在说密码错误"；
        /// ③ 它是**暂存目录的兄弟**（不在 stage 里面）：stage 里的东西会被整体定稿搬走，
        ///    而它是解压的输入，不该被自己搬走。
        /// </summary>
        private string BuildEmbeddedArchivePath(ArchiveTask task)
        {
            string baseName = FileNameHelper.SanitizeFileName(FileNameHelper.GetArchiveBaseName(task.CurrentPath));

            /*
             * ⚠ 后缀**默认**还是 `.zip`，哪怕抠出来的其实是 7z / RAR（用户 2026-09-25 第 42 条实测过才留下的）：
             *
             * · 而"容器里装的是分卷的第一卷"时，后缀会改变引擎的判决：
             *   叫 `封面.zip` → 引擎报的是「分卷缺失」这一类（成因说得对）；
             *   叫 `封面.7z` → 引擎改口说「**文件损坏**」（把"缺后续卷"说成了"包坏了"，用户会去怀疑文件）。
             *   实测三种命名（同一段第一卷字节）：`.zip` → Is not archive / 分卷缺失一类；
             *   `.7z` 与 `.7z.001` → Unexpected end of archive（被归到"文件损坏"）。
             *   所以**分卷那一档**不许"顺手改成真实格式的后缀" —— 那会把最需要说清的那一档说歪。
             *   回归 = `SplitInsideContainerTests` 里那两条引擎行为证据。
             *
             * ⚠ 2026-10-11 大测试实测补上另一半（run-5555，8.4 MB 媒体前缀 + 一整个 RAR5 尾巴）：
             *   上面那个"完整归档不看后缀"的前提**对大的 RAR 尾巴不成立** —— 同一份 96 MB 字节，
             *   叫 `X.zip` 时 `7z x` 退出码 2、`Open ERROR: Cannot open the file as [zip] archive`、**产物 0 个**；
             *   叫 `X.rar` 时退出码 0、4 个产物。于是"完整归档"那一档的名字改由内容证据决定，
             *   写点只有一个：`AlignCarvedArchiveExtension`（见 `ArchiveTask.CarvedArchiveExtension`）。
             */
            string suffix = string.IsNullOrWhiteSpace(task.CarvedArchiveExtension)
                ? ".zip"
                : task.CarvedArchiveExtension;

            return Path.Combine(_pathService.BuildTaskWorkDirectory(task), baseName + suffix);
        }

        /// <summary>
        /// 抠出来的那一份**按内容起名**（用户 2026-10-11 大测试 run-5555 实测出来的缺口）。
        ///
        /// <para><b>缺口现场</b>：`222-L5.png`（8.4 MB 媒体前缀 + 一整个 RAR5 尾巴）抠出来的那份叫
        /// `222-L5.zip` ⇒ `7z x` 按扩展名挑 zip 处理器、直接拒绝（退出码 2、产物 0 个）⇒
        /// 上层把这次失败归到「分卷缺失」⇒ 整单跳过、内容物一个都没有。
        /// 而同一份字节改名成 `.rar` 就能正常解开（本机实测，两个名字只差后缀）。</para>
        ///
        /// <para><b>判据（只读内容事实，⛔ 不看后缀、⛔ 不猜）</b>：抠完拿底层那张卡
        /// <see cref="FileNature.Inspect"/> —— 内容**可证"不是分卷组的一员"** 且魔数认得出格式时才改名；
        /// 真分卷成员（`VolumeMember`）与 7z 那一族（判不出）**一个字节都不动**，
        /// 保持用户 2026-09-25 第 42 条要的 `.zip`（那一档的回归 = `SplitInsideContainerTests`）。</para>
        ///
        /// <para>改名只发生在**本任务自己的暂存目录**里（抠取副本是我们自己刚写出来的临时文件）：
        /// 拿不到"确实在工作区根之下"这个事实证明就什么都不做。</para>
        /// </summary>
        private string AlignCarvedArchiveExtension(ArchiveTask task, string carvedPath)
        {
            if (task == null || string.IsNullOrWhiteSpace(carvedPath) || !File.Exists(carvedPath))
            {
                return carvedPath;
            }

            FileNatureCard card = FileNature.Inspect(carvedPath);
            string realExtension = VolumeContentInference.ExtensionFor(card.Format);

            if (realExtension.Length == 0)
            {
                CriterionLog(
                    "内嵌归档：抠出来的那一份按内容起名",
                    $"内容：格式={card.Format} 分卷身份={card.Membership}｜名字：{Path.GetFileName(carvedPath)}",
                    "不按内容起名（魔数认不出格式）",
                    "保持现有名字",
                    carvedPath);

                return carvedPath;
            }

            if (card.Membership != VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember)
            {
                CriterionLog(
                    "内嵌归档：抠出来的那一份按内容起名",
                    $"内容：格式={card.Format} 分卷身份={card.Membership}｜名字：{Path.GetFileName(carvedPath)}",
                    "内容不能证明「不是分卷组的一员」⇒ 保持 .zip（用户 2026-09-25 第 42 条：这一档叫 .zip 引擎才报「分卷缺失」）",
                    "保持现有名字",
                    carvedPath);

                return carvedPath;
            }

            string wanted = "." + realExtension;

            if (string.Equals(Path.GetExtension(carvedPath), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return carvedPath;
            }

            if (!ArchivePathGuard.IsInsideRoot(_pathService.WorkDirectory, carvedPath, out string reason))
            {
                AppendLog("WARN", $"{task.LogName}：抠出来的那一份不在工作区根之下，没有按内容改名 —— {reason}");

                return carvedPath;
            }

            string target = Path.ChangeExtension(carvedPath, realExtension);

            try
            {
                File.Move(carvedPath, target, overwrite: true);
            }
            catch (Exception ex)
            {
                AppendLog("WARN", $"{task.LogName}：抠出来的那一份按内容改名失败（沿用原名继续）：{ex.Message}");

                return carvedPath;
            }

            task.CarvedArchiveExtension = wanted;

            AppendLog(
                "INFO",
                $"{task.LogName}：抠出来的那一份按内容起名（内容可证不是分卷组的一员）：" +
                $"{Path.GetFileName(carvedPath)} → {Path.GetFileName(target)}");

            CriterionLog(
                "内嵌归档：抠出来的那一份按内容起名",
                $"内容：格式={card.Format} 分卷身份={card.Membership}｜名字：原名 {Path.GetFileName(carvedPath)}",
                $"内容格式={card.Format} ⇒ 后缀该是 {wanted}",
                $"改名 {Path.GetFileName(target)}",
                target);

            return target;
        }

        /// <summary>
        /// 内嵌归档 + 引擎报「分卷缺失」时补一句事实（用户 2026-09-25 第 42 条）。
        ///
        /// <para>真 7z 实测的现场：`封面.jpg` 里装着的**正是** `set.7z.001`，后续卷 `set.7z.002/.003`
        /// 就在同一个目录里。抠出来交给引擎之后，引擎按**名字**找同组的其他卷 ——
        /// 抠出来的那一段没有卷名，于是报"这是后续卷、缺少首卷"，而事实正好相反：
        /// 首卷在我们手上，缺的是"名字对得上的后续卷"。用户拿着那句话只会去满盘找首卷。</para>
        ///
        /// <para>判据全部来自文件系统（<see cref="OrphanVolumeSetDetector"/> 是纯函数）：
        /// 任务确实有内嵌归档 + 引擎报的确实是分卷缺失类 + 同目录里确实存在"缺首卷的那一组"。
        /// 三条缺一条就返回空串（⛔ 不猜、不拼凑）。</para>
        /// </summary>
        private string BuildEmbeddedVolumeMissingHint(ArchiveTask task, string? detectedErrorType)
        {
            if (task == null || task.EmbeddedArchiveOffset <= 0)
            {
                return string.Empty;
            }

            if (!IsVolumeMissingErrorType(detectedErrorType))
            {
                return string.Empty;
            }

            try
            {
                string directory = Path.GetDirectoryName(task.CurrentPath) ?? string.Empty;

                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    return string.Empty;
                }

                IReadOnlyList<OrphanVolumeSet> orphans = OrphanVolumeSetDetector.Find(
                    CollectVolumeFilesInDirectory(directory).Select(Path.GetFileName));

                if (orphans.Count == 0)
                {
                    return string.Empty;
                }

                /*
                 * 只有一组时才敢说——两组以上说明这个目录里缺首卷的组不止一个，
                 * 我们无法确定容器里装的是哪一组（⛔ 宁可不说，也不指错组）。
                 */
                if (orphans.Count > 1)
                {
                    AppendLog(
                        "WARN",
                        $"{task.LogName}：同目录里有 {orphans.Count} 组缺首卷的分卷，无法确定容器里装的是哪一组；" +
                        "这一条补充说明就不给了（失败原因仍是引擎报的那一条）。");

                    return string.Empty;
                }

                string hint = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.EmbeddedFirstVolumeHintFormat,
                    orphans[0].Describe(),
                    task.FileName,
                    orphans[0].FirstVolumeName);

                /*
                 * 开关打开时补一句"这次已经试过了" —— 否则用户会以为程序没试
                 * （方案 A 落地后，失败原因里的建议必须与"当前这一档到底试没试"一致）。
                 */
                if (Settings.AssembleSplitVolumesFromContainer)
                {
                    hint += StatusText.EmbeddedFirstVolumeHintAssemblyTriedSuffix;
                }

                AppendLog(
                    "WARN",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.EmbeddedFirstVolumeHintLogFormat,
                        task.FileName,
                        orphans[0].Describe()));

                return hint;
            }
            catch
            {
                // 补充说明拿不到就算了：失败原因本身仍然成立，绝不因为这句让结论变化。
                return string.Empty;
            }
        }

        /// <summary>引擎报的是不是"分卷缺失"这一类（7-Zip 与 UnRAR 两套错误码都认）。</summary>
        private static bool IsVolumeMissingErrorType(string? detectedErrorType)
        {
            if (string.IsNullOrWhiteSpace(detectedErrorType))
            {
                return false;
            }

            return string.Equals(detectedErrorType, EngineErrorTypes.VolumeMissing, StringComparison.Ordinal)
                || string.Equals(detectedErrorType, Engines.SevenZip.SevenZipOutputParser.MissingFirstVolumeErrorType, StringComparison.Ordinal)
                || string.Equals(detectedErrorType, "VolumeMissing", StringComparison.Ordinal);
        }


        /// <summary>
        /// 直读的百分比 → 既有进度口径的桥（<see cref="TaskProgressSink"/>）。
        ///
        /// <para>为什么要桥：直读器是纯逻辑、不认识 <c>ArchiveProgress</c>（那是引擎层类型），
        /// 而界面上的百分比 / "当前条目" / "跨 10% 写一行日志" 全都由 sink 统一处理 ——
        /// 桥过来之后，直读那条路在界面上的表现与 7z 那条**完全一样**（同样能看出在动、同样能取消）。</para>
        /// </summary>
        private sealed class DirectReadProgressBridge : IProgress<int>
        {
            private readonly ProgressHeartbeatStep.Sink _sink;

            public DirectReadProgressBridge(ProgressHeartbeatStep.Sink sink)
            {
                _sink = sink;
            }

            public void Report(int percent)
            {
                // 不带"当前条目"：日志里"xxx.mp4：进度 20%"已经说清了是哪个任务，
                // 再挂一遍文件名只是噪声（引擎路径给的是**归档内**的条目名，这里没有对应物）。
                _sink.Report(new ArchiveProgress { Percent = percent });
            }
        }

    }
}