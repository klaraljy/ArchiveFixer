using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Storage;

namespace ArchiveFixer.Packing
{
    /// <summary>打包收尾（原包 / 其余物）的结果 —— 只用来记账与写日志，**不参与成败判定**。</summary>
    public sealed class PackingCleanupOutcome
    {
        /// <summary>这次收尾有没有真的做事（没成功 / 不做外层时为 false）。</summary>
        public bool Ran { get; init; }

        /// <summary>原包有没有被搬进其余物。</summary>
        public bool SourceMoved { get; init; }

        /// <summary>原包搬进去之后的位置。</summary>
        public string SourceMoveTarget { get; init; } = string.Empty;

        /// <summary>其余物处理的结果（"保留" / "已移入回收站" / "已彻底删除" / "没做成：…"）。</summary>
        public string RestSummary { get; init; } = string.Empty;

        /// <summary>释放的字节数（保留那一档是 0）。</summary>
        public long FreedBytes { get; init; }

        /// <summary>要写进日志的行（调用方原样写出去；口径只有这一处）。</summary>
        public IReadOnlyList<string> LogLines { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// 打包成功之后的**收尾**：原包操作 + 其余物操作（用户 2026-09-26 第 46 条）。
    ///
    /// <para>两档的口径（他确认过的）：</para>
    /// <list type="bullet">
    /// <item><description><b>原包</b>：不动（默认）/ 移入其余物（搬进装 7z 分卷的那个文件夹里）；</description></item>
    /// <item><description><b>其余物</b>（= 装 7z 分卷的文件夹，单文件时还有那个临时建的同名文件夹）：
    /// 不动 / 移入回收站 / **彻底删除（默认）**。</description></item>
    /// </list>
    ///
    /// <para>⛔ 四条红线（每条都有真实会出事的情形）：</para>
    /// <list type="number">
    /// <item><description><b>没有成功就一个字节都不动</b>（失败 / 取消 / 校验没过 → 什么都不做）；</description></item>
    /// <item><description><b>不做外层容器时，分卷就是结果</b> —— 其余物那一档一律不执行（否则会把结果删掉）；</description></item>
    /// <item><description><b>只删落点目录里、我们自己造的那几个目录</b>（容器内校验；越界只 WARN）；</description></item>
    /// <item><description><b>删失败不改结论</b>：外层容器已经生成且校验通过，收尾出问题只写日志。</description></item>
    /// </list>
    /// </summary>
    public static class PackingCleanup
    {
        /// <summary>
        /// 执行收尾。返回的 <see cref="PackingCleanupOutcome.LogLines"/> 由调用方写进日志。
        /// </summary>
        /// <param name="plan">本次规划（落点、源名、其余物路径都从它取）。</param>
        /// <param name="succeeded">打包是不是真的成了（⛔ 只有 true 才动任何东西）。</param>
        /// <param name="outerArtifactPath">外层容器产物（不做外层时传空串）。</param>
        /// <param name="delete">删除实现（默认走 <see cref="RecycleBinService"/>；测试可注入）。</param>
        public static PackingCleanupOutcome Run(
            PackingPlan? plan,
            bool succeeded,
            string? outerArtifactPath,
            Func<DeleteRequest, DeleteOptions, DeleteResult>? delete = null)
        {
            if (plan == null)
            {
                return new PackingCleanupOutcome();
            }

            var lines = new List<string>();

            // 红线 ①：没有成功就一个字节都不动。
            if (!succeeded)
            {
                lines.Add("没有成功：原包与其余物一个字节都不动（失败 / 取消不留任何改动）。");

                return new PackingCleanupOutcome { Ran = false, LogLines = lines };
            }

            // 红线 ②：不做外层容器时，分卷本身就是结果 —— 其余物那一档不许执行。
            if (!plan.OuterContainer.HasOuterArtifact())
            {
                lines.Add("本次不做外层容器：分卷就是结果，其余物那一档不执行（原包也照你选的档处理）。");
            }

            PackingRunOptions options = plan.RunOptions ?? new PackingRunOptions();

            bool sourceMoved = false;
            string moveTarget = string.Empty;

            // ───────── ① 原包：不动 / 移入其余物 ─────────
            if (options.SourceHandling == PackingSourceHandling.MoveToRest)
            {
                (sourceMoved, moveTarget, string moveLine, string? moveProblem) = MoveSourceIntoRest(plan);

                lines.Add(moveLine);

                if (moveProblem != null)
                {
                    lines.Add("WARN " + moveProblem);
                }
            }

            // ───────── ② 其余物：不动 / 回收站 / 彻底删除 ─────────
            List<string> restPaths = ResolveRestPaths(plan);
            string restSummary;
            long freed = 0;

            if (options.RestHandling == PackingRestHandling.Keep || !plan.OuterContainer.HasOuterArtifact())
            {
                restSummary = restPaths.Count == 0 ? "没有其余物" : "保留";

                foreach (string path in restPaths)
                {
                    lines.Add($"其余物保留：{Describe(path)}");
                }
            }
            else
            {
                DeleteMode mode = options.RestHandling == PackingRestHandling.Delete
                    ? DeleteMode.Permanent
                    : DeleteMode.RecycleBin;

                var failures = new List<string>();

                foreach (string path in restPaths)
                {
                    // 红线 ③：只动落点目录里、我们自己造的那几个目录。
                    if (!PackingPaths.IsInside(plan.TargetDirectory, path) ||
                        string.Equals(
                            Path.GetFullPath(path),
                            Path.GetFullPath(plan.TargetDirectory),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        failures.Add($"{path}（不在落点目录里，跳过）");
                        continue;
                    }

                    long bytes = MeasureBytes(path);

                    DeleteResult result = (delete ?? DefaultDelete)(
                        new DeleteRequest(path, "打包成功后按「其余物操作」处理"),
                        new DeleteOptions
                        {
                            AllowedRoot = plan.TargetDirectory,
                            UserConfirmed = true,
                            Mode = mode,
                            Reason = "打包完成后处理其余物（用户在小确认弹窗里选的档）"
                        });

                    bool ok = result?.Outcomes != null
                              && result.Outcomes.Count > 0
                              && result.Outcomes.All(outcome => outcome.Success);

                    if (!ok)
                    {
                        string why = result?.Outcomes?.FirstOrDefault()?.Message ?? "删除没有成功";

                        failures.Add($"{path}（{why}）");
                        continue;
                    }

                    freed += bytes;

                    lines.Add(
                        $"{(mode == DeleteMode.Permanent ? "彻底删除" : "移入回收站")}：{Describe(path)}"
                        + $"（{TaskSpaceEstimate.FormatSize(bytes)}）");
                }

                restSummary = failures.Count == 0
                    ? (mode == DeleteMode.Permanent ? "已彻底删除" : "已移入回收站")
                    : "部分没做成：" + string.Join("；", failures.Take(3));
            }

            lines.Add(
                $"收尾：原包{(sourceMoved ? "已移入其余物（" + moveTarget + "）" : "不动")}；"
                + $"其余物{restSummary}" + (freed > 0 ? $"（释放 {TaskSpaceEstimate.FormatSize(freed)}）" : string.Empty));

            return new PackingCleanupOutcome
            {
                Ran = true,
                SourceMoved = sourceMoved,
                SourceMoveTarget = moveTarget,
                RestSummary = restSummary,
                FreedBytes = freed,
                LogLines = lines
            };
        }

        /// <summary>把原包搬进装分卷的文件夹（⛔ 只搬用户给的那一个；撞名就如实报，不覆盖）。</summary>
        private static (bool Moved, string Target, string Line, string? Problem) MoveSourceIntoRest(PackingPlan plan)
        {
            string source = plan.SourceResolution?.SourcePath ?? plan.SourceFolder;

            if (string.IsNullOrWhiteSpace(source) || !(File.Exists(source) || Directory.Exists(source)))
            {
                return (false, string.Empty, "原包操作为「移入其余物」，但源已经不在了 —— 这一步跳过。", null);
            }

            string name = plan.SourceResolution?.SourceName is { Length: > 0 } sourceName
                ? sourceName
                : Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            string target = Path.Combine(plan.OutputFolder, name);

            try
            {
                if (File.Exists(target) || Directory.Exists(target))
                {
                    return (false, target, $"原包移入其余物：目标已经存在（{target}），这一步跳过（不覆盖）。", null);
                }

                if (Directory.Exists(source))
                {
                    Directory.CreateDirectory(plan.OutputFolder);
                    Directory.Move(source, target);
                }
                else
                {
                    Directory.CreateDirectory(plan.OutputFolder);
                    File.Move(source, target);
                }

                return (true, target, $"原包已移入其余物：{source} → {target}", null);
            }
            catch (Exception ex)
            {
                return (false, target, "原包没能移入其余物（它还在原地）。", $"{source} → {target}：{ex.Message}");
            }
        }

        /// <summary>其余物清单：装分卷的文件夹 + （单文件时）那个临时建的同名文件夹。</summary>
        private static List<string> ResolveRestPaths(PackingPlan plan)
        {
            var paths = new List<string>();

            void Add(string? path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                foreach (string existing in paths)
                {
                    if (string.Equals(
                            Path.GetFullPath(existing),
                            Path.GetFullPath(path),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }

                paths.Add(path);
            }

            Add(plan.OutputFolder);

            string wrapper = plan.SourceResolution?.WrapperFolderToCreate ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(wrapper))
            {
                Add(wrapper);
            }

            return paths;
        }

        private static DeleteResult DefaultDelete(DeleteRequest request, DeleteOptions options)
        {
            var service = new RecycleBinService();

            return service.Delete(request, options);
        }

        private static long MeasureBytes(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    return new FileInfo(path).Length;
                }

                if (!Directory.Exists(path))
                {
                    return 0;
                }

                return Directory
                    .EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Sum(file =>
                    {
                        try
                        {
                            return new FileInfo(file).Length;
                        }
                        catch
                        {
                            return 0L;
                        }
                    });
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>日志里的短地址：只留落点目录那一层往下的部分。</summary>
        private static string Describe(string path)
        {
            try
            {
                return Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            catch
            {
                return path;
            }
        }
    }
}
