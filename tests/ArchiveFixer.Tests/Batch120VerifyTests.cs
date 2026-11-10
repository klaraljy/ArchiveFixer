using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests.Batch120
{
    /// <summary>
    /// 检测代码的入口：只读 recipe + 运行记录 + 磁盘，逐条判红绿。
    ///
    /// 一条样本红只影响它自己；每条结果同时落到
    /// <c>E:\AF\120批检测\917000000\&lt;样本&gt;.json</c>，方便汇总。
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public sealed class Batch120VerifyTests
    {
        private static readonly string ReportRoot = $@"E:\AF\120批检测\{Batch120Operation.Seed}";

        private readonly ITestOutputHelper _output;

        public Batch120VerifyTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Theory]
        [MemberData(nameof(Batch120OperationTests.NormalIds), MemberType = typeof(Batch120OperationTests))]
        public Task 普通样本_检测(string sampleId) => VerifyAndReportAsync(sampleId);

        [Theory]
        [MemberData(nameof(Batch120OperationTests.SpecificIds), MemberType = typeof(Batch120OperationTests))]
        public Task 特定样本_检测(string sampleId) => VerifyAndReportAsync(sampleId);

        [Theory]
        [MemberData(nameof(Batch120OperationTests.AbnormalIds), MemberType = typeof(Batch120OperationTests))]
        public Task 异常样本_检测(string sampleId) => VerifyAndReportAsync(sampleId);

        [Theory]
        [MemberData(nameof(Batch120OperationTests.SpaceIds), MemberType = typeof(Batch120OperationTests))]
        public Task 空间样本_检测(string sampleId) => VerifyAndReportAsync(sampleId);

        private Task VerifyAndReportAsync(string sampleId)
        {
            // 同操作代码：没显式设种子就跳过，避免污染全量。
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ARCHIVEFIXER_120_SEED"))) { return Task.CompletedTask; }
            List<CheckResult> results = Batch120Verify.Verify(sampleId);

            Directory.CreateDirectory(ReportRoot);
            File.WriteAllText(
                Path.Combine(ReportRoot, sampleId + ".json"),
                JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));

            CheckResult[] red = results.Where(r => r.Verdict == "Red").ToArray();
            foreach (CheckResult line in results)
            {
                _output.WriteLine($"{line.Verdict,-5} {line.CheckId,-3} 期望[{line.Expected}] 实际[{line.Actual}] {line.Note}");
            }

            Assert.True(red.Length == 0, sampleId + " 红项：" + string.Join(" || ", red.Select(r => $"{r.CheckId} 期望[{r.Expected}] 实际[{r.Actual}]")));
            return Task.CompletedTask;
        }
    }
}
