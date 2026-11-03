using ArchiveFixer.Storage;
using Xunit;

namespace ArchiveFixer.Tests
{
    public class SpaceDecisionStateTests
    {
        [Fact]
        public void ProbeFailureIsUnknownNotEnough()
        {
            var decision = new SpaceGateDecision
            {
                Allowed = true,
                AvailableBytes = -1
            };

            Assert.Equal(SpaceDecisionState.Unknown, decision.State);
            Assert.True(decision.ProbeFailed);
        }

        [Fact]
        public void AllowedWithKnownBytesIsEnough()
        {
            var decision = new SpaceGateDecision
            {
                Allowed = true,
                AvailableBytes = 1024
            };

            Assert.Equal(SpaceDecisionState.Enough, decision.State);
        }

        [Fact]
        public void RejectedWithKnownBytesIsNotEnough()
        {
            var decision = new SpaceGateDecision
            {
                Allowed = false,
                AvailableBytes = 1024
            };

            Assert.Equal(SpaceDecisionState.NotEnough, decision.State);
        }
    }
}
