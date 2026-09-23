using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Packing
{
    /// <summary>
    /// 打包（用户 2026-09-22 需求第 10 条）：文件夹 A 的内容 → 7z 加密分卷 → 放进文件夹 B →
    /// B 整个压成带密码的 rar → 结果 = 压缩包 B。
    ///
    /// <para><b>两步 + 一次校验</b>（命令行的参数模板**只允许出现在本目录内**，
    /// docs/打包功能.md §5）：</para>
    /// <code>
    /// ① 7z.exe  a -t7z -mx=5 -mhe=on -bsp1 -p&lt;密码&gt; -v512m "&lt;B&gt;\&lt;A名&gt;.7z" "&lt;A&gt;\*"
    /// ② Rar.exe a -hp&lt;密码&gt; -r -ep1 "&lt;B&gt;.rar" "&lt;B&gt;"
    /// ③ 核对：Rar.exe lb -p&lt;密码&gt; "&lt;B&gt;.rar&gt;" → 分卷数对得上、且在 B 这一层下
    /// </code>
    ///
    /// <para><b>几条不肯让步的规矩</b>：</para>
    /// <list type="bullet">
    /// <item><description>7z **不能**建 rar（RAR 算法是专有的）。本机没有 <c>Rar.exe</c> 时
    /// **明确报错**并给出两条出路，⛔ 绝不把 7z 的产物改名成 <c>.rar</c> 糊过去；</description></item>
    /// <item><description><c>Rar.exe</c> 是共享软件：**只检测、只调用，绝不打包、绝不复制**（AGENTS.md §3.1）；</description></item>
    /// <item><description>取消只杀**自己启动的那个 PID 及其子进程**（在 <see cref="PackProcessRunner"/> 里）；</description></item>
    /// <item><description>失败 / 取消**不留半个 rar**；分卷保留（重试前要用户自己清空 B —— 见下）；</description></item>
    /// <item><description>完成之后核对产物，对不上**不显示成功**（不变量 6）。</description></item>
    /// </list>
    ///
    /// <para><b>为什么 B 必须不存在或为空</b>：B 会被整个装进 rar，混进旧文件就会让"结果 = 压缩包 B"
    /// 这句话不成立；而"把上一次失败留下的半成品静默删掉再重来"又是另一种不打招呼的破坏。
    /// 所以规则是**明说 + 让用户自己决定**：B 非空就拒绝并告诉他清空或换一个。</para>
    /// </summary>
    public sealed class PackingService
    {
        private readonly ToolLocator _tools;
        private readonly IPackProcessRunner _runner;
        private readonly Func<string, long?> _availableSpaceProbe;

        public PackingService()
            : this(ToolLocator.Default, null, null)
        {
        }

        /// <param name="tools">外部工具路径的唯一来源；null = <see cref="ToolLocator.Default"/>。</param>
        /// <param name="runner">起进程的那一层；null = 真的起进程（测试注入假的，用来验判据）。</param>
        /// <param name="availableSpaceProbe">
        /// 取目标盘可用空间；null = <see cref="SpaceChecker.GetAvailableFreeSpace"/>。
        /// 注入点是为了让"空间不够就不开始"这条判据能被确定性地测（真机上没法把盘填满）。
        /// </param>
        public PackingService(
            ToolLocator? tools,
            IPackProcessRunner? runner = null,
            Func<string, long?>? availableSpaceProbe = null)
        {
            _tools = tools ?? ToolLocator.Default;
            _runner = runner ?? new PackProcessRunner(_tools);
            _availableSpaceProbe = availableSpaceProbe ?? SpaceChecker.GetAvailableFreeSpace;
        }

        public ToolLocator Tools => _tools;

        /// <summary>
        /// 跑一次打包。**不抛异常**：失败与取消都落成 <see cref="PackingResult"/>。
        /// </summary>
        /// <param name="request">请求（含两个密码；只存内存）。</param>
        /// <param name="log">每一条步骤日志的出口（可空）；内容与 <see cref="PackingResult.LogLines"/> 一致。</param>
        /// <param name="progress">进度接收端（可空）。</param>
        public async Task<PackingResult> PackAsync(
            PackingRequest request,
            Action<string>? log = null,
            IProgress<PackingProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var stopwatch = Stopwatch.StartNew();
            var lines = new List<string>();

            void Write(string line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    return;
                }

                lines.Add(line);
                log?.Invoke(line);
            }

            void Report(PackingStep step, string text, int percent = PackingProgress.UnknownPercent, string detail = "")
            {
                progress?.Report(new PackingProgress
                {
                    Step = step,
                    StepText = text,
                    Percent = percent,
                    Detail = detail
                });
            }

            PackingResult Fail(string reason, string message, IReadOnlyList<PackingVolume>? volumes = null)
            {
                stopwatch.Stop();
                Write("失败：" + reason);

                return new PackingResult
                {
                    State = PackingState.Failed,
                    Message = message,
                    FailureReason = reason,
                    Volumes = volumes ?? Array.Empty<PackingVolume>(),
                    LogLines = lines,
                    Elapsed = stopwatch.Elapsed,
                    SkippedOuterRar = request?.SkipOuterRar ?? false
                };
            }

            PackingResult Cancelled(string reason, string? rarPath, IReadOnlyList<PackingVolume>? volumes = null)
            {
                stopwatch.Stop();
                Write(StatusText.PackCancelled + "：" + reason);

                return new PackingResult
                {
                    State = PackingState.Cancelled,
                    Message = StatusText.PackCancelled,
                    FailureReason = reason,
                    RarPath = rarPath,
                    Volumes = volumes ?? Array.Empty<PackingVolume>(),
                    LogLines = lines,
                    Elapsed = stopwatch.Elapsed,
                    SkippedOuterRar = request?.SkipOuterRar ?? false
                };
            }

            Report(PackingStep.Preparing, StatusText.PackStepPreparing);

            // ───────── ① 请求与落点（在动任何字节之前全部判完） ─────────
            if (!PackingPasswordPolicy.IsUsable(request?.Password))
            {
                return Fail(PackingPasswordPolicy.RequiredMessage, StatusText.PackFailed);
            }

            if (!PackingPlan.TryCreate(request!, out PackingPlan? plan, out string planError) || plan == null)
            {
                return Fail(planError, StatusText.PackFailed);
            }

            Write("开始打包：" + request!.DescribeForLog());
            Write("7-Zip：" + _tools.DescribeResolution());

            /*
             * 没有 Rar.exe 时**在切分卷之前**就停下（用户原话的那条出路要立刻能给）：
             * 先花十几分钟切出几十个分卷、再告诉他"外层做不了"，等于让他在最贵的步骤上白等。
             * 这一步只是**检测**，绝不复制、绝不内置（共享软件，AGENTS.md §3.1）。
             */
            if (!request.SkipOuterRar)
            {
                if (!_tools.RarExists)
                {
                    Write("Rar.exe：" + _tools.DescribeNoRarAvailable());

                    return Fail(
                        StatusText.PackNeedRar + Environment.NewLine
                        + "两条出路：① 装好 WinRAR（带 Rar.exe）后重试；"
                        + "② 勾上「" + StatusText.PackSkipRarOption + "」，这次就只做 7z 分卷。",
                        StatusText.PackFailed);
                }

                Write("Rar.exe：" + _tools.DescribeRarResolution());
            }
            else
            {
                Write("按要求跳过外层 rar：" + StatusText.PackSkipRarOption);
            }

            // ───────── ② 空间门（与解压侧同一套 SpaceGate 口径） ─────────
            long? available = _availableSpaceProbe(plan.OutputFolder);

            SpaceGateDecision decision = SpaceGate.Check(
                plan.RequiredSpaceBytes,
                available,
                SpaceGate.DefaultReserveBytes);

            Write("空间门：" + decision.ToLogLine());

            if (!decision.Allowed)
            {
                return Fail(decision.Reason, StatusText.DiskSpaceInsufficient);
            }

            // ───────── ③ 准备 B ─────────
            try
            {
                Directory.CreateDirectory(plan.OutputFolder);
            }
            catch (Exception ex)
            {
                return Fail($"创建输出文件夹失败：{PackPasswordGuard.Sanitize(ex.Message, request.Password)}", StatusText.PackFailed);
            }

            Write(plan.DescribeVolumePlan());

            // ───────── ④ 第一步：7z 加密分卷 ─────────
            if (cancellationToken.IsCancellationRequested)
            {
                return Cancelled("开始之前就被取消了", null);
            }

            Report(PackingStep.Volumes, StatusText.PackStepVolumes);

            PackStepResult volumeStep;
            List<PackingVolume> volumes;

            try
            {
                volumeStep = await _runner.RunAsync(
                        PackToolKind.SevenZip,
                        BuildVolumeArguments(plan, request.Password),
                        request.Password,
                        WrapProgress(progress, PackingStep.Volumes, StatusText.PackStepVolumes),
                        cancellationToken)
                    .ConfigureAwait(false);

                // 数分卷必须在进程结束之后：跑到一半去数，数到的是半成品。
                volumes = CollectVolumes(plan);
            }
            catch (OperationCanceledException)
            {
                return Cancelled("生成 7z 分卷时被取消", null, CollectVolumes(plan));
            }

            if (volumeStep.Cancelled)
            {
                Write($"分卷情况：B 里现有 {volumes.Count} 个分卷（**可能不完整**），重试前请先清空 B。");

                return Cancelled("生成 7z 分卷时被取消", null, volumes);
            }

            if (!volumeStep.Success)
            {
                Write($"分卷情况：B 里现有 {volumes.Count} 个分卷（**可能不完整**），重试前请先清空 B。");

                return Fail(
                    $"7-Zip 没能建出加密分卷（退出码 {volumeStep.ExitCode}）。"
                    + DescribeOutputTail(volumeStep)
                    + "分卷没有做成，外层 rar 也就没开始 —— 结果文件一个都没有生成。",
                    StatusText.PackFailed,
                    volumes);
            }

            string volumeProblem = ValidateVolumes(volumes, plan);

            if (volumeProblem.Length > 0)
            {
                return Fail(volumeProblem, StatusText.PackFailed, volumes);
            }

            Write($"7z 分卷完成：{volumes.Count} 个（共 {TaskSpaceEstimate.FormatSize(volumes.Sum(v => v.Bytes))}）"
                  + $"；预计 {plan.PlannedVolumeCount} 卷");

            // ───────── 只做 7z 分卷这条路（本机没装 WinRAR 时的出路） ─────────
            if (request.SkipOuterRar)
            {
                Report(PackingStep.Finished, StatusText.PackSuccess, 100);

                stopwatch.Stop();

                var volumesOnly = new PackingResult
                {
                    State = PackingState.Succeeded,
                    Message = StatusText.PackPartialVolumesOnly,
                    SkippedOuterRar = true,
                    Volumes = volumes,
                    VerificationDetail = "只做了 7z 分卷（按要求跳过外层 rar）",
                    LogLines = lines,
                    Elapsed = stopwatch.Elapsed
                };

                Write(volumesOnly.Describe());

                return volumesOnly;
            }

            // ───────── ⑤ 第二步：外层加密 rar ─────────
            Report(PackingStep.OuterRar, StatusText.PackStepRar);

            PackStepResult rarStep;

            try
            {
                rarStep = await _runner.RunAsync(
                        PackToolKind.Rar,
                        BuildRarArguments(plan, request.EffectiveOuterPassword),
                        request.EffectiveOuterPassword,
                        WrapProgress(progress, PackingStep.OuterRar, StatusText.PackStepRar),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                DeleteIncompleteRar(plan.RarPath, Write);

                return Cancelled("生成外层 rar 时被取消", null, volumes);
            }

            if (rarStep.Cancelled)
            {
                DeleteIncompleteRar(plan.RarPath, Write);

                return Cancelled("生成外层 rar 时被取消（分卷还在 B 里）", null, volumes);
            }

            if (!rarStep.Success)
            {
                DeleteIncompleteRar(plan.RarPath, Write);

                return Fail(
                    $"外层 rar 没做成（Rar.exe 退出码 {rarStep.ExitCode}）。" + DescribeOutputTail(rarStep)
                    + $"7z 分卷还在：{plan.OutputFolder}（{volumes.Count} 个）—— 修好原因后可以重试，"
                    + "重试前请先清空 B，让程序从干净状态重建。",
                    StatusText.PackFailed,
                    volumes);
            }

            // ───────── ⑥ 核对产物（对不上就不显示成功） ─────────
            Report(PackingStep.Verifying, StatusText.PackStepVerify);

            PackingVerification verification;

            try
            {
                PackStepResult listing = await _runner.RunAsync(
                        PackToolKind.Rar,
                        BuildRarListArguments(plan, request.EffectiveOuterPassword),
                        request.EffectiveOuterPassword,
                        null,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (listing.Cancelled)
                {
                    return Cancelled("校验产物时被取消（rar 已经生成，但没核对过）", plan.RarPath, volumes);
                }

                if (!listing.Success)
                {
                    verification = new PackingVerification
                    {
                        Ok = false,
                        Detail = $"列不出 rar 的条目（Rar.exe 退出码 {listing.ExitCode}），没法确认它装的是什么。"
                                 + DescribeOutputTail(listing),
                        Problems = new[] { "列条目失败" }
                    };
                }
                else
                {
                    verification = PackingVerifier.Verify(
                        PackingVerifier.ParseBareList(listing.StandardOutput),
                        Path.GetFileName(plan.OutputFolder),
                        volumes);
                }
            }
            catch (OperationCanceledException)
            {
                return Cancelled("校验产物时被取消（rar 已经生成，但没核对过）", plan.RarPath, volumes);
            }

            Write("产物校验：" + verification.Detail);

            if (!verification.Ok)
            {
                /*
                 * 不变量 6：**产物对不上就不是成功**。
                 * 这里刻意不删 rar：它确实存在、也可能对用户有用（比如条目名与预期不同），
                 * 删掉是不可逆的；用户看到这句话之后自己决定。
                 */
                return Fail(
                    verification.Detail + "。结果文件没有被当成成功：请先自己打开看一眼再决定留不留。",
                    StatusText.PackVerifyFailed,
                    volumes);
            }

            long rarBytes = 0;

            try
            {
                rarBytes = new FileInfo(plan.RarPath).Length;
            }
            catch
            {
                rarBytes = 0;
            }

            Report(PackingStep.Finished, StatusText.PackSuccess, 100);

            stopwatch.Stop();

            var result = new PackingResult
            {
                State = PackingState.Succeeded,
                Message = StatusText.PackSuccess,
                RarPath = plan.RarPath,
                RarBytes = rarBytes,
                Volumes = volumes,
                VerificationDetail = verification.Detail,
                LogLines = lines,
                Elapsed = stopwatch.Elapsed
            };

            Write(result.Describe());

            return result;
        }

        /// <summary>
        /// 第一步的命令行（**只允许在本目录内拼**）。
        ///
        /// <para><c>-mhe=on</c>：连文件名一起加密；<c>-v&lt;n&gt;m</c>：按 MiB 分卷；
        /// <c>-bsp1</c>：进度打到 stdout（**不能加 <c>-bd</c>**，那会把进度指示器整个压掉，
        /// 与解压侧踩过的坑同一个）；<c>-sccUTF-8</c>：输出用 UTF-8，中文名才认得出来；
        /// <c>-y</c>：不问任何问题（无人值守）。</para>
        /// </summary>
        internal static List<string> BuildVolumeArguments(PackingPlan plan, string password)
        {
            string volumeMegabytes = (plan.VolumeSizeBytes / (1024L * 1024L))
                .ToString(CultureInfo.InvariantCulture);

            return new List<string>
            {
                "a",
                "-t7z",
                "-mx=5",
                "-mhe=on",
                "-bsp1",
                "-sccUTF-8",
                "-y",
                "-p" + password,
                "-v" + volumeMegabytes + "m",
                Path.Combine(plan.OutputFolder, plan.VolumeBaseName),
                Path.Combine(plan.SourceFolder, "*")
            };
        }

        /// <summary>
        /// 第二步的命令行：<c>a -hp&lt;密码&gt; -r -ep1 [-ibck] -y "&lt;B&gt;.rar" "&lt;B&gt;"</c>。
        ///
        /// <para><c>-hp</c>：连文件名一起加密；<c>-r</c>：递归子目录；<c>-ep1</c>：去掉上层目录、
        /// 只保留 <c>B</c> 这一层（本机实测：条目名就是 <c>B\xxx.7z.001</c>，解出来有个文件夹）。</para>
        ///
        /// <para>退到 <c>WinRAR.exe</c> 时补 <c>-ibck</c>（后台 / 托盘运行）——
        /// 那个是 GUI 程序，不加它会在用户桌面弹出一个进度窗口并抢焦点。</para>
        /// </summary>
        private List<string> BuildRarArguments(PackingPlan plan, string outerPassword)
        {
            var args = new List<string> { "a", "-hp" + outerPassword, "-r", "-ep1" };

            if (_tools.IsUsingWinRarGuiForRar)
            {
                args.Add("-ibck");
            }

            args.Add("-y");
            args.Add(plan.RarPath);
            args.Add(plan.OutputFolder);

            return args;
        }

        /// <summary>
        /// 核对产物时的列条目命令：<c>lb -p&lt;密码&gt; &lt;rar&gt;</c>。
        /// 用 <c>lb</c>（bare list，一行一个名字）而不是 <c>l</c>：后者的表头 / 分隔线 / 列宽
        /// 属于"某一版 RAR 的排版"，钉进代码里迟早会随版本变（见 <see cref="PackingVerifier"/>）。
        /// </summary>
        private List<string> BuildRarListArguments(PackingPlan plan, string outerPassword)
        {
            var args = new List<string> { "lb", "-p" + outerPassword };

            if (_tools.IsUsingWinRarGuiForRar)
            {
                args.Add("-ibck");
            }

            args.Add(plan.RarPath);

            return args;
        }

        /// <summary>
        /// 7z 的进度行**不带密码**（这里只转发百分比），但仍然统一走一层包装：
        /// 上层拿到的是"这一步到哪了"，与工具输出格式无关。
        /// </summary>
        private static IProgress<PackStepProgress>? WrapProgress(
            IProgress<PackingProgress>? progress,
            PackingStep step,
            string stepText)
        {
            if (progress == null)
            {
                return null;
            }

            return new Progress<PackStepProgress>(p => progress.Report(new PackingProgress
            {
                Step = step,
                StepText = stepText,
                Percent = p.Percent,
                Detail = p.Detail
            }));
        }

        /// <summary>从 B 里数出 7z 切出来的分卷（按 <c>&lt;A名&gt;.7z.NNN</c> 认，不自己拼名字）。</summary>
        internal static List<PackingVolume> CollectVolumes(PackingPlan plan)
        {
            var volumes = new List<PackingVolume>();

            try
            {
                if (!Directory.Exists(plan.OutputFolder))
                {
                    return volumes;
                }

                foreach (string file in Directory.EnumerateFiles(plan.OutputFolder))
                {
                    string fileName = Path.GetFileName(file);

                    if (!fileName.StartsWith(plan.VolumeBaseName + ".", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string suffix = fileName[(plan.VolumeBaseName.Length + 1)..];

                    if (suffix.Length == 0 || !suffix.All(char.IsDigit))
                    {
                        continue;
                    }

                    long bytes = 0;

                    try
                    {
                        bytes = new FileInfo(file).Length;
                    }
                    catch
                    {
                        bytes = 0;
                    }

                    volumes.Add(new PackingVolume
                    {
                        FileName = fileName,
                        Path = file,
                        Index = int.TryParse(suffix, out int index) ? index : 0,
                        Bytes = bytes
                    });
                }
            }
            catch
            {
                // 数不出来就返回已经数到的那些：这里只影响"报告里写几个"，不该把成功判成失败。
            }

            return volumes
                .OrderBy(v => v.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// 分卷连续性检查（不变量 7 的口径：缺卷要报"缺哪几个"，不是笼统说一句）。
        /// 返回空串 = 通过。
        /// </summary>
        internal static string ValidateVolumes(IReadOnlyList<PackingVolume> volumes, PackingPlan plan)
        {
            if (volumes.Count == 0)
            {
                return $"7-Zip 报成功，但 {plan.OutputFolder} 里一个分卷都没有 —— 产物不对，这次不算成功。";
            }

            var missing = new List<string>();

            for (int expected = 1; expected <= volumes.Count; expected++)
            {
                if (volumes.All(v => v.Index != expected))
                {
                    missing.Add($"{plan.VolumeBaseName}.{expected:000}");
                }
            }

            return missing.Count == 0
                ? string.Empty
                : $"7-Zip 切出来的分卷不连续，缺少：{string.Join('、', missing)}。"
                  + "产物不完整，这次不算成功。";
        }

        /// <summary>删掉本次没做完的 rar（**只在本次真的创建过它之后**调）。</summary>
        private static void DeleteIncompleteRar(string rarPath, Action<string> write)
        {
            try
            {
                if (!File.Exists(rarPath))
                {
                    return;
                }

                File.Delete(rarPath);
                write($"已清理没做完的 rar：{rarPath}");
            }
            catch (Exception ex)
            {
                write($"没做完的 rar 删不掉（请自己删）：{rarPath} —— {PackPasswordGuard.Sanitize(ex.Message, null)}");
            }
        }

        /// <summary>把外部工具输出的最后几行附在失败原因后面（用户与开发者都靠它定位）。</summary>
        private static string DescribeOutputTail(PackStepResult step)
        {
            string combined = step.CombinedOutput;

            if (string.IsNullOrWhiteSpace(combined))
            {
                return string.Empty;
            }

            string[] lines = combined
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToArray();

            if (lines.Length == 0)
            {
                return string.Empty;
            }

            string tail = string.Join(" / ", lines.TakeLast(3));

            return "工具输出（末几行）：" + tail + "。";
        }
    }
}
