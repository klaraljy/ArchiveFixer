using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 「一组分卷的**第一卷**名字被改坏」时的修复（用户 2026-09-25 第 37 条）。
    ///
    /// <para><b>为什么要修而不是只报错</b>：他那个 12.22 GiB 的容器里是一组三卷 7z，第一卷叫
    /// <c>Code Complete-BZ.7z(删掉.001</c>（打包者塞了字、还把右括号吃掉了），后两卷名字正常。
    /// 7-Zip 顺着名字找不到后续卷，就只能把它当"通用分片"（解出来是一个等大的垃圾文件），
    /// 于是那 12 GiB 的内容**永远出不来**。他要的是"素材解出来"，不是"给我一句解释"。</para>
    ///
    /// <para><b>改谁的名字</b>：⛔ **绝不改用户给的源文件**（不变量 1）。只有这一卷是**我们自己产出的**
    /// （续解任务：它本来就是上一层解出来的过程物，现在躺在 `其余物` 里）时才改名 ——
    /// 那种情况下的"改名"是把我们自己的产物摆正，日志里一条 `分卷改名` 写清 from → to。
    /// 用户自己添加的那种坏名字分卷**一个字节都不动**，仍然如实报「分卷缺失」+ 给出改名建议。</para>
    ///
    /// <para><b>为什么只改名、不复制</b>：改名是同一卷上的重命名（瞬间完成、不占额外空间、
    /// 也不产生需要在收尾时清理的中间产物）；而"复制一份标准命名的副本"要动辄几十 GB 的拷贝，
    /// 在工作区里还会被当成产物卷进校验与定稿。</para>
    /// </summary>
    public static class BrokenVolumeChainRepair
    {
        /// <summary>一次改名计划：整组从"名字对不上"变成"标准分卷命名"。</summary>
        public sealed class RenamePlan
        {
            /// <summary>标准基名（例如 <c>Code Complete-BZ.7z</c>）。</summary>
            public string StandardBaseName { get; init; } = string.Empty;

            /// <summary>（现路径 → 目标文件名）按卷号从 1 开始排好；目标名与现名相同的项不会出现在这里。</summary>
            public IReadOnlyList<(string SourcePath, string TargetName)> Renames { get; init; }
                = Array.Empty<(string, string)>();

            /// <summary>这一组第一卷**改完之后**的完整路径（引擎要从它开始读）。</summary>
            public string FirstVolumePathAfterRename { get; init; } = string.Empty;

            /// <summary>给日志的一句话（<c>Code Complete-BZ.7z(删掉.001 → Code Complete-BZ.7z.001</c>）。</summary>
            public string Describe() =>
                string.Join(
                    "；",
                    Renames.Select(r => $"{Path.GetFileName(r.SourcePath)} → {r.TargetName}"));
        }

        /// <summary>
        /// 试着给"名字被改坏的第一卷"做一个改名计划。做不出来返回 null（调用方照旧报分卷缺失）。
        ///
        /// <para>判据（全部要成立）：</para>
        /// <list type="number">
        /// <item><description>手里这个文件的名字里带**卷号 1**（<c>.001</c> / <c>.part1.rar</c> / <c>.z01</c>…）；</description></item>
        /// <item><description>同目录里存在"像这一组后续卷"的文件，且卷号从 **2 开始连续**（2、3、4…不许跳号）；</description></item>
        /// <item><description>标准基名推得出来（用既有实现 <c>VolumeGroupDetector.TryGetFirstVolumeName</c>）。</description></item>
        /// </list>
        ///
        /// <para>⚠ 名字只是**线索**：改完之后管线会拿引擎**列一次目录**来验证 ——
        /// 真归档的清单才作数；要是引擎还说它是"通用分片"，那一步会照旧判「分卷缺失」（不会假装成功）。</para>
        /// </summary>
        public static RenamePlan? TryPlan(string? archivePath, IEnumerable<string?>? siblingFileNamesInDirectory)
        {
            if (string.IsNullOrWhiteSpace(archivePath) || siblingFileNamesInDirectory == null)
            {
                return null;
            }

            string fileName = Path.GetFileName(archivePath);
            int? selfIndex = Detection.VolumeGroupDetector.TryGetVolumeIndex(fileName);

            // 只有"第一卷"才有修复的余地（`.002` 单独跑没有意义）。
            if (selfIndex != 1)
            {
                return null;
            }

            var siblings = new List<(int Index, string Name)>();

            foreach (string? name in siblingFileNamesInDirectory)
            {
                if (string.IsNullOrWhiteSpace(name) ||
                    string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int? index = Detection.VolumeGroupDetector.TryGetVolumeIndex(name);

                if (index is null or < 2)
                {
                    continue;
                }

                siblings.Add((index.Value, name!));
            }

            if (siblings.Count == 0)
            {
                return null;
            }

            // 卷号必须从 2 开始连续：跳号说明这一组本来就不全，改了也解不开（不变量 7）。
            var ordered = siblings.OrderBy(x => x.Index).ToList();

            for (int i = 0; i < ordered.Count; i++)
            {
                if (ordered[i].Index != i + 2)
                {
                    return null;
                }
            }

            /*
             * 后续卷的名字是"正常"的那一半 —— 用它们推标准基名（既有实现：`X.7z.002` → `X.7z.001`）。
             * 推不出来就说明这一组本来就不标准，别乱改（宁可照实报错）。
             */
            string? standardFirst = Detection.VolumeGroupDetector.TryGetFirstVolumeName(ordered[0].Name);

            if (string.IsNullOrWhiteSpace(standardFirst))
            {
                return null;
            }

            /*
             * 族必须先认出来，再按**那一族的标准名**推后续卷。
             * ⛔ 旧代码一律"掐掉最后一个点段 + 接 .NNN"：对 RAR 的 `X.part1.rar` 会掐出基名 `X.part1`，
             * 后续卷就被改成 `X.part1.002` —— 真机 2026-09-30 正是这样把用户的名字改成七零八落的。
             * 认不出族 ⇒ 返回 null：**判不出就不改**（宁可照实报错，也不给一个错的名字）。
             */
            (string Stem, Detection.VolumeNamingFamily Family)? family = ParseStandardFirst(standardFirst!);

            if (family is null)
            {
                return null;
            }

            int lastIndex = ordered[^1].Index;
            IReadOnlyList<string> standardNames = Detection.VolumeNumberFromContent.BuildStandardNames(
                family.Value.Stem,
                family.Value.Family,
                lastIndex);

            if (standardNames.Count < lastIndex)
            {
                return null;
            }

            string directory = Path.GetDirectoryName(archivePath) ?? string.Empty;
            string firstVolumeAfterRename = Path.Combine(directory, standardFirst!);

            var renames = new List<(string, string)>();
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddRename(string sourcePath, string targetName)
            {
                // 目标名与现名一样就不用动（后续卷通常已经是标准名）。
                if (!string.Equals(Path.GetFileName(sourcePath), targetName, StringComparison.OrdinalIgnoreCase))
                {
                    renames.Add((sourcePath, targetName));
                }

                targets.Add(Path.Combine(directory, targetName));
            }

            AddRename(archivePath, standardFirst);

            foreach ((int index, string name) in ordered)
            {
                AddRename(Path.Combine(directory, name), standardNames[index - 1]);
            }

            /*
             * 目标名不能已经被别的文件占着（否则改名会覆盖别人的东西 —— 一次都不许发生）。
             * "占着"的判定：目标路径存在、且它不是这次要改名的某个源文件本身。
             */
            var sourcePaths = new HashSet<string>(
                new[] { archivePath }.Concat(ordered.Select(x => Path.Combine(directory, x.Name))),
                StringComparer.OrdinalIgnoreCase);

            foreach (string target in targets)
            {
                if (File.Exists(target) && !sourcePaths.Contains(target))
                {
                    return null;
                }
            }

            return new RenamePlan
            {
                StandardBaseName = family.Value.Stem,
                Renames = renames,
                FirstVolumePathAfterRename = firstVolumeAfterRename
            };
        }

        /// <summary>
        /// 这个计划是不是"什么都不用改"（名字本来就标准）。
        ///
        /// <para>⚠ 调用方必须先看这一位：一组名字正常的普通分卷（<c>X.7z.001/.002</c>）同样会被
        /// <see cref="TryPlan"/> 算出"零条改名"的计划 —— 那种情况**既不用改也不用记日志**，
        /// 否则每一组正常分卷的续解都会多出一句"分卷名字被改坏"的假话。</para>
        /// </summary>
        public static bool IsNoOp(RenamePlan? plan) => plan == null || plan.Renames.Count == 0;

        /// <summary>
        /// 执行改名计划。**全部成功才算成功**；中途失败会把已经改过的**原样改回去**（尽力而为），
        /// 并把失败原因写在 <paramref name="failure"/> 里（调用方照实写日志，不改任务结论）。
        ///
        /// <para>改名顺序刻意用"两阶段"：先把所有要动的文件改成临时名，再改成目标名 ——
        /// 这样 `A.001 → B.001` 与 `B.001 → …` 这种互换也不会互相踩（与不变量 3 的名称交换同一口径）。</para>
        /// </summary>
        public static bool TryApply(RenamePlan? plan, out string failure)
        {
            failure = string.Empty;

            if (plan == null || plan.Renames.Count == 0)
            {
                return plan != null;
            }

            var staged = new List<(string Temporary, string Target)>();

            try
            {
                foreach ((string sourcePath, string targetName) in plan.Renames)
                {
                    if (!File.Exists(sourcePath))
                    {
                        failure = $"要改名的分卷不在了：{sourcePath}";
                        Rollback(staged);
                        return false;
                    }

                    string directory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
                    string temporary = Path.Combine(
                        directory,
                        $"~afxvol~{Guid.NewGuid():N}~{Path.GetFileName(sourcePath)}");

                    File.Move(sourcePath, temporary);
                    staged.Add((temporary, Path.Combine(directory, targetName)));
                }

                foreach ((string temporary, string target) in staged)
                {
                    File.Move(temporary, target);
                }

                return true;
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                Rollback(staged);
                return false;
            }
        }

        /// <summary>
        /// 从"标准的第一卷名"反推这一组的**族与基名**（后续卷的名字由
        /// <see cref="Detection.VolumeNumberFromContent.BuildStandardNames"/> 按族生成，⛔ 不在别处再拼一遍）。
        /// <para>认不出的族一律返回 <c>null</c>：<b>判不出就不改</b>。
        /// 已知两种标准首卷名：RAR 的 <c>X.part1.rar</c>、7z 的 <c>X.7z.001</c>。</para>
        /// </summary>
        private static (string Stem, Detection.VolumeNamingFamily Family)? ParseStandardFirst(string standardFirst)
        {
            string name = Path.GetFileName(standardFirst);

            if (name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
            {
                int partIndex = name.LastIndexOf(".part", StringComparison.OrdinalIgnoreCase);

                if (partIndex > 0)
                {
                    string number = name[(partIndex + ".part".Length)..^".rar".Length];

                    if (number.Length > 0 && number.All(char.IsAsciiDigit) && number.TrimStart('0') == "1")
                    {
                        return (name[..partIndex], Detection.VolumeNamingFamily.RarPart);
                    }
                }

                return null;
            }

            if (name.EndsWith(".001", StringComparison.Ordinal))
            {
                /*
                 * 数字族的 stem 由 BuildStandardNames 自己补 `.7z.` —— 这里必须把 `.7z` 也剥掉，
                 * 否则会拼出 `X.7z.7z.002`（实测踩到过）。
                 */
                string stem = name[..^4];

                if (stem.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
                {
                    stem = stem[..^3];
                }

                return (stem, Detection.VolumeNamingFamily.SevenZipNumbered);
            }

            return null;
        }

        private static void Rollback(List<(string Temporary, string Target)> staged)
        {
            // 尽量把还在临时名上的文件改回它原来的名字（改不回去只写日志，绝不抛）。
            foreach ((string temporary, string target) in staged)
            {
                try
                {
                    if (File.Exists(temporary) && !File.Exists(target))
                    {
                        File.Move(temporary, target);
                    }
                }
                catch
                {
                    // 回滚失败：上面那一步的日志已经把路径写清楚了。
                }
            }
        }
    }
}
