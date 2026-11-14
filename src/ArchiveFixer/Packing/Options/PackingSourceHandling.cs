using ArchiveFixer.Models;
using ArchiveFixer.Storage;
using System;
using System.IO;

namespace ArchiveFixer.Packing;

public enum PackingSourceHandling
{
    /// <summary>不动（默认）：源文件夹/文件一个字节都不碰。</summary>
    KeepInPlace = 0,

    /// <summary>移入其余物（= 装 7z 分卷的那个文件夹里面）。</summary>
    MoveToRest = 1
}

/// <summary>打包里的**其余物操作**（= 装 7z 分卷的那个文件夹；用户 2026-09-26）。</summary>

