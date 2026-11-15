using ArchiveFixer.Extraction;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace ArchiveFixer.Tests;

public sealed class StandardNameFastPathTests
{
    [Fact]
    public void 同目录001到004_同基名005在别处_判Incomplete不判内容缺片()
    {
        string a = @"C:\in-a";
        string b = @"C:\in-b";
        var paths = new[]
        {
            Path.Combine(a, "9953.part1.rar"),
            Path.Combine(a, "9953.part2.rar"),
            Path.Combine(a, "9953.part3.rar"),
            Path.Combine(a, "9953.part4.rar"),
            Path.Combine(b, "9953.part5.rar")
        };

        IReadOnlyList<StandardNameFastPath.Decision> decisions = StandardNameFastPath.Decide(paths);

        Assert.Single(decisions);
        Assert.Equal(StandardNameFastPath.DecisionKind.Incomplete, decisions[0].Kind);
    }

    [Fact]
    public void 同目录完整标准组_判Ready()
    {
        string directory = @"C:\in";
        IReadOnlyList<StandardNameFastPath.Decision> decisions = StandardNameFastPath.Decide(new[]
        {
            Path.Combine(directory, "9953.part1.rar"),
            Path.Combine(directory, "9953.part2.rar")
        });

        StandardNameFastPath.Decision decision = Assert.Single(decisions);
        Assert.Equal(StandardNameFastPath.DecisionKind.Ready, decision.Kind);
    }

    [Fact]
    public void Rar标准partN_同目录同基名一组_入口part1()
    {
        string directory = @"C:\in";
        var groups = StandardNameFastPath.Find(new[]
        {
            Path.Combine(directory, "9953.part1.rar"),
            Path.Combine(directory, "9953.part2.rar"),
            Path.Combine(directory, "9953.part3.rar")
        });

        StandardNameFastPath.Group group = Assert.Single(groups);
        Assert.Equal("9953", group.BaseName);
        Assert.Equal(StandardNameFastPath.Family.RarPart, group.Family);
        Assert.Equal(Path.Combine(directory, "9953.part1.rar"), group.EntryPath);
        Assert.Equal(3, group.Pieces.Count);
    }

    [Fact]
    public void 被回退封掉的基名不再Ready()
    {
        string directory = @"C:\in";
        var paths = new[]
        {
            Path.Combine(directory, "9953.part1.rar"),
            Path.Combine(directory, "9953.part2.rar")
        };

        StandardNameFastPath.Decision decision = Assert.Single(
            StandardNameFastPath.Decide(paths, _ => true));

        Assert.Equal(StandardNameFastPath.DecisionKind.Incomplete, decision.Kind);
    }

    [Fact]
    public void 缺号的标准片不返回快通道组()
    {
        string directory = @"C:\in";
        var paths = new[]
        {
            Path.Combine(directory, "9953.part1.rar"),
            Path.Combine(directory, "9953.part2.b2"),
            Path.Combine(directory, "9953.part3.c3"),
            Path.Combine(directory, "9953.part4.rar")
        };

        Assert.Empty(StandardNameFastPath.Find(paths));
        Assert.DoesNotContain(
            StandardNameFastPath.Decide(paths),
            decision => decision.Kind == StandardNameFastPath.DecisionKind.Ready);
    }

    [Fact]
    public void 卷号有缺口或者重复_一律不Ready()
    {
        string directory = @"C:\in";

        Assert.Empty(StandardNameFastPath.Find(new[]
        {
            Path.Combine(directory, "gap.7z.001"),
            Path.Combine(directory, "gap.7z.003")
        }));

        Assert.Empty(StandardNameFastPath.Find(new[]
        {
            Path.Combine(directory, "dup.part1.rar"),
            Path.Combine(directory, "dup.part01.rar")
        }));
    }

    [Fact]
    public void 超长卷号不抛异常_直接不Ready()
    {
        string directory = @"C:\in";
        var paths = new[]
        {
            Path.Combine(directory, "huge.part99999999999999999999.rar"),
            Path.Combine(directory, "huge.part2.rar")
        };

        Assert.Empty(StandardNameFastPath.Find(paths));
    }

    [Fact]
    public void 带额外点的基名不合并_交给正常第二大步()
    {
        string directory = @"C:\in";
        var paths = new[]
        {
            Path.Combine(directory, "报告.第一季.7z.001"),
            Path.Combine(directory, "报告.第一季.7z.002"),
            Path.Combine(directory, "报告.第二季.7z.001"),
            Path.Combine(directory, "报告.第二季.7z.002")
        };

        Assert.Empty(StandardNameFastPath.Find(paths));
    }

    [Fact]
    public void 单片标准包不进入第二大步快通道()
    {
        Assert.Empty(StandardNameFastPath.Find(new[]
        {
            Path.Combine(@"C:\in", "single.7z.001")
        }));
    }

    [Fact]
    public void 后缀夹中文_仍然按标准名识别_基名不动()
    {
        string directory = @"C:\in";
        var groups = StandardNameFastPath.Find(new[]
        {
            Path.Combine(directory, "小明.par删除t1.ra除r"),
            Path.Combine(directory, "小明.part2.rar删除")
        });

        StandardNameFastPath.Group group = Assert.Single(groups);
        Assert.Equal("小明", group.BaseName);
        Assert.Equal(StandardNameFastPath.Family.RarPart, group.Family);
        Assert.Equal(Path.Combine(directory, "小明.par删除t1.ra除r"), group.EntryPath);
        Assert.Equal(2, group.Pieces.Count);
    }

    [Fact]
    public void 快通道名字族_映射到既有命名族_不要求内容卡定族()
    {
        Assert.Equal(
            ArchiveFixer.Detection.VolumeNamingFamily.RarPart,
            StandardNameFastPath.ToNamingFamily(StandardNameFastPath.Family.RarPart));
        Assert.Equal(
            ArchiveFixer.Detection.VolumeNamingFamily.RarPart,
            StandardNameFastPath.ToNamingFamily(StandardNameFastPath.Family.RarOld));
        Assert.Equal(
            ArchiveFixer.Detection.VolumeNamingFamily.SevenZipNumbered,
            StandardNameFastPath.ToNamingFamily(StandardNameFastPath.Family.SevenZip));
        Assert.Equal(
            ArchiveFixer.Detection.VolumeNamingFamily.ZipNumbered,
            StandardNameFastPath.ToNamingFamily(StandardNameFastPath.Family.ZipNumbered));
        Assert.Equal(
            ArchiveFixer.Detection.VolumeNamingFamily.ZipSpanned,
            StandardNameFastPath.ToNamingFamily(StandardNameFastPath.Family.ZipSpanned));
    }

    [Fact]
    public void 跨盘Zip_标准入口是末片zip()
    {
        string directory = @"C:\in";
        var groups = StandardNameFastPath.Find(new[]
        {
            Path.Combine(directory, "archive.z01"),
            Path.Combine(directory, "archive.z02"),
            Path.Combine(directory, "archive.zip")
        });

        StandardNameFastPath.Group group = Assert.Single(groups);
        Assert.Equal(StandardNameFastPath.Family.ZipSpanned, group.Family);
        Assert.Equal(Path.Combine(directory, "archive.zip"), group.EntryPath);
    }

    [Fact]
    public void 七z和Zip通用分片各自成组_入口001()
    {
        string directory = @"C:\in";
        var groups = StandardNameFastPath.Find(new[]
        {
            Path.Combine(directory, "pack.7z.001"),
            Path.Combine(directory, "pack.7z.002"),
            Path.Combine(directory, "zip.zip.001"),
            Path.Combine(directory, "zip.zip.002")
        });

        Assert.Equal(2, groups.Count);
        Assert.Contains(groups, group => group.Family == StandardNameFastPath.Family.SevenZip
            && group.EntryPath.EndsWith("pack.7z.001", System.StringComparison.OrdinalIgnoreCase));
        Assert.Contains(groups, group => group.Family == StandardNameFastPath.Family.ZipNumbered
            && group.EntryPath.EndsWith("zip.zip.001", System.StringComparison.OrdinalIgnoreCase));
    }
}
