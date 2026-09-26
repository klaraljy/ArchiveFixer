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

        /// <summary>源是文件夹还是单文件（单文件时会先建同名文件夹，那一层算其余物）。</summary>
        public PackingSourceKind SourceKind { get; init; } = PackingSourceKind.Folder;

        /// <summary>最终产物（<c>.rar</c> 或没装 WinRAR 时的 <c>.7z</c>）的完整路径。</summary>
        public string OuterPath { get; init; } = string.Empty;

        /// <summary>外层容器（实际生效的那个：rar / 7z）。</summary>
        public PackOuterContainer OuterContainer { get; init; } = PackOuterContainer.Rar;

        /// <summary>内容摘要（几个文件、多大）。</summary>
        public string ContentSummary { get; init; } = string.Empty;

        /// <summary>分卷那一句话（"按 2 卷切……"）。</summary>
        public string VolumeRuleText { get; init; } = string.Empty;

        /// <summary>需要多少空余空间（一句话）。</summary>
        public string SpaceText { get; init; } = string.Empty;

        /// <summary>打开弹窗时的初值（来自设置 —— 上次选的那一套）。</summary>
        public PackingRunOptions Initial { get; init; } = new();

        /// <summary>取默认值构造（源 + 规划已经算好时）。</summary>
        public static PackingConfirmRequest FromPlan(PackingPlan plan, PackingRunOptions initial)
        {
            if (plan == null)
            {
                return new PackingConfirmRequest();
            }

            string outerPath = plan.OuterContainer switch
            {
                PackOuterContainer.SevenZip => plan.SevenZipOuterPath,
                PackOuterContainer.None => string.Empty,
                _ => plan.RarPath
            };

            return new PackingConfirmRequest
            {
                SourceName = plan.SourceFolderName,
                SourcePath = plan.SourceResolution?.SourcePath ?? plan.SourceFolder,
                SourceKind = plan.SourceResolution?.Kind ?? PackingSourceKind.Folder,
                OuterPath = outerPath,
                OuterContainer = plan.OuterContainer,
                ContentSummary = $"内容：{plan.FileCount} 个文件，{TaskSpaceEstimate.FormatSize(plan.ContentBytes)}"
                                 + (plan.UnreadableCount > 0 ? $"（其中 {plan.UnreadableCount} 个读不到大小）" : string.Empty),
                VolumeRuleText = plan.DescribeVolumePlan(),
                SpaceText = $"需要空余空间约 {TaskSpaceEstimate.FormatSize(plan.RequiredSpaceBytes)}",
                Initial = initial ?? new PackingRunOptions()
            };
        }

        /// <summary>弹窗顶部那几行（源 → 产物）。</summary>
        public IReadOnlyList<string> BuildHeadLines()
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

            lines.Add(
                OuterContainer == PackOuterContainer.SevenZip
                    ? $"最终产物：{OuterPath}（本机没有 Rar.exe，外层用 7z —— 无需额外安装）"
                    : $"最终产物：{OuterPath}");

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

        /// <summary>其余物到底是什么（弹窗里给用户看的一句）。</summary>
        public string RestExplanation => SourceKind == PackingSourceKind.File
            ? $"其余物 = 装 7z 分卷的文件夹 + 为这个文件临时建的同名文件夹「{SourceName}」"
            : "其余物 = 装 7z 分卷的那个文件夹";

        /// <summary>原包到底是什么。</summary>
        public string SourceExplanation => SourceKind == PackingSourceKind.File
            ? $"原包 = 你选的那个文件（{Path.GetFileName(SourcePath)}）"
            : $"原包 = 你选的那个文件夹（{SourceName}）";
    }
}
