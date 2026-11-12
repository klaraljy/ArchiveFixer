using System;
using System.IO;
using ArchiveFixer.Models;
using ArchiveFixer.Storage;

namespace ArchiveFixer.Packing
{    /// <summary>用户选的东西是**文件夹**还是**单个文件**（用户 2026-09-26 第 46 条要求支持单文件）。</summary>
    public enum PackingSourceKind
    {
        /// <summary>文件夹（主用法：里面装着资源）。</summary>
        Folder = 0,

        /// <summary>单个文件（例如 <c>111.mp4</c>）：程序在它旁边生成同名文件夹把它装进去再打包。</summary>
        File = 1
    }

    /// <summary>最终 <c>.rar</c> 放在哪（用户 2026-09-26 第 46 条 + 当天追加）。</summary>
}
