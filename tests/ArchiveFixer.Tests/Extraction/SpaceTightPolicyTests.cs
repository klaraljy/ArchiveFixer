using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests;

public sealed class SpaceTightPolicyTests
{
    [Fact]
    public void Normal_batch_keeps_last_process_and_does_not_force_source_delete()
    {
        SpaceTightPolicy policy = SpaceTightPolicy.ForBatch(false);

        Assert.False(policy.Enabled);
        Assert.True(policy.LastProcessOnFailureKeeps);
        Assert.True(policy.RestOnFailureKeepsLastProcess);
        Assert.False(policy.SourceOnLayerSuccessDeletes);
    }

    [Fact]
    public void Space_tight_deletes_source_but_keeps_last_process()
    {
        SpaceTightPolicy policy = SpaceTightPolicy.ForBatch(true);

        Assert.True(policy.Enabled);
        Assert.True(policy.SourceOnLayerSuccessDeletes);
        Assert.True(policy.LastProcessOnFailureKeeps);
        Assert.True(policy.RestOnFailureKeepsLastProcess);
        Assert.True(policy.PartialContentOnFailureDrops);
    }
}
