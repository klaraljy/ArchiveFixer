using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ArchiveFixer.Tests.Batch120;

public sealed class BatchNormalSettingsTests
{
    [Fact]
    public void Normal40_位置严格各半_且不要求每个组合5份()
    {
        const string seed = "929999999";
        var choices = Enumerable.Range(1, 40)
            .Select(i => BatchNormalSettings.TryGet((929999999L + i).ToString(), seed, out var c) ? c : default)
            .ToArray();

        Assert.Equal(20, choices.Count(c => c.Placement == "Original"));
        Assert.Equal(20, choices.Count(c => c.Placement == "Specified"));
        Assert.DoesNotContain(choices, c => string.IsNullOrEmpty(c.Placement));
    }

    [Fact]
    public void Normal40_八个组合全出现()
    {
        const string seed = "929999999";
        var combos = new HashSet<string>();
        for (long id = 930000000; id <= 930000039; id++)
        {
            Assert.True(BatchNormalSettings.TryGet(id.ToString(), seed, out var c));
            combos.Add($"{c.Placement}/{c.SourceHandling}/{c.RestHandling}");
        }

        Assert.Equal(8, combos.Count);
    }

    [Fact]
    public void 例子930000000()
    {
        Assert.True(BatchNormalSettings.TryGet("930000000", "929999999", out var c));
        Assert.Equal("Original", c.Placement);
        Assert.Equal("DeleteInPlace", c.SourceHandling);
        Assert.Equal("Keep", c.RestHandling);
    }

    [Fact]
    public void 只覆盖Normal1到40()
    {
        Assert.False(BatchNormalSettings.TryGet("929999999", "929999999", out _));
        Assert.False(BatchNormalSettings.TryGet("930000041", "929999999", out _));
        Assert.False(BatchNormalSettings.TryGet("930000051", "929999999", out _));
    }
}
