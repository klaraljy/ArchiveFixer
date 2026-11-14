using ArchiveFixer.Models;
using ArchiveFixer.Storage;
using System;
using System.IO;

namespace ArchiveFixer.Packing;

public sealed class PackingOuterContainerResolution
{
    /// <summary>本来要做的（请求里那个）。</summary>
    public PackOuterContainer Requested { get; init; } = PackOuterContainer.Rar;

    /// <summary>实际会做的。</summary>
    public PackOuterContainer Effective { get; init; } = PackOuterContainer.Rar;

    /// <summary>是不是"要 rar 但本机没有 <c>Rar.exe</c>，于是自动改用 7z"。</summary>
    public bool FellBackToSevenZip =>
        Requested == PackOuterContainer.Rar && Effective == PackOuterContainer.SevenZip;

    /// <summary>要外层、却连内置 7-Zip 都没有 → 什么外层都做不出来（调用方据此报错）。</summary>
    public bool Impossible => Requested.HasOuterArtifact() && !Effective.HasOuterArtifact();

    /// <summary>给用户与日志的一句话（没有发生回退时是空串）。</summary>
    public string Note => FellBackToSevenZip
        ? "本机没有 Rar.exe（WinRAR 是共享软件，程序不随包分发它）—— 自动改用 7z 外层："
          + "产物会是一个 .7z，而不是 .rar。"
        : string.Empty;
}

/// <summary>把"想做的容器"翻成"实际会做的容器"（**唯一出口**，见 <see cref="PackingOuterContainerResolution"/>）。</summary>

