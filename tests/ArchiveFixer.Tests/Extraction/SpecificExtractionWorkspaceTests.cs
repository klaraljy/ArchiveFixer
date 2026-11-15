using ArchiveFixer.Extraction;
using System;
using System.IO;
using Xunit;

namespace ArchiveFixer.Tests;

/// <summary>
/// 特定解压工作区的唯一创建出口：父根、任务目录、每层目录、标准卷入口目录
/// 都必须在创建时带 Hidden；清理只收自己那一份。
/// </summary>
public sealed class SpecificExtractionWorkspaceTests : IDisposable
{
    private readonly string _root;

    public SpecificExtractionWorkspaceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "SpecificWorkspace_" + Guid.NewGuid().ToString("N"));
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
    public void 工作区根_任务目录_层目录_标准卷入口都隐藏()
    {
        string outputRoot = Path.Combine(_root, "out");
        SpecificExtractionWorkspace workspace = SpecificExtractionWorkspace.Create(outputRoot);

        string layer = workspace.CreateLayer(0);
        string entry = workspace.CreateStandardVolumeEntry(layer);

        Assert.True(IsHidden(workspace.RootDirectory), workspace.RootDirectory);
        Assert.True(IsHidden(workspace.TaskDirectory), workspace.TaskDirectory);
        Assert.True(IsHidden(layer), layer);
        Assert.True(IsHidden(entry), entry);
    }

    [Fact]
    public void 清理只删自己的任务目录_父根空时收掉()
    {
        string outputRoot = Path.Combine(_root, "out");
        SpecificExtractionWorkspace workspace = SpecificExtractionWorkspace.Create(outputRoot);
        File.WriteAllText(Path.Combine(workspace.TaskDirectory, "half.bin"), "x");

        workspace.Cleanup();

        Assert.False(Directory.Exists(workspace.TaskDirectory));
        Assert.False(Directory.Exists(workspace.RootDirectory));
    }

    private static bool IsHidden(string path) =>
        Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.Hidden) != 0;
}
