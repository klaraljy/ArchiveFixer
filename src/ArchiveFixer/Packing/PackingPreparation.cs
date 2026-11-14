using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using ArchiveFixer.Storage;
using System;
using System.IO;

namespace ArchiveFixer.Packing;

/// <summary>
/// 打包的**计划阶段**（<see cref="PackingService"/> 的协作类，职责：计划 / 落点准备）。
///
/// <para>这一段的口径是「**在动任何字节之前，把所有判据一次判完**」：密码能不能用、源与落点怎么规划、
/// 外层容器做不做得出来、空间够不够、落点目录建不建得出来、单文件源进不进得去。
/// 顺序是刻意的 —— 外层容器的可用性检查必须排在切分卷**之前**：先花十几分钟切出几十个分卷、
/// 再告诉他"外层做不了"，等于让他在最贵的步骤上白等。</para>
///
/// <para>⛔ 它不抛异常：失败落成 <see cref="PackingPreparedRun.FailureReason"/>
/// （日志与结果用的是同一句），<see cref="PackingPreparedRun.Plan"/> 为 null 就是"别往下走了"。</para>
/// </summary>
internal sealed class PackingPreparation
{
    private readonly ToolLocator _tools;
    private readonly Func<string, long?> _availableSpaceProbe;

    /// <param name="tools">外部工具路径的唯一来源。</param>
    /// <param name="availableSpaceProbe">取目标盘可用空间（注入点：让"空间不够就不开始"可确定性地测）。</param>
    internal PackingPreparation(ToolLocator tools, Func<string, long?> availableSpaceProbe)
    {
        _tools = tools;
        _availableSpaceProbe = availableSpaceProbe;
    }

    /// <summary>
    /// 校验请求、算出规划、把落点准备好。返回的 <see cref="PackingPreparedRun.Plan"/> 为 null = 不能开跑。
    /// </summary>
    /// <param name="request">打包请求（含两个密码；只存内存）。</param>
    /// <param name="runLog">日志出口（每一句都进结果与回调）。</param>
    internal PackingPreparedRun TryPrepare(PackingRequest request, PackingRunLog runLog)
    {
        static PackingPreparedRun Rejected(string reason, string message)
        {
            return new PackingPreparedRun
            {
                FailureReason = reason,
                FailureMessage = message
            };
        }

        // ───────── ① 请求与落点（在动任何字节之前全部判完） ─────────
        if (!PackingPasswordPolicy.IsUsable(request?.Password))
        {
            return Rejected(PackingPasswordPolicy.RequiredMessage, StatusText.PackFailed);
        }

        if (!PackingPlan.TryCreate(request!, out PackingPlan? plan, out string planError) || plan == null)
        {
            return Rejected(planError, StatusText.PackFailed);
        }

        runLog.Write("开始打包：" + request!.DescribeForLog());
        runLog.Write("7-Zip：" + _tools.DescribeResolution());

        /*
         * 外层容器的可用性检查，全部**在切分卷之前**做完：
         *
         * 先花十几分钟切出几十个分卷、再告诉他"外层做不了"，等于让他在最贵的步骤上白等。
         * 这一步只是**检测**，绝不复制、绝不内置 Rar.exe（共享软件，AGENTS.md §3.1）。
         *
         * ⚠ 2026-09-26 第 46 条改口径（用户原话："如果用户没有装 WinRAR，那就弄 7z 吧"）：
         * 外面选的是 rar 但本机没有 Rar.exe 时**自动改用 7z 外层**（7-Zip 是随程序分发的，无需安装），
         * 并在日志与结果里如实写明"用的是 7z 而不是 rar" —— 不再拿"去装 WinRAR"把整件事卡住。
         * ⛔ 这个结论**界面也要用**（⑤页摘要 / 确认弹窗里那句"最终产物"），所以它只有一处计算：
         * `PackingOuterContainerResolver`（见 `PackingViewModel.EffectiveOuterContainer`）。
         */
        PackingOuterContainerResolution outer = PackingOuterContainerResolver.Resolve(
            request.OuterContainer,
            _tools.RarExists,
            _tools.SevenZipExists);

        PackOuterContainer effectiveContainer = outer.Effective;

        if (outer.Impossible)
        {
            runLog.Write("7-Zip：" + _tools.DescribeResolution());

            return Rejected(
                $"本机既没有 Rar.exe 也没有可用的 7-Zip（{_tools.SevenZipExePath}），"
                + "没法生成任何外层容器。请检查 7-Zip 那一格设置，或重新解压一份程序。",
                StatusText.PackFailed);
        }

        if (outer.FellBackToSevenZip)
        {
            runLog.Write("Rar.exe：" + _tools.DescribeNoRarAvailable());
            runLog.Write(outer.Note + $"结果会是 {Path.GetFileName(plan.SevenZipOuterPath)}。");
            runLog.Write("外层容器 7z：" + _tools.DescribeResolution() + "（无需额外安装）");
        }
        else if (effectiveContainer == PackOuterContainer.Rar)
        {
            runLog.Write("外层容器 rar：" + _tools.DescribeRarResolution());
        }
        else if (effectiveContainer == PackOuterContainer.SevenZip)
        {
            runLog.Write("外层容器 7z：" + _tools.DescribeResolution() + "（无需额外安装）");
        }
        else
        {
            runLog.Write("按要求不做外层容器：" + StatusText.PackOuterNoneText);
        }

        runLog.Write(plan.DescribeOuterContainer(effectiveContainer));

        // ───────── ② 空间门（与解压侧同一套 SpaceGate 口径） ─────────
        long? available = _availableSpaceProbe(plan.OutputFolder);

        SpaceGateDecision decision = SpaceGate.Check(
            plan.RequiredSpaceBytes,
            available,
            SpaceGate.DefaultReserveBytes);

        runLog.Write("空间门：" + decision.ToLogLine());

        if (!decision.Allowed)
        {
            return Rejected(decision.Reason, StatusText.DiskSpaceInsufficient);
        }

        // ───────── ③ 准备 B ─────────
        try
        {
            Directory.CreateDirectory(plan.OutputFolder);
        }
        catch (Exception ex)
        {
            return Rejected(
                $"创建输出文件夹失败：{PackPasswordGuard.Sanitize(ex.Message, request.Password)}",
                StatusText.PackFailed);
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
            if (!TryPrepareSingleFileSource(plan, runLog, out string prepareProblem))
            {
                return Rejected(prepareProblem, StatusText.PackFailed);
            }
        }

        runLog.Write(plan.DescribeVolumePlan());

        return new PackingPreparedRun
        {
            Plan = plan,
            EffectiveContainer = effectiveContainer
        };
    }

    /// <summary>
    /// 单文件源的准备：建同名文件夹 + 把源文件放进去（**硬链接优先**）。
    ///
    /// <para>⛔ 源文件一个字节都不改：同盘 <c>CreateHardLinkW</c>（零字节、多一个名字），
    /// 跨盘只能复制（先查空间，不够就不做）。放不进去就**当场停**（不开始切分卷）。</para>
    /// </summary>
    private bool TryPrepareSingleFileSource(PackingPlan plan, PackingRunLog runLog, out string problem)
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
            runLog.Write($"单文件准备：同名文件夹里已经有这个文件了，直接用：{target}");
            return true;
        }

        try
        {
            // 同一个盘：硬链接（零字节、不改源文件）。
            if (TryCreateHardLink(target, source))
            {
                runLog.Write($"单文件准备：已把 {Path.GetFileName(source)} 接进同名文件夹（硬链接，不复制字节、源文件不动）→ {target}");
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

            runLog.Write($"单文件准备：已把 {Path.GetFileName(source)} 复制进同名文件夹（跨盘，源文件仍在原地）→ {target}");
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
}
