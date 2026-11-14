using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using ArchiveFixer.Packing;
using ArchiveFixer.Storage;
using System;
using System.IO;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// ⑤打包页的「摘要」那一组：内容摘要行、最终产物那行、外层容器的实际结论、结果区文本。
///
/// <para>⛔ 外层容器"实际会用哪一个"只有一个来源（<see cref="PackingOuterContainerResolver"/>，
/// 与 <c>PackingService</c> 引的是同一份计算）：⑤页摘要、确认弹窗里那句"最终产物"、结果区
/// 都必须写**实际**会产出的那个文件名 —— 否则没装 WinRAR 的机器上会写着 <c>1111.rar</c>、
/// 跑完却得到 <c>1111.7z</c>（界面撒谎）。</para>
/// </summary>
internal sealed class PackingSummaryComposer
{
    private readonly PackingViewModel _vm;
    private readonly ToolLocator _tools;
    private readonly PackingOptionsCoordinator _options;

    /// <summary>最近一次算出来的规划（摘要 / 确认弹窗预览都用它）。</summary>
    private PackingPlan? _lastPlan;

    internal PackingSummaryComposer(
        PackingViewModel viewModel,
        ToolLocator tools,
        PackingOptionsCoordinator options)
    {
        _vm = viewModel;
        _tools = tools;
        _options = options;
    }

    /// <summary>这台机器上能不能做外层 rar。</summary>
    internal bool RarAvailable
    {
        get
        {
            try
            {
                return _tools.RarExists;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>内置 7-Zip 在不在（自动回退到 7z 外层时要用）。</summary>
    internal bool SevenZipAvailable
    {
        get
        {
            try
            {
                return _tools.SevenZipExists;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>外层容器**实际会用哪一个**（第 46 条：没装 WinRAR 就自动改用 7z）。</summary>
    internal PackingOuterContainerResolution EffectiveOuterContainer =>
        PackingOuterContainerResolver.Resolve(_vm.OuterContainer, RarAvailable, SevenZipAvailable);

    /// <summary>Rar.exe 的现状那句话（"用的哪一个、从哪来的" / "没找到，期望位置在哪"）。</summary>
    internal string RarStatusText
    {
        get
        {
            try
            {
                return _tools.DescribeRarResolution();
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    /// <summary>
    /// 重算摘要（扫一遍源的文件数与总大小）。
    /// ⚠ 大文件夹上这一次扫描是要花时间的，所以它绑在"选完源 / 点重算"上，**不是**每敲一个字符就跑。
    /// </summary>
    internal void Refresh()
    {
        PackingRequest request = _options.BuildRequest(new PackingRunOptions());

        if (!PackingPlan.TryCreate(request, out PackingPlan? plan, out string error) || plan == null)
        {
            _vm.SummaryText = string.IsNullOrWhiteSpace(_vm.SourceFolder) ? "先选一个文件夹（或一个文件）。" : error;
            _vm.PlacementText = string.Empty;
            _lastPlan = null;
            return;
        }

        _lastPlan = plan;

        PackingOuterContainerResolution outer = EffectiveOuterContainer;

        // ⛔ 要外层却连内置 7-Zip 都没有（程序装坏了那种）：**不许写成"不做外层、结果就是分卷"** ——
        //    服务那边会直接报"没法生成任何外层容器"，界面先说清楚，别让他确认一件做不成的事。
        if (outer.Impossible)
        {
            _vm.SummaryText =
                $"内容：{plan.FileCount} 个文件，{TaskSpaceEstimate.FormatSize(plan.ContentBytes)}"
                + Environment.NewLine
                + "外层容器：做不出来 —— 本机既没有 Rar.exe，也没有可用的 7-Zip。";
            _vm.PlacementText = $"请先修好 7-Zip（程序目录 tools\\7zip：{_tools.SevenZipExePath}），否则打不了包。";

            return;
        }

        string outerNote = outer.FellBackToSevenZip
            ? "（本机没有 Rar.exe，自动改用 7z —— 无需额外安装）"
            : string.Empty;

        _vm.SummaryText =
            $"内容：{plan.FileCount} 个文件，{TaskSpaceEstimate.FormatSize(plan.ContentBytes)}"
            + (plan.UnreadableCount > 0 ? $"（其中 {plan.UnreadableCount} 个条目读不到大小）" : string.Empty)
            + Environment.NewLine
            + $"内层 7z 分卷：{plan.DescribeVolumePlan()}"
            + Environment.NewLine
            + $"外层：1 个 {outer.Effective.ShortName()} 文件{outerNote}"
            + $"；需要空余空间：约 {TaskSpaceEstimate.FormatSize(plan.RequiredSpaceBytes)}";

        _vm.PlacementText = outer.Effective.HasOuterArtifact()
            ? $"最终产物：{OuterPathFor(plan, outer.Effective)}"
            : $"不做外层容器：结果就是 {plan.OutputFolder} 里的分卷";
    }

    /// <summary>
    /// 按**实际生效的容器**取最终产物的完整路径（`.rar` 还是 `.7z` 由它决定）。
    /// ⛔ 不许各处自己按 <c>plan.OuterContainer</c>（= 请求里那个）拼路径。
    /// </summary>
    internal static string OuterPathFor(PackingPlan plan, PackOuterContainer effectiveContainer) =>
        effectiveContainer switch
        {
            PackOuterContainer.SevenZip => plan.SevenZipOuterPath,
            PackOuterContainer.None => string.Empty,
            _ => plan.RarPath
        };

    /// <summary>每次开跑前把结果区清空。</summary>
    internal void ClearResult()
    {
        _vm.HasResult = false;
        _vm.ResultTitle = string.Empty;
        _vm.ResultText = string.Empty;
        _vm.ResultKind = string.Empty;
        _vm.ResultOuterPath = string.Empty;
        _vm.Volumes.Clear();
    }

    /// <summary>把结果区那一屏写出来（分卷行 + 结论 + 实际产物 + 其余物去向）。</summary>
    internal void ApplyResult(PackingResult result, PackingPlan plan)
    {
        _vm.Volumes.Clear();

        foreach (PackingVolume volume in result.Volumes)
        {
            _vm.Volumes.Add(new PackingVolumeRow
            {
                FileName = volume.FileName,
                SizeText = TaskSpaceEstimate.FormatSize(volume.Bytes)
            });
        }

        _vm.HasResult = true;
        _vm.ResultOuterPath = result.OuterPath ?? string.Empty;

        switch (result.State)
        {
            case PackingState.Succeeded:
                _vm.ResultKind = "Success";
                _vm.ResultTitle = result.OuterContainer.HasOuterArtifact()
                    ? StatusText.PackSuccess
                    : StatusText.PackPartialVolumesOnly;
                _vm.Info(result.Describe());
                break;

            case PackingState.Cancelled:
                _vm.ResultKind = "Warning";
                _vm.ResultTitle = StatusText.PackCancelled;
                _vm.Warn(result.Describe());
                break;

            default:
                _vm.ResultKind = "Error";
                _vm.ResultTitle = StatusText.PackFailed;
                _vm.Error(result.Describe());
                break;
        }

        /*
         * 结果区要把**实际产物**说全：外层是什么容器、落在哪个文件；不做外层时也要说清
         * "就是 B 里这几个分卷"，失败时更要说清"外层没生成"—— 而不是留一个空白的路径行让用户猜。
         */
        string outerLine;

        if (!string.IsNullOrWhiteSpace(result.OuterPath))
        {
            outerLine = $"外层产物（{result.OuterContainer.ShortName()}）：{result.OuterPath}";
        }
        else if (result.OuterContainer.HasOuterArtifact())
        {
            outerLine = $"外层产物：没有生成（这次选的容器是「{result.OuterContainer.ShortName()}」）。";
        }
        else
        {
            outerLine = "没有外层容器文件（选的是「不做外层容器」）。";
        }

        /*
         * ⛔ 结果区**不许撒谎**（第 46 条收尾自查抓到的第 6 处）：
         * 原来这里无条件写"分卷在：<文件夹>（N 个）"+「其余物按你选的档留着了，自己删掉就行」——
         * 而**默认档就是把那个文件夹彻底删掉**，于是他跑完看到的是一句"分卷在 X、你可以自己删 X"，
         * 那个 X 已经不存在了。现在按**收尾那句机器结论**分档说。
         */
        string volumesLine;

        if (result.CleanupNote.Contains("已彻底删除", StringComparison.Ordinal))
        {
            volumesLine = "其余物（分卷文件）：已按你选的档彻底删除（不再占空间）。";
        }
        else if (result.CleanupNote.Contains("已移入回收站", StringComparison.Ordinal))
        {
            volumesLine = "其余物（分卷文件）：已按你选的档移入回收站（可还原）。";
        }
        else if (result.Success && Directory.Exists(plan.OutputFolder))
        {
            volumesLine = $"其余物（分卷文件）：{plan.OutputFolder}（{result.Volumes.Count} 个分卷）";
        }
        else if (Directory.Exists(plan.OutputFolder))
        {
            volumesLine = $"未完成的分卷文件还在：{plan.OutputFolder}（{result.Volumes.Count} 个）"
                        + "—— 这次中途停下了，这些分卷可能不完整；重试前请先清空它。";
        }
        else
        {
            volumesLine = "没有生成任何分卷。";
        }

        // 「其余物留着」那句提示只在**真的留着**的时候才出现（失败 / 取消时收尾根本没跑）。
        bool restKept = result.Success && result.CleanupNote.Contains("保留", StringComparison.Ordinal);

        _vm.ResultText = result.Describe() + Environment.NewLine
            + outerLine + Environment.NewLine
            + volumesLine
            + (restKept ? Environment.NewLine + StatusText.PackKeepFolderHint : string.Empty);

        _vm.ProgressPercent = result.Success ? 100 : _vm.ProgressPercent;
        _vm.CurrentStepText = result.Success ? StatusText.PackSuccess : _vm.ResultTitle;
    }
}
