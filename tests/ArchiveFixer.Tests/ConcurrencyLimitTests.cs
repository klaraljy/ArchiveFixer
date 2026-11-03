using ArchiveFixer.Storage;
using Xunit;

namespace ArchiveFixer.Tests
{
    public class ConcurrencyLimitTests
    {
        [Fact]
        public void EffectiveTakesLowerOfRequestedAndRecommendation()
        {
            Assert.Equal(2, ConcurrencyLimit.Create(4, 2).Effective);
            Assert.Equal(3, ConcurrencyLimit.Create(3, 8).Effective);
        }

        [Fact]
        public void UnknownRecommendationUsesRequestedAndClamps()
        {
            Assert.Equal(4, ConcurrencyLimit.Create(4, 0).Effective);
            Assert.Equal(8, ConcurrencyLimit.Create(99, 0).Effective);
            Assert.Equal(1, ConcurrencyLimit.Create(0, 0).Effective);
        }
    }
}
