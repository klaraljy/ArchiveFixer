using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using System;
using System.IO;
using Xunit;

namespace ArchiveFixer.Tests;

/// <summary>
/// 特定解压容器根：未指定位置跟源包父目录；指定位置时保留选中文件夹名和相对子路径。
/// 这里只测容器根，最后一层内容名由 SpecificOutputNaming.Resolve 单独负责。
/// </summary>
public sealed class SpecificExtractionPlacementTests : IDisposable
{
    private readonly string _root;

    public SpecificExtractionPlacementTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "SpecificPlacement_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
        }
    }

    [Fact]
    public void 文件夹指定位置_保留选中文件夹名和相对子路径()
    {
        string selected = Path.Combine(_root, "AAA");
        string nested = Path.Combine(selected, "111合集");
        string target = Path.Combine(_root, "BBB");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(target);
        string source = Path.Combine(nested, "001.rar");
        File.WriteAllText(source, "x");

        OutputPlacementResult result = SpecificExtractionPlacement.ResolveContainerRoot(
            source,
            OutputPlacementMode.CustomRootPerArchive,
            target,
            SourceSelectionKind.Folder,
            selected);

        Assert.True(result.Success, result.Message);
        Assert.Equal(Path.Combine(target, "AAA", "111合集"), result.DestinationDirectory);
        Assert.Equal("111合集", result.RelativeSubPath);
    }

    [Fact]
    public void 文件夹指定位置_包直接躺在选中文件夹里_只加选中文件夹名()
    {
        string selected = Path.Combine(_root, "AAA");
        string target = Path.Combine(_root, "BBB");
        Directory.CreateDirectory(selected);
        Directory.CreateDirectory(target);
        string source = Path.Combine(selected, "001.rar");
        File.WriteAllText(source, "x");

        OutputPlacementResult result = SpecificExtractionPlacement.ResolveContainerRoot(
            source,
            OutputPlacementMode.CustomRootPerArchive,
            target,
            SourceSelectionKind.Folder,
            selected);

        Assert.True(result.Success, result.Message);
        Assert.Equal(Path.Combine(target, "AAA"), result.DestinationDirectory);
        Assert.Equal(string.Empty, result.RelativeSubPath);
    }

    [Fact]
    public void 单文件指定位置_只用指定根()
    {
        string source = Path.Combine(_root, "001.rar");
        string target = Path.Combine(_root, "BBB");
        File.WriteAllText(source, "x");
        Directory.CreateDirectory(target);

        OutputPlacementResult result = SpecificExtractionPlacement.ResolveContainerRoot(
            source,
            OutputPlacementMode.CustomRootPerArchive,
            target,
            SourceSelectionKind.File,
            source);

        Assert.True(result.Success, result.Message);
        Assert.Equal(target, result.DestinationDirectory);
    }

    [Fact]
    public void 文件夹未指定位置_用源包父目录()
    {
        string selected = Path.Combine(_root, "AAA");
        string nested = Path.Combine(selected, "111合集");
        Directory.CreateDirectory(nested);
        string source = Path.Combine(nested, "001.rar");
        File.WriteAllText(source, "x");

        OutputPlacementResult result = SpecificExtractionPlacement.ResolveContainerRoot(
            source,
            OutputPlacementMode.PerArchiveSubfolder,
            null,
            SourceSelectionKind.Folder,
            selected);

        Assert.True(result.Success, result.Message);
        Assert.Equal(nested, result.DestinationDirectory);
    }

    [Fact]
    public void 源包在选中文件夹之外_相对子路径不越界()
    {
        string selected = Path.Combine(_root, "AAA");
        string outside = Path.Combine(_root, "outside");
        string target = Path.Combine(_root, "BBB");
        Directory.CreateDirectory(selected);
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(target);
        string source = Path.Combine(outside, "001.rar");
        File.WriteAllText(source, "x");

        OutputPlacementResult result = SpecificExtractionPlacement.ResolveContainerRoot(
            source,
            OutputPlacementMode.CustomRootPerArchive,
            target,
            SourceSelectionKind.Folder,
            selected);

        Assert.True(result.Success, result.Message);
        Assert.Equal(Path.Combine(target, "AAA"), result.DestinationDirectory);
        Assert.Equal(string.Empty, result.RelativeSubPath);
        Assert.DoesNotContain("..", result.DestinationDirectory, StringComparison.Ordinal);
    }
}

