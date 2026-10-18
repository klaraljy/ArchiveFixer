using System.Collections.Generic;
using System.Linq;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **中间片顺序的高概率尝试**（用户 2026-10-18 口径）：通用 ZIP 只有末片能从内容确认，
    /// 其余中间片读不出卷号时按“与规范槽位名的后缀相似度”排高概率顺序，再按顺序试开；
    /// 片数上限 5、排列上限 24。这里只钉“顺序怎么生成/排序”，⛔ 不碰文件与引擎。
    /// </summary>
    public sealed class VolumeOrderTrialTests
    {
        [Fact]
        public void 两片等大中间片_名字像规范名的排在前面_末片固定在最后()
        {
            var pieces = new List<string>
            {
                @"C:\x\ccc-L2.zip.png",
                @"C:\x\ccc-L2.zip",
                @"C:\x\ccc-L2.zip.003"
            };

            IReadOnlyList<IReadOnlyList<string>> orders = VolumeOrderTrial.BuildCandidateOrders(
                pieces,
                @"C:\x\ccc-L2.zip.003",
                VolumeNamingFamily.ZipNumbered,
                "ccc-L2");

            Assert.Equal(2, orders.Count);
            Assert.All(orders, order => Assert.EndsWith(".003", order[^1], System.StringComparison.OrdinalIgnoreCase));
            Assert.EndsWith(".zip", orders[0][0], System.StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("png", orders[0][0], System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void 名字里读得出卷号时_高概率顺序排在第一位_另一种顺序仍保留()
        {
            var pieces = new List<string>
            {
                @"C:\x\aaa.7z.002",
                @"C:\x\aaa.7z.001"
            };

            IReadOnlyList<IReadOnlyList<string>> orders = VolumeOrderTrial.BuildCandidateOrders(
                pieces,
                tailPath: null,
                VolumeNamingFamily.SevenZipNumbered,
                "aaa.7z");

            Assert.Equal(2, orders.Count);
            Assert.EndsWith(".001", orders[0][0], System.StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".002", orders[0][1], System.StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".002", orders[1][0], System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void 片数超过用户口径的上限_不猜_返回空()
        {
            var pieces = Enumerable.Range(1, 6)
                .Select(index => $@"C:\x\bbb.zip.{index:D3}")
                .ToList();

            Assert.Empty(VolumeOrderTrial.BuildCandidateOrders(
                pieces,
                @"C:\x\bbb.zip.006",
                VolumeNamingFamily.ZipNumbered,
                "bbb"));
        }

        [Fact]
        public void 末片不在清单里_不硬塞_仍然按高概率给出顺序()
        {
            var pieces = new List<string>
            {
                @"C:\x\ddd.zip.png",
                @"C:\x\ddd.zip"
            };

            IReadOnlyList<IReadOnlyList<string>> orders = VolumeOrderTrial.BuildCandidateOrders(
                pieces,
                @"C:\x\ddd.zip.003",
                VolumeNamingFamily.ZipNumbered,
                "ddd");

            Assert.Equal(2, orders.Count);
            Assert.All(orders, order => Assert.Equal(2, order.Count));
        }
    }
}
