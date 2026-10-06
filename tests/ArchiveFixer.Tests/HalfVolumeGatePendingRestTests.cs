using System;
using System.IO;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **半套分卷闸门**："成品目录里还留着的同组另一片"指的是**内容物**，
    /// ⛔ **不包括**那些已经收拢进"其余物"、正等着按档删掉的过程物（真机 CCCC 2026-10-06 22:22）。
    ///
    /// <para><b>现场</b>：入口包 `111.zip`（48.35 MB）在 `111\111\其余物` 里删不掉，外面被点名的
    /// `111.z01`、`111.rar` **全都躺在组层其余物里** —— 拿它们当"外面还留着一片"，
    /// 就把同一次收尾里的删除全拦死（批末重试 4 次全被同一句挡住）。</para>
    ///
    /// <para><b>红检</b>：把闸门里那条"已经在其余物里的跳过"撤掉 ⇒
    /// <c>其余物里的同组片_不算外面还留着一片_不许拦</c> 当场变红。</para>
    /// </summary>
    public class HalfVolumeGatePendingRestTests : IDisposable
    {
        private readonly string _root;

        public HalfVolumeGatePendingRestTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "af-half-volume-rest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
                // 临时目录清不掉不影响结论。
            }
        }

        /// <summary>⛔ 同组的那一片**已经在别的其余物里**（正等着删）⇒ 不算"外面还留着一片"，不许拦。</summary>
        [Fact]
        public void 其余物里的同组片_不算外面还留着一片_不许拦()
        {
            string layer = Path.Combine(_root, "111", "111");
            string rest = Path.Combine(layer, "其余物");
            string groupRest = Path.Combine(layer, "111", "其余物");

            Directory.CreateDirectory(rest);
            Directory.CreateDirectory(groupRest);

            File.WriteAllText(Path.Combine(rest, "111.zip"), "入口包（这一份要删）");
            File.WriteAllText(Path.Combine(groupRest, "111.z01"), "同组的一片（也已收拢待删）");
            File.WriteAllText(Path.Combine(groupRest, "111.rar"), "这一单自己的源包（已收拢待删）");

            Assert.Null(RestVolumeCompletenessGate.DescribeBlocker(rest));
        }

        /// <summary>⛔ **红线**：同组的那一片还在**内容物**里（不在任何其余物里）⇒ 必须拦。</summary>
        [Fact]
        public void 成品目录里的同组片_照旧一律拦()
        {
            string layer = Path.Combine(_root, "111", "111");
            string rest = Path.Combine(layer, "其余物");

            Directory.CreateDirectory(rest);
            File.WriteAllText(Path.Combine(rest, "111.zip"), "入口包");
            File.WriteAllText(Path.Combine(layer, "111.z01"), "同组的第一片，还在内容物里");

            string? blocker = RestVolumeCompletenessGate.DescribeBlocker(rest);

            Assert.NotNull(blocker);
            Assert.Contains("111.z01", blocker!, StringComparison.Ordinal);
        }
    }
}
