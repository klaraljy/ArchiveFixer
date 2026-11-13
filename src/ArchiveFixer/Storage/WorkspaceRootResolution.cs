using System;
using System.Collections.Generic;

namespace ArchiveFixer.Storage
{
    /// <summary>
    /// 一次"本批工作区放哪"的结论。
    ///
    /// <para>它只描述结论（路径 + 为什么 + 目标目录在哪），不碰任何界面 —— 分层铁律：
    /// <c>Storage</c> 不引用 WPF。</para>
    /// </summary>
    public sealed class WorkspaceRootResolution
    {
        /// <summary>
        /// 本批工作区根目录。**目标位置不可用时是空串**（<see cref="WorkspaceRootOrigin.TargetUnusable"/>）——
        /// 调用方必须据此停下并报错，⛔ 不许拿空串去拼路径、更不许自己补一个"兜底位置"。
        /// </summary>
        public string RootDirectory { get; init; } = string.Empty;

        /// <summary>这个根是怎么定出来的。</summary>
        public WorkspaceRootOrigin Origin { get; init; }

        /// <summary>
        /// 工作区落在**哪个目标目录**里面（它就是"这一单成品要落的目录"，也是
        /// <see cref="RootDirectory"/> 的父目录）。定不下来时为空串。
        /// </summary>
        public string TargetDirectory { get; init; } = string.Empty;

        /// <summary>
        /// 这个目标目录**是这一次建出来的**（解析之前它还不存在）。
        ///
        /// <para>为什么要如实记下来：工作区要落在目标目录里面，所以批首必须先建它（用户原话
        /// "原地就在原地创"）。代价是"最终目录连建都不该建"这条老口径在实现上不再成立 ——
        /// 于是收尾那边必须知道"这个目录壳是不是我们自己造的"：是，而且里面什么都没有时，
        /// 就该由我们自己收掉（⛔ 用户本来就有的目录一个字节都不许动）。</para>
        /// </summary>
        public bool TargetDirectoryCreated { get; init; }

        /// <summary>本批落点涉及到的盘（去重、按首次出现顺序）。只有一个盘时是单元素。</summary>
        public IReadOnlyList<string> BatchDrives { get; init; } = Array.Empty<string>();

        /// <summary>本批落点跨了不止一个盘（跨盘时**必须如实说明**，不许假装同盘）。</summary>
        public bool CrossDrive => BatchDrives.Count > 1;

        /// <summary>为什么是它（中文，含真实路径）—— 进日志，用户与排障都读得到。</summary>
        public string Reason { get; init; } = string.Empty;

        /// <summary>真的定下来了（<see cref="RootDirectory"/> 非空）。false = 必须报错停手。</summary>
        public bool Resolved => Origin != WorkspaceRootOrigin.TargetUnusable;

        /// <summary>这条结论该用的日志级别：定不下来是 ERROR（不许降级成 WARN 让它看起来没事）。</summary>
        public string LogLevel => Resolved ? "INFO" : "ERROR";
    }
}
