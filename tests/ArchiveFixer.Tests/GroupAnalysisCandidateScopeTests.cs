using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 第二大步候选范围：只扫交接目录顶层，不能递归进已解出的内容物子树。
    /// </summary>
    public sealed class GroupAnalysisCandidateScopeTests
    {
        [Fact]
        public void 组分析_不递归扫描子目录里的同名片()
        {
            string root = Path.Combine(Path.GetTempPath(), "af-group-scope-" + System.Guid.NewGuid().ToString("N"));
            string source = Path.Combine(root, "src");
            string nested = Path.Combine(source, "out", "nested");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(nested);

            string seed = Path.Combine(source, "pack.part1.rar");
            string nestedPiece = Path.Combine(nested, "pack.part2.rar");

            try
            {
                File.WriteAllText(seed, "1");
                File.WriteAllText(nestedPiece, "2");

                List<GroupAnalysis.GroupCandidate> groups = GroupAnalysis.Analyze(
                    new[] { seed },
                    new[] { root });

                GroupAnalysis.GroupCandidate group = Assert.Single(groups);
                Assert.Single(group.Pieces);
                Assert.Equal(seed, group.Pieces[0]);
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                }
            }
        }
    }
}
