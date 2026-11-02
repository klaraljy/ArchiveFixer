using System.Linq;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    public class CleanupPlannerTests
    {
        [Fact]
        public void 账本_只保留有效路径_可按身份查询()
        {
            var ledger = new CleanupLedger();

            ledger.Add(new CleanupItem { Path = @"C:\out\a.7z", Kind = CleanupItemKind.LayerInput });
            ledger.Add(new CleanupItem { Path = " ", Kind = CleanupItemKind.UserSource });

            Assert.Single(ledger.Items);
            Assert.True(ledger.Has(CleanupItemKind.LayerInput));
            Assert.False(ledger.Has(CleanupItemKind.UserSource));

            ledger.Clear();
            Assert.Empty(ledger.Items);
        }

        [Fact]
        public void 成功链_顺序固定为过程物先于原包()
        {
            ChainCleanupPlan plan = CleanupPlanner.BuildChainPlan(@"C:\out", sourcePending: true);

            Assert.Equal(
                new[]
                {
                    ChainCleanupStep.CollectProcessArtifacts,
                    ChainCleanupStep.ApplyRestHandling,
                    ChainCleanupStep.SweepPassThrough,
                    ChainCleanupStep.ProcessUserSource
                },
                plan.Steps.ToArray());

            Assert.Equal(@"C:\out", plan.AllowedRoot);
        }

        [Fact]
        public void 空间不足档_规划里没有原包步骤()
        {
            ChainCleanupPlan plan = CleanupPlanner.BuildChainPlan(@"C:\out", sourcePending: false);

            Assert.DoesNotContain(ChainCleanupStep.ProcessUserSource, plan.Steps);
            Assert.Equal(ChainCleanupStep.SweepPassThrough, plan.Steps.Last());
        }

        [Fact]
        public void 允许根_取内容物文件夹的父目录()
        {
            Assert.Equal(@"C:\out", CleanupPlanner.ResolveAllowedRoot(@"C:\out\pkg"));
            Assert.Equal(string.Empty, CleanupPlanner.ResolveAllowedRoot(null));
        }
    }
}
