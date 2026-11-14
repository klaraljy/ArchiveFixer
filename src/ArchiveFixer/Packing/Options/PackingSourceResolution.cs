using ArchiveFixer.Models;
using ArchiveFixer.Storage;
using System;
using System.IO;

namespace ArchiveFixer.Packing
{
    public sealed class PackingSourceResolution
    {
        public bool Success { get; init; }

        /// <summary>不能打包时那句给用户看的话。</summary>
        public string Reason { get; init; } = string.Empty;

        /// <summary>用户选的那个路径（文件夹或文件）。</summary>
        public string SourcePath { get; init; } = string.Empty;

        public PackingSourceKind Kind { get; init; } = PackingSourceKind.Folder;

        /// <summary>真正要打包的文件夹（单文件时是那个新建的同名文件夹）。</summary>
        public string FolderToPack { get; init; } = string.Empty;

        /// <summary>
        /// 单文件时**要新建**的同名文件夹（例如 <c>111.mp4</c> → <c>111\</c>）；文件夹时是空串。
        /// 它算**其余物**（用户 2026-09-26 原话："他不算是原包的内容"）—— 默认会被删掉。
        /// </summary>
        public string WrapperFolderToCreate { get; init; } = string.Empty;

        /// <summary>命名基准：文件夹名 / 文件基名（<c>1111</c>、<c>111</c>）。</summary>
        public string SourceName { get; init; } = string.Empty;
    }

    /// <summary>
    /// 把"用户选的东西"翻成"要打包哪个文件夹"（纯逻辑，除了 <c>File/Directory.Exists</c> 不碰文件系统）。
    /// </summary>
}
