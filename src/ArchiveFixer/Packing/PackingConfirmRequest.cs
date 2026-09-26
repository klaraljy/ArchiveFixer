using System;
using System.Collections.Generic;
using System.IO;
using ArchiveFixer.Models;
using ArchiveFixer.Storage;

namespace ArchiveFixer.Packing
{
    /// <summary>
    /// 小确认弹窗要显示的东西（用户 2026-09-26 第 46 条：
    /// "用户点击打包操作会出现一个小弹窗，里面就显示着最终得到的压缩文件放在什么位置上，原包操作和删除操作怎样的选择"）。
    ///
    /// <para>纯数据 + 文案，**不引用 WPF** —— 弹窗只负责把它画出来，
    /// 于是"弹窗里到底写了什么"这件事可以被测试直接钉住（不必去驱动界面）。</para>
    /// </summary>
    public sealed class PackingConfirmRequest
    {
        /// <summary>源的名字（文件夹名 / 单文件基名）—— 所有命名的基准。</summary>
        public string SourceName { get; init; } = string.Empty;

        /// <summary>用户选中的那个路径（文件夹或文件）。</summary>
        public string SourcePath { get; init; } = string.Empty;

        /// <summary>
        /// 源解析结果（单文件时含"要建的那个同名文件夹"）。
        /// 有它才能按**当前**落点选择实时重算"最终产物在哪"（见 <see cref="BuildHeadLines"/>）。
        /// </summary>
        public PackingSourceResolution? Resolution { get; init; }

        /// <summary>源是文件夹还是单文件（单文件时会先建同名文件夹，那一层算其余物）。</summary>
        public PackingSourceKind SourceKind { get; init; } = PackingSourceKind.Folder;

        /// <summary>
        /// **实际生效的**外层容器（没装 WinRAR 时是 7z）—— 标题那行写的产物名必须跟它走，
        /// ⛔ 不许按"请求里那个"写（那会让弹窗写着 `.rar`、跑完得到 `.7z`）。
        /// </summary>
        public PackOuterContainer EffectiveContainer { get; init; } = PackOuterContainer.Rar;

        /// <summary>与 <see cref="EffectiveContainer"/> 同一个值（旧调用方按这个名字读）。</summary>
        public PackOuterContainer OuterContainer => EffectiveContainer;

        /// <summary>
        /// 按"打开弹窗时那一套选择"算出来的最终产物路径（打开弹窗那一刻的快照）。
        /// <see cref="ArtifactPathFor"/> 在拿不到源解析结果时回落到它。
        /// </summary>
        public string OuterPath { get; init; } = string.Empty;

        /// <summary>内容摘要（几个文件、多大）。</summary>
        public string ContentSummary { get; init; } = string.Empty;

        /// <summary>分卷那一句话（"按 2 卷切……"）。</summary>
        public string VolumeRuleText { get; init; } = string.Empty;

        /// <summary>需要多少空余空间（一句话）。</summary>
        public string SpaceText { get; init; } = string.Empty;

        /// <summary>打开弹窗时的初值（来自设置 —— 上次选的那一套）。</summary>
        public PackingRunOptions Initial { get; init; } = new();

        /// <summary>取默认值构造（源 + 规划已经算好时）。</summary>
        public static PackingConfirmRequest FromPlan(
            PackingPlan plan,
            PackingRunOptions initial,
            PackOuterContainer effectiveContainer = PackOuterContainer.Rar)
        {
            if (plan == null)
            {
                return new PackingConfirmRequest();
            }

            return new PackingConfirmRequest
            {
                SourceName = plan.SourceFolderName,
                SourcePath = plan.SourceResolution?.SourcePath ?? plan.SourceFolder,
                Resolution = plan.SourceResolution,
                SourceKind = plan.SourceResolution?.Kind ?? PackingSourceKind.Folder,
                OuterPath = effectiveContainer switch
                {
                    PackOuterContainer.SevenZip => plan.SevenZipOuterPath,
                    PackOuterContainer.None => string.Empty,
                    _ => plan.RarPath
                },
                EffectiveContainer = effectiveContainer,
                ContentSummary = $"内容：{plan.FileCount} 个文件，{TaskSpaceEstimate.FormatSize(plan.ContentBytes)}"
                                 + (plan.UnreadableCount > 0 ? $"（其中 {plan.UnreadableCount} 个文件读不到大小）" : string.Empty),
                VolumeRuleText = plan.DescribeVolumePlan(),
                SpaceText = $"需要空余空间约 {TaskSpaceEstimate.FormatSize(plan.RequiredSpaceBytes)}",
                Initial = initial ?? new PackingRunOptions()
            };
        }

        /// <summary>
        /// 按**当前**那一套选择算"最终产物在哪"（落点 + 实际容器 + 源名，重名自动让位）。
        ///
        /// <para>⛔ 复用规划层那两处口径（`PackingPaths.ResolveTargetDirectory` 与
        /// `PackingNaming.ResolveUniqueFilePath`），⛔ 不在这里自己拼一遍路径 ——
        /// 否则弹窗说的名字与真正产出的名字会不一致。</para>
        /// </summary>
        public string ArtifactPathFor(PackingRunOptions options)
        {
            if (Resolution == null)
            {
                return OuterPath;
            }

            string directory = PackingPaths.ResolveTargetDirectory(Resolution, options ?? new PackingRunOptions());

            if (string.IsNullOrWhiteSpace(directory))
            {
                return OuterPath;
            }

            string extension = EffectiveContainer == PackOuterContainer.SevenZip ? ".7z" : ".rar";

            return PackingNaming.ResolveUniqueFilePath(directory, SourceName, extension);
        }

        /// <summary>
        /// 弹窗顶部那几行（源 → 产物）。
        /// <paramref name="options"/> 传当前选择时会**按它重算**最终产物路径（默认用打开弹窗时那一套）。
        /// </summary>
        public IReadOnlyList<string> BuildHeadLines(PackingRunOptions? options = null)
        {
            var lines = new List<string>
            {
                SourceKind == PackingSourceKind.File
                    ? $"源文件：{SourcePath}（会先在它旁边建同名文件夹「{SourceName}」，再把文件放进去）"
                    : $"源文件夹：{SourcePath}"
            };

            if (!string.IsNullOrWhiteSpace(ContentSummary))
            {
                lines.Add(ContentSummary);
            }

            if (!string.IsNullOrWhiteSpace(VolumeRuleText))
            {
                lines.Add(VolumeRuleText);
            }

            if (!string.IsNullOrWhiteSpace(SpaceText))
            {
                lines.Add(SpaceText);
            }

            string artifact = ArtifactPathFor(options ?? Initial);

            lines.Add(
                EffectiveContainer == PackOuterContainer.SevenZip
                    ? $"最终产物：{artifact}（本机没有 Rar.exe，外层自动用 7z —— 无需额外安装）"
                    : EffectiveContainer == PackOuterContainer.None
                        ? "最终产物：不做外层容器（结果就是那个装分卷的文件夹里的分卷）"
                        : $"最终产物：{artifact}");

            return lines;
        }

        /// <summary>点在「本地」时的落点文本（源旁边）。</summary>
        public string LocalTargetText
        {
            get
            {
                try
                {
                    string? parent = Path.GetDirectoryName(
                        SourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                    return string.IsNullOrWhiteSpace(parent) ? "（源所在目录）" : parent!;
                }
                catch
                {
                    return "（源所在目录）";
                }
            }
        }

        /// <summary>
        /// 其余物到底是什么（弹窗里给用户看的一句）。
        ///
        /// <para>⚠ 2026-09-26 追加二之后**分卷不再套文件夹**：分卷直接落在落点目录里，
        /// 所以"其余物"就是**那几个分卷文件本身**（<c>PackingCleanup.EnumerateVolumeFiles</c> 逐个点名），
        /// 单文件时再加上为它临时建的同名文件夹 —— 这句话以前写的是"装 7z 分卷的那个文件夹"，
        /// 会让用户以为"整个落点目录都会被删掉"（事实核查：真机上他问过同类问题）。</para>
        /// </summary>
        public string RestExplanation => SourceKind == PackingSourceKind.File
            ? $"其余物 = 那几个 7z 分卷文件 + 为这个文件临时建的同名文件夹「{SourceName}」"
            : "其余物 = 那几个 7z 分卷文件（就在落点目录里，只清这几个文件，落点里别的东西一个都不动）";

        /// <summary>
        /// 落点那句话（弹窗里**只显示**，选择在⑤页 —— 用户 2026-09-26 追加："选择还是得放在页面"）。
        /// 形如 <c>默认（跟①页「输出位置」）→ D:\出包</c> / <c>本地（源旁边）→ C:\素材</c>。
        /// </summary>
        public string PlacementText
        {
            get
            {
                PackingRunOptions options = Initial ?? new PackingRunOptions();
                string directory = TargetDirectory;

                return $"{options.TargetModeText} → "
                     + (string.IsNullOrWhiteSpace(directory) ? "（推不出目录）" : directory);
            }
        }

        /// <summary>最终产物会落在哪个目录（按打开弹窗时那一套落点算好的）。</summary>
        public string TargetDirectory
        {
            get
            {
                if (Resolution == null)
                {
                    try
                    {
                        return string.IsNullOrWhiteSpace(OuterPath) ? string.Empty : Path.GetDirectoryName(OuterPath) ?? string.Empty;
                    }
                    catch
                    {
                        return string.Empty;
                    }
                }

                return PackingPaths.ResolveTargetDirectory(Resolution, Initial ?? new PackingRunOptions());
            }
        }

        /// <summary>原包到底是什么。</summary>
        public string SourceExplanation => SourceKind == PackingSourceKind.File
            ? $"原包 = 你选的那个文件（{Path.GetFileName(SourcePath)}）"
            : $"原包 = 你选的那个文件夹（{SourceName}）";
    }
}
