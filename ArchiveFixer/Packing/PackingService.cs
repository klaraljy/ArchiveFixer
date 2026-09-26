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
    /// B 整个压成一个带密码的**外层容器** → 结果 = 压缩包 B。
    ///
    /// <para><b>外层容器三选一</b>（用户 2026-09-23 决定，取代早先的 <c>bool SkipOuterRar</c>）：
    /// <c>rar</c>（默认）/ <c>7z</c> / <c>不做外层</c>。为什么必须有三条：
    /// <c>Rar.exe</c> / <c>WinRAR.exe</c> 是**共享软件**，RARLAB 的 EULA 禁止随任何软件包分发
    /// （AGENTS.md §3.1），所以"本机没装 WinRAR"是常态 —— 那时除了"去装一个"和"什么都不做"，
    /// 还必须有第三条不需要额外安装的路（7-Zip 是 LGPL、本来就随程序分发）。</para>
    ///
    /// <para><b>两步 + 一次校验</b>（命令行的参数模板**只允许出现在本目录内**，
    /// docs/打包功能.md §5）：</para>
    /// <code>
    /// ① 7z.exe  a -t7z -mx=5 -mhe=on -bsp1 -sccUTF-8 -y -p&lt;密码&gt; -v512m "&lt;B&gt;\&lt;A名&gt;.7z" "&lt;A&gt;\*"
    /// ②a Rar.exe a -hp&lt;密码&gt; -r -ep1 "&lt;B&gt;.rar" "&lt;B&gt;"          ← 外层容器 = rar
    /// ②b 7z.exe  a -t7z -mhe=on -mx=5 -bsp1 -sccUTF-8 -y -p&lt;密码&gt; "&lt;B&gt;.7z" "&lt;B&gt;"   ← 外层容器 = 7z
    /// ③a Rar.exe lb -p&lt;密码&gt; "&lt;B&gt;.rar"                            ← 按容器校验
    /// ③b 7z.exe  l -slt -p&lt;密码&gt; "&lt;B&gt;.7z"
    /// </code>
    ///
    /// <para><b>几条不肯让步的规矩</b>：</para>
    /// <list type="bullet">
    /// <item><description>7z **不能**建 rar（RAR 算法是专有的）。选了 rar 而本机没有 <c>Rar.exe</c> 时
    /// **明确报错**并给出**三条**出路，⛔ 绝不静默换成别的容器、也绝不把 7z 的产物改名成 <c>.rar</c> 糊过去；</description></item>
    /// <item><description><c>Rar.exe</c> 是共享软件：**只检测、只调用，绝不打包、绝不复制**（AGENTS.md §3.1）；</description></item>
    /// <item><description>取消只杀**自己启动的那个 PID 及其子进程**（在 <see cref="PackProcessRunner"/> 里）；</description></item>
    /// <item><description>失败 / 取消**不留半个外层容器**；分卷保留（重试前要用户自己清空 B —— 见下）；</description></item>
    /// <item><description>完成之后**按容器**核对产物，对不上**不显示成功**（不变量 6）。</description></item>
    /// </list>
    ///
    /// <para><b>为什么 B 必须不存在或为空</b>：B 会被整个装进外层容器，混进旧文件就会让"结果 = 压缩包 B"
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
                    OuterContainer = request?.OuterContainer ?? PackOuterContainer.Rar,
                    Volumes = volumes ?? Array.Empty<PackingVolume>(),
                    LogLines = lines,
                    Elapsed = stopwatch.Elapsed
                };
            }

            PackingResult Cancelled(string reason, string? outerPath, IReadOnlyList<PackingVolume>? volumes = null)
            {
                stopwatch.Stop();
                Write(StatusText.PackCancelled + "：" + reason);

                return new PackingResult
                {
                    State = PackingState.Cancelled,
                    Message = StatusText.PackCancelled,
                    FailureReason = reason,
                    OuterContainer = request?.OuterContainer ?? PackOuterContainer.Rar,
                    OuterPath = outerPath,
                    Volumes = volumes ?? Array.Empty<PackingVolume>(),
                    LogLines = lines,
                    Elapsed = stopwatch.Elapsed
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
             * 外层容器的可用性检查，全部**在切分卷之前**做完（三条路各查各的）：
             *
             * 先花十几分钟切出几十个分卷、再告诉他"外层做不了"，等于让他在最贵的步骤上白等。
             * 这一步只是**检测**，绝不复制、绝不内置 Rar.exe（共享软件，AGENTS.md §3.1）。
             *
             * ⚠ 2026-09-26 第 46 条改口径（用户原话："如果用户没有装 WinRAR，那就弄 7z 吧"）：
             * 外面选的是 rar 但本机没有 Rar.exe 时**自动改用 7z 外层**（7-Zip 是随程序分发的，无需安装），
             * 并在日志与结果里如实写明"用的是 7z 而不是 rar" —— 不再拿"去装 WinRAR"把整件事卡住。
             */
            PackOuterContainer effectiveContainer = request.OuterContainer;

            switch (request.OuterContainer)
            {
                case PackOuterContainer.Rar:
                    if (!_tools.RarExists)
                    {
                        effectiveContainer = PackOuterContainer.SevenZip;

                        Write("Rar.exe：" + _tools.DescribeNoRarAvailable());
                        Write(
                            "本机没有 Rar.exe（WinRAR 是共享软件，程序不分发它）—— "
                            + "按你说的改用 **7z 外层**（7-Zip 随程序分发、无需额外安装）："
                            + $"结果会是 {Path.GetFileName(plan.SevenZipOuterPath)}，而不是 .rar。");

                        if (!_tools.SevenZipExists)
                        {
                            return Fail(
                                $"本机既没有 Rar.exe 也没有可用的 7-Zip（{_tools.SevenZipExePath}），"
                                + "没法生成任何外层容器。请检查 7-Zip 那一格设置，或重新解压一份程序。",
                                StatusText.PackFailed);
                        }
                    }
                    else
                    {
                        Write("外层容器 rar：" + _tools.DescribeRarResolution());
                    }

                    break;

                case PackOuterContainer.SevenZip:
                    if (!_tools.SevenZipExists)
                    {
                        Write("7-Zip：" + _tools.DescribeResolution());

                        return Fail(
                            $"外层容器选了 7z，但没找到 7-Zip 程序：{_tools.SevenZipExePath}。"
                            + "它是随程序分发的（tools\\7zip），请检查这一格设置或重新解压一份程序。",
                            StatusText.PackFailed);
                    }

                    Write("外层容器 7z：" + _tools.DescribeResolution() + "（无需额外安装）");
                    break;

                default:
                    Write("按要求不做外层容器：" + StatusText.PackOuterNoneText);
                    break;
            }

            Write(plan.DescribeOuterContainer(effectiveContainer));

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

            /*
             * ───────── ③b 单文件源：先给它建同名文件夹并把它放进去（用户 2026-09-26 第 46 条）─────────
             *
             * 原话："我们就在外面生成一个文件夹。111(文件夹)\111.MP4，然后我们再对其进行打包操作"。
             * ⛔ 那条路**不动源文件**：同盘用**硬链接**（零字节），跨盘才复制（先查空间）。
             * 那个新建的文件夹算**其余物**（原话："他不算是原包的内容"）。
             */
            if (!string.IsNullOrWhiteSpace(plan.WrapperFolderToCreate))
            {
                if (!TryPrepareSingleFileSource(plan, Write, out string prepareProblem))
                {
                    return Fail(prepareProblem, StatusText.PackFailed);
                }
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

            // ───────── 不做外层容器这条路（本机没装 WinRAR 时最省的出路） ─────────
            if (!request.OuterContainer.HasOuterArtifact())
            {
                Report(PackingStep.Finished, StatusText.PackSuccess, 100);

                stopwatch.Stop();

                var volumesOnly = new PackingResult
                {
                    State = PackingState.Succeeded,
                    Message = StatusText.PackPartialVolumesOnly,
                    OuterContainer = PackOuterContainer.None,
                    Volumes = volumes,
                    VerificationDetail = "只做了 7z 分卷（按要求不做外层容器）",
                    LogLines = lines,
                    Elapsed = stopwatch.Elapsed
                };

                Write(volumesOnly.Describe());

                return volumesOnly;
            }

            // ───────── ⑤ 第二步：外层容器（rar 或 7z） ─────────
            // ⚠ 用**实际生效的**容器（第 46 条：没装 WinRAR 时 rar 已在上面的检查里降级成 7z）。
            bool outerIsRar = effectiveContainer == PackOuterContainer.Rar;
            string outerPath = outerIsRar ? plan.RarPath : plan.SevenZipOuterPath;
            string outerName = outerIsRar ? "外层 rar" : "外层 7z";
            string outerTool = outerIsRar ? "Rar.exe" : "7-Zip";
            PackToolKind outerToolKind = outerIsRar ? PackToolKind.Rar : PackToolKind.SevenZip;
            string outerStepText = outerIsRar ? StatusText.PackStepRar : StatusText.PackStepSevenZipOuter;

            Report(PackingStep.OuterContainer, outerStepText);

            PackStepResult outerStep;

            try
            {
                outerStep = await _runner.RunAsync(
                        outerToolKind,
                        outerIsRar
                            ? BuildRarArguments(plan, request.EffectiveOuterPassword)
                            : BuildSevenZipOuterArguments(plan, request.EffectiveOuterPassword),
                        request.EffectiveOuterPassword,
                        WrapProgress(progress, PackingStep.OuterContainer, outerStepText),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                DeleteIncompleteOuter(outerPath, outerName, Write);

                return Cancelled($"生成{outerName}时被取消", null, volumes);
            }

            if (outerStep.Cancelled)
            {
                DeleteIncompleteOuter(outerPath, outerName, Write);

                return Cancelled($"生成{outerName}时被取消（分卷还在 B 里）", null, volumes);
            }

            if (!outerStep.Success)
            {
                DeleteIncompleteOuter(outerPath, outerName, Write);

                return Fail(
                    $"{outerName}没做成（{outerTool} 退出码 {outerStep.ExitCode}）。" + DescribeOutputTail(outerStep)
                    + $"7z 分卷还在：{plan.OutputFolder}（{volumes.Count} 个）—— 修好原因后可以重试，"
                    + "重试前请先清空 B，让程序从干净状态重建。",
                    StatusText.PackFailed,
                    volumes);
            }

            // ───────── ⑥ 核对产物（**按容器**读一遍；对不上就不显示成功） ─────────
            string verifyStepText = outerIsRar ? StatusText.PackStepVerify : StatusText.PackStepVerifySevenZip;

            Report(PackingStep.Verifying, verifyStepText);

            PackingVerification verification;

            try
            {
                PackStepResult listing = await _runner.RunAsync(
                        outerToolKind,
                        outerIsRar
                            ? BuildRarListArguments(plan, request.EffectiveOuterPassword)
                            : BuildSevenZipListArguments(plan, request.EffectiveOuterPassword),
                        request.EffectiveOuterPassword,
                        null,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (listing.Cancelled)
                {
                    return Cancelled($"校验产物时被取消（{outerName}已经生成，但没核对过）", outerPath, volumes);
                }

                if (!listing.Success)
                {
                    verification = new PackingVerification
                    {
                        Ok = false,
                        Detail = $"列不出{outerName}的条目（{outerTool} 退出码 {listing.ExitCode}），没法确认它装的是什么。"
                                 + DescribeOutputTail(listing),
                        Problems = new[] { "列条目失败" }
                    };
                }
                else
                {
                    verification = PackingVerifier.Verify(
                        outerIsRar
                            ? PackingVerifier.ParseBareList(listing.StandardOutput)
                            : PackingVerifier.ParseSevenZipSltList(listing.StandardOutput),
                        Path.GetFileName(plan.OutputFolder),
                        volumes,
                        outerIsRar ? "rar" : "7z");
                }
            }
            catch (OperationCanceledException)
            {
                return Cancelled($"校验产物时被取消（{outerName}已经生成，但没核对过）", outerPath, volumes);
            }

            Write("产物校验：" + verification.Detail);

            if (!verification.Ok)
            {
                /*
                 * 不变量 6：**产物对不上就不是成功**。
                 * 这里刻意不删外层容器：它确实存在、也可能对用户有用（比如条目名与预期不同），
                 * 删掉是不可逆的；用户看到这句话之后自己决定。
                 */
                return Fail(
                    verification.Detail + "。结果文件没有被当成成功：请先自己打开看一眼再决定留不留。",
                    StatusText.PackVerifyFailed,
                    volumes);
            }

            long outerBytes = 0;

            try
            {
                outerBytes = new FileInfo(outerPath).Length;
            }
            catch
            {
                outerBytes = 0;
            }

            /*
             * ───────── ⑦ 收尾：原包 / 其余物（用户 2026-09-26 第 46 条）─────────
             *
             * ⛔ 位置是刻意的：**只有外层容器生成 + 校验通过**之后才走这一步；
             * 失败 / 取消 / 校验没过都在前面 return 掉了，一个字节都不会动（红线）。
             * 收尾出问题也不改结论（结果文件已经在那儿了），只把话写进日志。
             */
            PackingCleanupOutcome cleanup = PackingCleanup.Run(plan, succeeded: true, outerPath);

            foreach (string line in cleanup.LogLines)
            {
                Write(line);
            }

            Report(PackingStep.Finished, StatusText.PackSuccess, 100);

            stopwatch.Stop();

            var result = new PackingResult
            {
                State = PackingState.Succeeded,
                Message = StatusText.PackSuccess,
                OuterContainer = effectiveContainer,
                OuterPath = outerPath,
                OuterBytes = outerBytes,
                Volumes = volumes,
                VerificationDetail = verification.Detail,
                CleanupNote = cleanup.Ran
                    ? $"原包：{(cleanup.SourceMoved ? "已移入其余物" : "不动")}；其余物：{cleanup.RestSummary}"
                    : string.Empty,
                LogLines = lines,
                Elapsed = stopwatch.Elapsed
            };

            Write(result.Describe());

            return result;
        }

        /// <summary>
        /// 单文件源的准备：建同名文件夹 + 把源文件放进去（**硬链接优先**）。
        ///
        /// <para>⛔ 源文件一个字节都不改：同盘 <c>CreateHardLinkW</c>（零字节、多一个名字），
        /// 跨盘只能复制（先查空间，不够就不做）。放不进去就**当场停**（不开始切分卷）。</para>
        /// </summary>
        private bool TryPrepareSingleFileSource(PackingPlan plan, Action<string> write, out string problem)
        {
            problem = string.Empty;

            string source = plan.SourceResolution.SourcePath;
            string folder = plan.WrapperFolderToCreate;
            string target = Path.Combine(folder, Path.GetFileName(source));

            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                problem = $"建不出同名文件夹（{folder}）：{ex.Message}";
                return false;
            }

            if (File.Exists(target))
            {
                write($"单文件准备：同名文件夹里已经有这个文件了，直接用：{target}");
                return true;
            }

            try
            {
                // 同一个盘：硬链接（零字节、不改源文件）。
                if (TryCreateHardLink(target, source))
                {
                    write($"单文件准备：已把 {Path.GetFileName(source)} 接进同名文件夹（硬链接，不复制字节、源文件不动）→ {target}");
                    return true;
                }

                // 跨盘：只能复制 —— 先查空间。
                long length = new FileInfo(source).Length;
                long? free = _availableSpaceProbe(folder);

                if (free.HasValue && free.Value < length + (256L * 1024 * 1024))
                {
                    problem = $"要把 {Path.GetFileName(source)} 复制进同名文件夹，需要 {TaskSpaceEstimate.FormatSize(length)}，"
                            + $"但目标盘只剩 {(free.HasValue ? TaskSpaceEstimate.FormatSize(free.Value) : "未知")}（还要留 256 MiB 余量）。"
                            + "请自己建一个同名文件夹把它放进去，或者换一个落点。";
                    return false;
                }

                File.Copy(source, target, overwrite: false);

                write($"单文件准备：已把 {Path.GetFileName(source)} 复制进同名文件夹（跨盘，源文件仍在原地）→ {target}");
                return true;
            }
            catch (Exception ex)
            {
                problem = $"把源文件放进同名文件夹失败（{source} → {target}）：{PackPasswordGuard.Sanitize(ex.Message, plan.RunOptions?.CustomOutputDirectory)}";
                return false;
            }
        }

        /// <summary>同盘硬链接（Windows）。做不了就返回 false，由调用方回落复制。</summary>
        private static bool TryCreateHardLink(string linkPath, string existingPath)
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            try
            {
                return CreateHardLinkW(linkPath, existingPath, IntPtr.Zero);
            }
            catch
            {
                return false;
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

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
        /// 第二步（外层容器 = 7z）的命令行：
        /// <c>a -t7z -mhe=on -mx=5 -bsp1 -sccUTF-8 -y -p&lt;密码&gt; "&lt;B&gt;.7z" "&lt;B&gt;"</c>。
        ///
        /// <para>与第一步同一套安全传参 / 进度 / 取消做法（<c>-bsp1</c> 进度、<c>-sccUTF-8</c> 输出编码、
        /// <c>-y</c> 无人值守），差别只有两条：不分卷（外层就是一个文件），以及
        /// <c>-mhe=on</c> 让**文件名也加密**（与 rar 那边的 <c>-hp</c> 对齐）。</para>
        ///
        /// <para>为什么参数里给的是 <c>B</c> 而不是 <c>B\*</c>：<c>7z a out.7z &lt;B&gt;</c> 会把 <c>B</c>
        /// 这一层目录名带进归档（条目是 <c>B\x.7z.001</c>），与 <c>Rar.exe -ep1</c> 的形态一一对应 ——
        /// 用户解出来看到的都是一个文件夹，而不是一堆散着的分卷（docs/打包功能.md §4）。</para>
        /// </summary>
        internal static List<string> BuildSevenZipOuterArguments(PackingPlan plan, string outerPassword)
        {
            return new List<string>
            {
                "a",
                "-t7z",
                "-mx=5",
                "-mhe=on",
                "-bsp1",
                "-sccUTF-8",
                "-y",
                "-p" + outerPassword,
                plan.SevenZipOuterPath,
                plan.OutputFolder
            };
        }

        /// <summary>
        /// 核对 7z 外层容器时的列条目命令：<c>l -slt -sccUTF-8 -y -p&lt;密码&gt; "&lt;B&gt;.7z"</c>。
        ///
        /// <para>为什么用 <c>-slt</c>（技术信息）而不是默认的表格排版：默认输出是给人看的表格，
        /// 列宽随内容变、还带汇总行，把它钉进代码等于把"某一版 7-Zip 的排版"当成契约；
        /// <c>-slt</c> 是一行一个 <c>键 = 值</c> 的机器格式（见 <see cref="PackingVerifier.ParseSevenZipSltList"/>）。
        /// 代价是它一开始会多出一段描述**归档自己**的块，那个由解析函数按分隔线切掉。</para>
        /// </summary>
        internal static List<string> BuildSevenZipListArguments(PackingPlan plan, string outerPassword)
        {
            return new List<string>
            {
                "l",
                "-slt",
                "-sccUTF-8",
                "-y",
                "-p" + outerPassword,
                plan.SevenZipOuterPath
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

        /// <summary>删掉本次没做完的外层容器（**只在本次真的创建过它之后**调）。</summary>
        private static void DeleteIncompleteOuter(string outerPath, string outerName, Action<string> write)
        {
            try
            {
                if (!File.Exists(outerPath))
                {
                    return;
                }

                File.Delete(outerPath);
                write($"已清理没做完的{outerName}：{outerPath}");
            }
            catch (Exception ex)
            {
                write($"没做完的{outerName}删不掉（请自己删）：{outerPath} —— {PackPasswordGuard.Sanitize(ex.Message, null)}");
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
