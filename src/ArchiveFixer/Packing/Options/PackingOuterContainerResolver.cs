using ArchiveFixer.Models;
using ArchiveFixer.Storage;
using System;
using System.IO;

namespace ArchiveFixer.Packing;

public static class PackingOuterContainerResolver
{
    /// <summary>
    /// 解析规则（用户原话："如果用户没有装 WinRAR，那就弄 7z 吧"）：
    /// 要 rar → 有 <c>Rar.exe</c> 就用 rar，没有但有内置 7-Zip 就**自动 7z**，两个都没有则做不出来；
    /// 要 7z → 有内置 7-Zip 就用它，没有则做不出来；不做外层 → 照旧（永远成立）。
    /// ⛔ **绝不把 7z 的产物叫成 <c>.rar</c>** —— 产物名跟着实际容器走。
    /// </summary>
    public static PackingOuterContainerResolution Resolve(
        PackOuterContainer requested,
        bool rarExists,
        bool sevenZipExists)
    {
        PackOuterContainer effective = requested switch
        {
            PackOuterContainer.Rar => rarExists
                ? PackOuterContainer.Rar
                : sevenZipExists ? PackOuterContainer.SevenZip : PackOuterContainer.None,
            PackOuterContainer.SevenZip => sevenZipExists ? PackOuterContainer.SevenZip : PackOuterContainer.None,
            _ => PackOuterContainer.None
        };

        return new PackingOuterContainerResolution
        {
            Requested = requested,
            Effective = effective
        };
    }
}
