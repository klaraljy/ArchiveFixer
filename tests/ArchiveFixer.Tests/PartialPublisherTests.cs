using ArchiveFixer.Extraction;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **部分完成发布**（口径 A）：把逐条核对过的文件搬进 <c>&lt;包名&gt;\部分完成\</c>。
    ///
    /// <para>这一组钉的是**不可逆的那几步**（用户 2026-10-02 拍板的口径，见 `docs/部分完成发布方案.md` §6）：
    /// ⛔ 绝不覆盖任何已存在的文件（同名只让位）、⛔ 绝不越出「部分完成」这一层、
    /// ⛔ 绝不碰源包、搬不动就如实记一笔并**留给调用方**（没全搬成就不许清工作区）。</para>
    ///
    /// <para>用真临时目录 + 小文件（几 KB），够真、也够快。</para>
    /// </summary>
    public class PartialPublisherTests : IDisposable
    {
        private readonly string _root;
        private readonly ITestOutputHelper _output;

        public PartialPublisherTests(ITestOutputHelper output)
        {
            _output = output;
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerPartialPublish", Guid.NewGuid().ToString("N"));
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

        private static void WriteFile(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }

        [Fact]
        public void 按子目录结构搬进部分完成目录_别的东西一个都不碰()
        {
            string staging = Path.Combine(_root, "stage");
            string output = Path.Combine(_root, "out", "包名");

            WriteFile(Path.Combine(staging, "a.bin"), "AAA");
            WriteFile(Path.Combine(staging, "sub", "b.bin"), "BBB");
            WriteFile(Path.Combine(staging, "sub", "c.bin"), "CCC");

            // 计划里**没有**它 ⇒ 不许跟着一起搬（它可能是坏的那一个）
            WriteFile(Path.Combine(staging, "broken.bin"), "XXX");

            PartialPublisher.Result result = PartialPublisher.Publish(
                staging,
                new List<(string, long)> { ("a.bin", 3), ("sub/b.bin", 3), ("sub/c.bin", 3) },
                output);

            _output.WriteLine($"落点={result.DestinationDirectory} 搬了 {result.MovedCount} 个");

            Assert.True(result.Attempted);
            Assert.Equal(3, result.MovedCount);
            Assert.True(result.AllMoved);
            Assert.Empty(result.Failures);

            string partial = Path.Combine(output, PartialPublisher.DirectoryName);

            Assert.True(File.Exists(Path.Combine(partial, "a.bin")));
            Assert.True(File.Exists(Path.Combine(partial, "sub", "b.bin")));
            Assert.True(File.Exists(Path.Combine(partial, "sub", "c.bin")));

            // 计划外的那个留在暂存目录里（调用方按自己的口径处理它）
            Assert.True(File.Exists(Path.Combine(staging, "broken.bin")), "计划外的文件不许被搬走");
            Assert.False(File.Exists(Path.Combine(partial, "broken.bin")));

            // 搬走的那些已经不在暂存目录里了（是"搬"，不是"复制"）
            Assert.False(File.Exists(Path.Combine(staging, "a.bin")));
        }

        /// <summary>
        /// ⛔ **同名只让位、绝不覆盖**：目标里已经有上一次那半份的同名文件 ⇒ 新的那份改成 <c>a(1).bin</c>，
        /// 旧内容一个字都不许变。这条是"重跑同一份包"最要紧的一道。
        /// </summary>
        [Fact]
        public void 目标里已有同名文件_让位成括号1_旧内容不许变()
        {
            string staging = Path.Combine(_root, "stage2");
            string output = Path.Combine(_root, "out2", "包名");
            string partial = Path.Combine(output, PartialPublisher.DirectoryName);

            WriteFile(Path.Combine(partial, "a.bin"), "上一份的内容");
            WriteFile(Path.Combine(staging, "a.bin"), "这一份的内容");

            PartialPublisher.Result result = PartialPublisher.Publish(
                staging,
                new List<(string, long)> { ("a.bin", 6) },
                output);

            _output.WriteLine($"搬 {result.MovedCount} 个，让位 {result.RenamedCount} 个");

            Assert.Equal(1, result.MovedCount);
            Assert.Equal(1, result.RenamedCount);
            Assert.Equal("上一份的内容", File.ReadAllText(Path.Combine(partial, "a.bin")));
            Assert.Equal("这一份的内容", File.ReadAllText(Path.Combine(partial, "a(1).bin")));
        }

        /// <summary>计划里有、暂存目录里已经没了 ⇒ 记一笔、不搬、其余照搬（不许当成"搬成功"）。</summary>
        [Fact]
        public void 计划里的文件在暂存目录里没了_记一笔并让AllMoved为假()
        {
            string staging = Path.Combine(_root, "stage3");
            string output = Path.Combine(_root, "out3", "包名");

            WriteFile(Path.Combine(staging, "ok.bin"), "OK");

            PartialPublisher.Result result = PartialPublisher.Publish(
                staging,
                new List<(string, long)> { ("ok.bin", 2), ("missing.bin", 10) },
                output);

            _output.WriteLine(string.Join(" | ", result.Failures.Select(f => $"{f.Path}:{f.Reason}")));

            Assert.Equal(1, result.MovedCount);
            Assert.False(result.AllMoved, "有一个没搬成 ⇒ 不许说\"全搬完了\"（调用方据此不清工作区）");
            Assert.Single(result.Failures);
            Assert.Equal("missing.bin", result.Failures[0].Path);
        }

        /// <summary>⛔ 越界防护：计划里带 <c>..</c> / 绝对路径 / 盘符的相对路径一律拒搬（一个字节都不许写出去）。</summary>
        [Theory]
        [InlineData(@"..\..\evil.bin")]
        [InlineData(@"sub\..\..\evil.bin")]
        [InlineData(@"C:\evil.bin")]
        [InlineData(@"\\server\share\evil.bin")]
        public void 计划里的相对路径想往上跑_拒搬(string evil)
        {
            string staging = Path.Combine(_root, "stage4");

            /*
             * ⚠ 这里刻意**不**造源文件：越界闸门排在"源文件在不在"之前（路径不合法直接拒），
             * 而且 `Path.Combine(staging, "C:\\evil.bin")` 会把文件真的往 C 盘根上写 ——
             * 第一版用例就是这么写歪的（幸好没写进去）。⛔ 别在这里造路径。
             */
            Directory.CreateDirectory(staging);

            string output = Path.Combine(_root, "out4", "包名");

            PartialPublisher.Result result = PartialPublisher.Publish(
                staging,
                new List<(string, long)> { (evil, 4) },
                output);

            _output.WriteLine(string.Join(" | ", result.Failures.Select(f => $"{f.Path}：{f.Reason}")));

            Assert.Equal(0, result.MovedCount);
            Assert.Single(result.Failures);

            // 目标目录之外一个字节都没多出来
            Assert.False(File.Exists(Path.Combine(_root, "evil.bin")));
            Assert.False(File.Exists(Path.Combine(_root, "out4", "evil.bin")));
        }

        [Fact]
        public void 计划是空的_一个字节都不动()
        {
            string staging = Path.Combine(_root, "stage5");
            string output = Path.Combine(_root, "out5", "包名");

            WriteFile(Path.Combine(staging, "a.bin"), "A");

            PartialPublisher.Result result = PartialPublisher.Publish(
                staging,
                new List<(string, long)>(),
                output);

            Assert.False(result.Attempted);
            Assert.Equal(0, result.MovedCount);
            Assert.False(Directory.Exists(Path.Combine(output, PartialPublisher.DirectoryName)));
        }

        [Fact]
        public void 暂存目录不存在_一个字节都不动()
        {
            PartialPublisher.Result result = PartialPublisher.Publish(
                Path.Combine(_root, "not-exists"),
                new List<(string, long)> { ("a.bin", 1) },
                Path.Combine(_root, "out6", "包名"));

            Assert.False(result.Attempted);
            Assert.Equal(0, result.MovedCount);
        }

        /// <summary>落点建不出来（拿一个非法目录名）⇒ 一个字节都不搬、如实记一笔。</summary>
        [Fact]
        public void 落点建不出来_一个字节都不搬()
        {
            string staging = Path.Combine(_root, "stage7");
            WriteFile(Path.Combine(staging, "a.bin"), "A");

            // `: ` 在 Windows 路径里非法 ⇒ CreateDirectory 必失败
            PartialPublisher.Result result = PartialPublisher.Publish(
                staging,
                new List<(string, long)> { ("a.bin", 1) },
                Path.Combine(_root, "bad:*", "包名"));

            _output.WriteLine(string.Join(" | ", result.Failures.Select(f => f.Reason)));

            Assert.False(result.Attempted);
            Assert.Equal(0, result.MovedCount);
            Assert.True(File.Exists(Path.Combine(staging, "a.bin")), "搬不动时内容必须还在暂存目录里");
        }

        /// <summary>
        /// ⛔ 最后一道不覆盖：让位探测**说"没人占"**（探针与真实文件系统不一致，是真实存在的失败形态），
        /// 而落点上真的躺着一个文件 ⇒ 这一个**不搬**（宁可少发布一个，也不覆盖用户上次那半份）。
        /// </summary>
        [Fact]
        public void 探针说没人占但落点上真有文件_这一个不搬()
        {
            string staging = Path.Combine(_root, "stage8");
            string output = Path.Combine(_root, "out8", "包名");
            string partial = Path.Combine(output, PartialPublisher.DirectoryName);

            WriteFile(Path.Combine(staging, "a.bin"), "A");
            WriteFile(Path.Combine(partial, "a.bin"), "上一份");

            PartialPublisher.Result result = PartialPublisher.Publish(
                staging,
                new List<(string, long)> { ("a.bin", 1) },
                output,
                new AlwaysFreeProbe());

            _output.WriteLine(string.Join(" | ", result.Failures.Select(f => f.Reason)));

            Assert.Equal(0, result.MovedCount);
            Assert.Single(result.Failures);
            Assert.False(result.AllMoved);

            /*
             * ⚠ 判据必须落在**我们那道不覆盖闸门**给出的说法上：只断言"没搬成"是不够的 ——
             * 把闸门撤掉时 `File.Move` 自己也会因为"目标已存在"抛异常、照样记成一笔失败，
             * 断言照样绿（写这一版时就是这么假绿的）。钉住原因才钉得住那道闸门。
             */
            Assert.Contains("不覆盖", result.Failures[0].Reason, StringComparison.Ordinal);

            // 铁证：老内容一个字没变，新内容还在暂存目录里
            Assert.Equal("上一份", File.ReadAllText(Path.Combine(partial, "a.bin")));
            Assert.True(File.Exists(Path.Combine(staging, "a.bin")));
        }

        /// <summary>一直说"没人占"的探针（模拟探针失真 —— 最后一道真实文件系统检查必须兜住）。</summary>
        private sealed class AlwaysFreeProbe : IArtifactTargetProbe
        {
            public bool Exists(string path) => false;

            public bool? IsDirectory(string path) => false;
        }
    }
}
