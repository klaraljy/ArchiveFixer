using System.Linq;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    public sealed class ContentGroupFailureFactsTests
    {
        [Fact]
        public void Missing_and_corrupted_are_group_facts()
        {
            var group = new ContentGroup
            {
                GroupId = "g",
                BaseName = "aaa-L4",
                Family = VolumeNamingFamily.ZipNumbered,
                SourceLayer = @"X:\src"
            };

            Assert.True(group.MarkFinalGroupFailure(FinalGroupFailureKind.Incomplete, "缺片"));
            Assert.Equal(FinalContentState.Missing, group.FinalState);
            Assert.Equal(FinalGroupFailureKind.Incomplete, group.FailureKind);
            Assert.False(group.FinalContentVerified);

            var corrupted = new ContentGroup
            {
                GroupId = "g2",
                BaseName = "aaa-L5",
                Family = VolumeNamingFamily.ZipNumbered,
                SourceLayer = @"X:\src"
            };

            Assert.True(corrupted.MarkFinalGroupFailure(FinalGroupFailureKind.Corrupted, "坏片"));
            Assert.Equal(FinalGroupFailureKind.Corrupted, corrupted.FailureKind);
            Assert.Equal(FinalContentState.Missing, corrupted.FinalState);
        }

        [Fact]
        public void Source_and_process_producers_are_separated()
        {
            var group = new ContentGroup
            {
                GroupId = "g",
                BaseName = "aaa-L4",
                Family = VolumeNamingFamily.ZipNumbered,
                SourceLayer = @"X:\src"
            };

            group.RecordFinalLayerProducer("src.rar", @"X:\src\src.rar", isSource: true);
            group.RecordFinalLayerProducer("mid.7z", @"X:\out\mid.7z", isSource: false);

            Assert.Single(group.FinalLayerProducers, item => item.IsSource);
            Assert.Single(group.FinalLayerProducers, item => !item.IsSource);
            Assert.Equal(@"X:\src\src.rar", group.FinalLayerProducers[0].Path);
            Assert.Equal(@"X:\out\mid.7z", group.FinalLayerProducers[1].Path);
        }
    }
}

