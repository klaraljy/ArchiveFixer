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
            string createdRestFolder = string.Empty;

            // ───────── ① 原包：不动 / 移入其余物 ─────────
            if (options.SourceHandling == PackingSourceHandling.MoveToRest)
            {
                (sourceMoved, moveTarget, createdRestFolder, string moveLine, string? moveProblem) =
                    MoveSourceIntoRest(plan);

                lines.Add(moveLine);

                if (moveProblem != null)
                {
                    lines.Add("WARN " + moveProblem);
                }
            }

            // ───────── ② 其余物：不动 / 回收站 / 彻底删除 ─────────
            List<string> restPaths = ResolveRestPaths(plan, createdRestFolder);
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

        /// <summary>
        /// 把原包搬进"其余物"（⛔ 只搬用户给的那一个；撞名自动让位，不覆盖）。
        ///
        /// <para>返回值里多一个 <c>RestFolder</c>：那是**这一档才建**的那个以源名命名的文件夹
        /// （其余物清单要把整份带上，否则"其余物 = 彻底删除"删不掉原包）。</para>
        /// </summary>
        private static (bool Moved, string Target, string RestFolder, string Line, string? Problem) MoveSourceIntoRest(
            PackingPlan plan)
        {
            string source = plan.SourceResolution?.SourcePath ?? plan.SourceFolder;

            if (string.IsNullOrWhiteSpace(source) || !(File.Exists(source) || Directory.Exists(source)))
            {
                return (false, string.Empty, string.Empty, "原包操作为「移入其余物」，但源已经不在了 —— 这一步跳过。", null);
            }

            string name = plan.SourceResolution?.SourceName is { Length: > 0 } sourceName
                ? sourceName
                : Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            /*
             * ⚠ 2026-09-26 追加改口径：分卷不再装在中间文件夹里，所以"移入其余物"**没有现成的文件夹可搬**。
             * 这一档 now 由我们来建一个以源名命名的文件夹（只在**用户显式选了这一档**时才建），
             * 把源搬进去 —— 于是"其余物 = 那个文件夹 + 那些分卷文件"，后半段
             * （其余物 = 彻底删除）仍然能一次把原包与分卷一起处理掉。
             * ⛔ 默认档（原包不动）**一个文件夹都不建** —— 那正是他嫌多余的那一层。
             */
            string target = PackingNaming.ResolveUniqueFolderPath(plan.OutputFolder, name);

            try
            {
                if (string.IsNullOrWhiteSpace(target))
                {
                    return (false, string.Empty, string.Empty, "原包移入其余物：推不出目标文件夹，这一步跳过。", null);
                }

                Directory.CreateDirectory(plan.OutputFolder);
                Directory.CreateDirectory(target);

                if (Directory.Exists(source))
                {
                    Directory.Move(source, Path.Combine(target, name));
                }
                else
                {
                    File.Move(source, Path.Combine(target, name));
                }

                string moved = Path.Combine(target, name);

                return (true, moved, target, $"原包已移入其余物：{source} → {moved}", null);
            }
            catch (Exception ex)
            {
                return (false, target, string.Empty, "原包没能移入其余物（它还在原地）。", $"{source} → {target}：{ex.Message}");
            }
        }

        /// <summary>
        /// 其余物清单：**切出来的那些 7z 分卷文件** +（单文件时）那个临时建的同名文件夹。
        ///
        /// <para>⚠ 2026-09-26 追加改口径：以前分卷装在一个以源名命名的中间文件夹里，其余物就是那个文件夹；
        /// 现在分卷**直接落在落点目录里**（用户原话："外面不用再套一件文件夹了"），
        /// 所以其余物 = 那些分卷文件本身。</para>
        /// </summary>
        private static List<string> ResolveRestPaths(PackingPlan plan, string createdRestFolder = "")
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

            foreach (string volume in EnumerateVolumeFiles(plan))
            {
                Add(volume);
            }

            // 「原包移入其余物」这一档才会有的那个文件夹（装了原包）。
            if (!string.IsNullOrWhiteSpace(createdRestFolder))
            {
                Add(createdRestFolder);
            }

            string wrapper = plan.SourceResolution?.WrapperFolderToCreate ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(wrapper))
            {
                Add(wrapper);
            }

            return paths;
        }

        /// <summary>落点目录里属于这一次的那些分卷文件（按基名 + 纯数字后缀认，与数分卷同一口径）。</summary>
        private static List<string> EnumerateVolumeFiles(PackingPlan plan)
        {
            var files = new List<string>();

            try
            {
                if (string.IsNullOrWhiteSpace(plan.OutputFolder) ||
                    string.IsNullOrWhiteSpace(plan.VolumeBaseName) ||
                    !Directory.Exists(plan.OutputFolder))
                {
                    return files;
                }

                foreach (string file in Directory.EnumerateFiles(plan.OutputFolder))
                {
                    string fileName = Path.GetFileName(file);

                    if (!fileName.StartsWith(plan.VolumeBaseName + ".", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string suffix = fileName[(plan.VolumeBaseName.Length + 1)..];

                    if (suffix.Length > 0 && suffix.All(char.IsDigit))
                    {
                        files.Add(file);
                    }
                }
            }
            catch
            {
                // 数不出来就当没有：收尾这一步的出问题只写日志，不改结论（红线 ④）。
            }

            return files;
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
