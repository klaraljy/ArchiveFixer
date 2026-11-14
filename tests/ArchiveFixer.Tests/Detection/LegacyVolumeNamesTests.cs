using ArchiveFixer.Detection;
using Xunit;

namespace ArchiveFixer.Tests
{
    public class LegacyVolumeNamesTests
    {
        [Fact]
        public void FacadeUsesExistingFactsForIndexAndPart()
        {
            Assert.Equal(1, LegacyVolumeNames.TryGetIndex("a.7z.001"));
            Assert.True(LegacyVolumeNames.IsVolumePart("a.7z.001"));
            Assert.False(LegacyVolumeNames.IsVolumePart("a.7z"));
            Assert.Null(LegacyVolumeNames.TryGetIndex("plain.txt"));
        }

        [Fact]
        public void FacadeExposesFirstVolumeAndGrouping()
        {
            Assert.Equal("a.7z.001", LegacyVolumeNames.TryGetFirstVolumeName("a.7z.002"));
            Assert.True(LegacyVolumeNames.BelongsToSameGroup("a.7z.001", "a.7z.002"));
            Assert.False(LegacyVolumeNames.BelongsToSameGroup("a.7z.001", "b.7z.001"));
        }
    }
}
