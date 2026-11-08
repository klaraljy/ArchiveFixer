using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    public sealed class SpaceTightPolicyTests
    {
        [Fact]
        public void Normal_batch_keeps_last_process_and_does_not_force_source_delete()
        {
            SpaceTightPolicy policy = SpaceTightPolicy.ForBatch(false, false);

            Assert.False(policy.Enabled);
            Assert.True(policy.LastProcessOnFailureKeeps);
            Assert.True(policy.RestOnFailureKeepsLastProcess);
            Assert.False(policy.SourceOnLayerSuccessDeletes);
        }

        [Fact]
        public void Space_tight_keeps_last_process_even_when_source_deletes()
        {
            SpaceTightPolicy policy = SpaceTightPolicy.ForBatch(true, keepSource: false);

            Assert.True(policy.Enabled);
            Assert.True(policy.SourceOnLayerSuccessDeletes);
            Assert.True(policy.LastProcessOnFailureKeeps);
            Assert.True(policy.RestOnFailureKeepsLastProcess);
            Assert.True(policy.PartialContentOnFailureDrops);
        }

        [Fact]
        public void Space_tight_keep_source_is_a_source_only_override()
        {
            SpaceTightPolicy policy = SpaceTightPolicy.ForBatch(true, keepSource: true);

            Assert.True(policy.Enabled);
            Assert.True(policy.KeepSource);
            Assert.False(policy.SourceOnLayerSuccessDeletes);
            Assert.True(policy.LastProcessOnFailureKeeps);
        }
    }
}
