using ArchiveFixer.Extraction;
using System;
using System.IO;
using Xunit;

namespace ArchiveFixer.Tests;

/// <summary>
/// 特定解压成品发布边界：隐藏的 layer 可以搬成成品，但成品根目录必须可见；
/// 文件属性与单包裹展平规则不能被顺手改掉。
/// </summary>
public sealed class SpecificExtractionOutputPublisherTests : IDisposable
{
    private readonly string _root;

    public SpecificExtractionOutputPublisherTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "SpecificPublish_" + Guid.NewGuid().ToString("N"));
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
    public void 根层散文件_成品根可见且文件属性不变()
    {
        string layer = Path.Combine(_root, "layer-00");
        Directory.CreateDirectory(layer);
        File.WriteAllText(Path.Combine(layer, "001.jpg"), "x");
        File.SetAttributes(layer, File.GetAttributes(layer) | FileAttributes.Hidden);
        File.SetAttributes(Path.Combine(layer, "001.jpg"), FileAttributes.ReadOnly);

        var publisher = new SpecificExtractionOutputPublisher(null);
        string finalPath = publisher.Publish("outer.7z", layer, Path.Combine(_root, "out"));

        Assert.False((File.GetAttributes(finalPath) & FileAttributes.Hidden) != 0);
        Assert.Equal(FileAttributes.ReadOnly, File.GetAttributes(Path.Combine(finalPath, "001.jpg")));

        File.SetAttributes(Path.Combine(finalPath, "001.jpg"), FileAttributes.Normal);
    }

    [Fact]
    public void 单一包裹文件夹_展平到成品根且可见()
    {
        string layer = Path.Combine(_root, "layer-00");
        string wrapper = Path.Combine(layer, "inner");
        Directory.CreateDirectory(wrapper);
        File.WriteAllText(Path.Combine(wrapper, "001.jpg"), "x");
        File.SetAttributes(layer, File.GetAttributes(layer) | FileAttributes.Hidden);

        var publisher = new SpecificExtractionOutputPublisher(null);
        string finalPath = publisher.Publish("outer.7z", layer, Path.Combine(_root, "out"));

        Assert.Equal("inner", Path.GetFileName(finalPath));
        Assert.False((File.GetAttributes(finalPath) & FileAttributes.Hidden) != 0);
        Assert.True(File.Exists(Path.Combine(finalPath, "001.jpg")));
    }
}
