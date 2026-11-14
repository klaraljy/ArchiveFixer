using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Extraction.VolumeStep;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace ArchiveFixer.Tests;

/// <summary>
/// prepared 组只能是**有片的可提交结论**；`SizeAnomaly` 按设计不填 Pieces，
/// 不能被 `AnalyzePrepared` 变成 0 片候选去替换发现层正确的组（真机 336 的 Pending 根因之一）。
/// </summary>
public sealed class SecondStepPreparedFallbackTests
{
    [Fact]
    public void SizeAnomaly无片_不得生成候选()
    {
        var group = new PreparedVolumeGroup
        {
            BaseName = "336",
            Family = VolumeNamingFamily.SevenZipNumbered,
            OriginalSourceLayer = @"C:\src",
            OriginalEntryPath = @"C:\src\336.7z.001",
            State = PreparedGroupState.SizeAnomaly,
            Evidence = "7z 首卷自述 636736722 字节，实际 636790140 字节",
            SizeAnomalyDetail = "字节和不等于自述整包"
        };

        Assert.Empty(VolumeCandidateDiscovery.AnalyzePrepared(new[] { group }));
    }

    [Fact]
    public void SizeAnomaly有片_也不得当成可提交()
    {
        var group = new PreparedVolumeGroup
        {
            BaseName = "336",
            Family = VolumeNamingFamily.SevenZipNumbered,
            OriginalSourceLayer = @"C:\src",
            OriginalEntryPath = @"C:\src\336.7z.001",
            State = PreparedGroupState.SizeAnomaly,
            Evidence = "结构异常",
            Pieces = new[]
            {
                new PreparedVolumePiece
                {
                    OriginalPath = @"C:\src\336.7z.001",
                    CanonicalName = "336.7z.001",
                    Size = 100
                }
            }
        };

        var service = new VolumeGroupService();
        VolumeGroupUnit? unit = service.TryCommitPrepared(group, "owner", @"C:\out");

        Assert.True(unit == null || unit.State == VolumeGroupState.SizeAnomaly);
    }
}
