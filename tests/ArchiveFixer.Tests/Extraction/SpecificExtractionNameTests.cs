using ArchiveFixer.Detection;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 特定解压的合集样本里，`弦音sic NO.001 Alter EGO egoist [9P-92MB].rar` 这类名字
    /// 是**独立归档**，不是分卷。名字里的 `NO.001` 只是标题的一部分；
    /// 不能因为“第一个点之前都是弦音sic NO”就把它和其它包合并成分卷组。
    /// </summary>
    public sealed class SpecificExtractionNameTests
    {
        [Fact]
        public void 合集内独立RAR_不会被归成同一分卷组()
        {
            var candidates = new[]
            {
                new VolumeCandidate { Path = "弦音sic NO.001 Alter EGO egoist [9P-92MB].rar" },
                new VolumeCandidate { Path = "弦音sic NO.002 FGO 伊莉雅 白裙 [9P-75MB].rar" },
                new VolumeCandidate { Path = "弦音sic NO.027 自拍 黄头发妹妹 [9P-33MB].rar" }
            };

            var groups = VolumeGroupDetector.Group(candidates);

            Assert.All(groups, group => Assert.True(group.KnownVolumeCount <= 1));
        }
    }
}
