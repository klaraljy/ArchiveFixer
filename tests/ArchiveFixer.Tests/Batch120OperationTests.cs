using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace ArchiveFixer.Tests.Batch120
{
    /// <summary>
    /// 操作代码的入口：只负责把样本真跑一遍并落运行记录，不做期望值断言。
    ///
    /// 覆盖 120 份：普通 50（40 成功 + 10 失败）、特定 20、异常 25、空间 25。
    /// 一条失败只影响它自己那一份，其余照跑。
    ///
    /// 空间 25 份目前是"不模拟空间"的版本：产品没有可注入剩余空间的探针，
    /// 真盘空间足够，所以 expected.blocked 那一档暂时跑不出来。
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public sealed class Batch120OperationTests
    {
        public static IEnumerable<object[]> NormalIds()
        {
            for (int index = 1; index <= 50; index++)
            {
                yield return new object[] { (long.Parse(Batch120Operation.Seed) + index).ToString() };
            }
        }

        public static IEnumerable<object[]> SpecificIds()
        {
            for (int index = 1; index <= 20; index++)
            {
                yield return new object[] { (long.Parse(Batch120Operation.Seed) + 100000 + index).ToString() };
            }
        }

        public static IEnumerable<object[]> AbnormalIds()
        {
            for (int index = 1; index <= 25; index++)
            {
                yield return new object[] { (long.Parse(Batch120Operation.Seed) + 200000 + index).ToString() };
            }
        }

        public static IEnumerable<object[]> SpaceIds()
        {
            for (int index = 1; index <= 15; index++)
            {
                yield return new object[] { "C" + index.ToString("D2") };
            }

            for (int index = 1; index <= 10; index++)
            {
                yield return new object[] { "B" + index.ToString("D2") };
            }
        }

        [Theory]
        [MemberData(nameof(NormalIds))]
        public async Task 普通样本_真跑并落运行记录(string sampleId)
        {
            await RunAndCheckAsync(sampleId);
        }

        [Theory]
        [MemberData(nameof(SpecificIds))]
        public async Task 特定样本_真跑并落运行记录(string sampleId)
        {
            await RunAndCheckAsync(sampleId);
        }

        [Theory]
        [MemberData(nameof(AbnormalIds))]
        public async Task 异常样本_真跑并落运行记录(string sampleId)
        {
            await RunAndCheckAsync(sampleId);
        }

        [Theory]
        [MemberData(nameof(SpaceIds))]
        public async Task 空间样本_真跑并落运行记录(string sampleId)
        {
            await RunAndCheckAsync(sampleId);
        }

        private static async Task RunAndCheckAsync(string sampleId)
        {
            // 120 批要外部样本与几分钟，默认不跑；显式设 ARCHIVEFIXER_120_SEED 才跑。
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ARCHIVEFIXER_120_SEED"))) { return; }
            Batch120RunRecord record = await Batch120Operation.RunOneAsync(sampleId);

            Assert.Null(record.Error);
            Assert.NotEmpty(record.Tasks);
            Assert.True(record.ElapsedMs > 0);
            Assert.NotEmpty(record.After);
        }
    }
}
