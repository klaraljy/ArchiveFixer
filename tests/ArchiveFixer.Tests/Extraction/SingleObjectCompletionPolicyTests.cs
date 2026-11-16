using ArchiveFixer.BindingModels;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using Xunit;

namespace ArchiveFixer.Tests;

public sealed class SingleObjectCompletionPolicyTests
{
    private static OutputVerificationResult Verified() => new()
    {
        Verified = true,
        Known = true,
        ManifestCrossChecked = true
    };

    [Fact]
    public void 删除源包_要求成功且校验通过()
    {
        Assert.True(SingleObjectCompletionPolicy.ShouldDeleteSource(
            SourceHandlingMode.DeleteInPlace, false, TaskOutcome.Succeeded, Verified(), false));

        Assert.False(SingleObjectCompletionPolicy.ShouldDeleteSource(
            SourceHandlingMode.KeepInPlace, false, TaskOutcome.Succeeded, Verified(), false));

        Assert.False(SingleObjectCompletionPolicy.ShouldDeleteSource(
            SourceHandlingMode.DeleteInPlace, false, TaskOutcome.Failed, Verified(), false));

        Assert.False(SingleObjectCompletionPolicy.ShouldDeleteSource(
            SourceHandlingMode.DeleteInPlace, false, TaskOutcome.Succeeded, new OutputVerificationResult { Verified = true }, false));
    }

    [Fact]
    public void 空间不足_成功后删源包_失败不删()
    {
        Assert.True(SingleObjectCompletionPolicy.ShouldDeleteSource(
            SourceHandlingMode.KeepInPlace, true, TaskOutcome.Succeeded, Verified(), false));

        Assert.False(SingleObjectCompletionPolicy.ShouldDeleteSource(
            SourceHandlingMode.KeepInPlace, true, TaskOutcome.Failed, Verified(), false));
    }

    [Fact]
    public void 最后一层过程物_空间不足固定保留_普通档按删除档()
    {
        Assert.True(SingleObjectCompletionPolicy.ShouldKeepLastProcess(RestHandlingModes.Keep, true));
        Assert.True(SingleObjectCompletionPolicy.ShouldKeepLastProcess(RestHandlingModes.Delete, true));
        Assert.True(SingleObjectCompletionPolicy.ShouldKeepLastProcess(RestHandlingModes.Keep, false));
        Assert.False(SingleObjectCompletionPolicy.ShouldKeepLastProcess(RestHandlingModes.Delete, false));
    }
}
