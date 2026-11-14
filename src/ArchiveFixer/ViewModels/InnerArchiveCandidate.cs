using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.Views;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;


namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// 本轮找到的一个内层归档，以及**它属于哪个父任务**。
    ///
    /// 为什么要带父任务（本轮改造的核心之一）：内层包不再被当成"另一个独立的任务"，
    /// 而是父任务这条流水线的一部分 —— 它的产物最终必须归到**父任务那一个**最终目录里。
    /// 只传文件路径的话，内层包会按自己的路径算落点（<c>&lt;id&gt;.7z\内容物</c>），
    /// 源目录旁边就又多一个平级目录 —— 正是用户抱怨的那个现象。
    /// </summary>
    internal sealed class InnerArchiveCandidate
    {
        /// <summary>内层归档的起点（分卷组的第一卷，或者单文件归档）。</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>父任务的最终目录（归集之后的落点）；内层任务的产物就落在这里。</summary>
        public string ParentOutputDirectory { get; init; } = string.Empty;

        /// <summary>父任务的名字（只用于日志与报告）。</summary>
        public string ParentTaskName { get; init; } = string.Empty;

        /// <summary>
        /// 这条续解链**根任务的身份**（= 根任务那一刻的规范化绝对路径）。
        /// <para>为什么必须按路径认链：链尾「删除操作」原先只看"是不是续解任务"，而调用方传进来的是
        /// **整批**任务清单 —— 于是 A 目录那个包的链尾，被 B 目录里**同名**的另一个包的失败挡住
        /// （真机 2026-09-30 日志原文：`333-Rar4.part1.rar：链尾的其余物不处理（链上的「111.part1.rar」没有成功）`）。
        /// 名字相同 ≠ 同一条链。</para>
        /// </summary>
        public string RootSourcePath { get; init; } = string.Empty;
    }
}

