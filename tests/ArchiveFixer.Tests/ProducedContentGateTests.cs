using System;
using System.IO;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 「这一趟真解出来的东西够不够证明密码是对的」这把闸门（唯一出口
    /// <see cref="ProducedContentGate"/>）的守门用例。
    ///
    /// <para><b>为什么会有这一组</b>（2026-10-12 真机 `2222`）：RAR4 + 存储方式（<c>-m0</c>）+
    /// 数据加密的包上，**错密码**也会把垃圾明文按原尺寸写满盘 —— 只数"文件数与字节数"会把错密码
    /// 判成"密码已被这一趟证实"，于是候选循环当场停手、**正确密码（密码本第 7 位）永远没被试**，
    /// 那条续解链死在第 3 层、用户看到的是「文件损坏：密码已经证实是对的」。</para>
    ///
    /// <para>判据补成两条：老门槛（至少 2 个非空文件、字节数 &gt; 0）**且** 不是"每一条都失败"
    /// （引擎自报的失败条目数 / 点名失败的条目数 **≥** 盘上产出的文件数）。</para>
    /// </summary>
    public class ProducedContentGateTests : IDisposable
    {
        private readonly string _root;

        public ProducedContentGateTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "af-produced-gate-" + Guid.NewGuid().ToString("N"));
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
            }

            GC.SuppressFinalize(this);
        }

        private string MakeFiles(int count, int size)
        {
            for (int i = 0; i < count; i++)
            {
                File.WriteAllBytes(Path.Combine(_root, "f" + i + ".bin"), new byte[size]);
            }

            return _root;
        }

        /// <summary>
        /// **本条就是真机 `2222` 的逐字形状**：错密码那一趟盘上留下 2 个满尺寸文件、
        /// 引擎自报 2 个（并点名 2 个）失败条目 ⇒ ⛔ 不许判"密码已证实"。
        ///
        /// <para>数字出处（运行期实测，⛔ 不是读源码推的）：对本机造的
        /// `Rar.exe a -ma4 -m0 -p&lt;密码&gt;`（两个 4 MiB 文件）调
        /// <c>SevenZipEngine.ExtractAsync</c>，错密码那一次拿到
        /// <c>ExitCode=2 / DetectedErrorType=WrongPassword / ReportedSubItemErrors=2 /
        /// FailedEntryNames=[alpha.bin, beta.bin]</c>，盘上 2 个文件 / 8 388 608 字节 ——
        /// 与对密码那一次**逐个字节同尺寸**。</para>
        /// </summary>
        [Fact]
        public void 错密码写出满尺寸垃圾_失败条目数不少于产出数_不许判密码已证实()
        {
            string dir = MakeFiles(count: 2, size: 1024);

            Assert.False(ProducedContentGate.TryMeasure(
                dir,
                reportedSubItemErrors: 2,
                namedFailedEntries: 2,
                out int files,
                out long bytes));

            Assert.Equal(2, files);
            Assert.True(bytes > 0);
        }

        /// <summary>
        /// 反向对照（真机 `giu910` 那一档）：557 个文件里只坏了 1 个 ⇒ 密码**确实**已经被这一趟证实，
        /// 照旧判"已证实"（老行为一个字不变）。
        /// </summary>
        [Fact]
        public void 密码对_个别条目坏了_其余都解出来了_照旧判密码已证实()
        {
            string dir = MakeFiles(count: 5, size: 512);

            Assert.True(ProducedContentGate.TryMeasure(
                dir,
                reportedSubItemErrors: 1,
                namedFailedEntries: 1,
                out int files,
                out long bytes));

            Assert.Equal(5, files);
            Assert.Equal(5 * 512, bytes);
        }

        /// <summary>
        /// 只点名失败、没自报计数（中文版 UnRAR 那一类）时，**点名数**这一条也要能顶住：
        /// 产出 2 个、点名 2 个 ⇒ 每一条都失败。
        /// </summary>
        [Fact]
        public void 只有点名条目数可用时_每一条都被点名_也不许判密码已证实()
        {
            string dir = MakeFiles(count: 2, size: 256);

            Assert.False(ProducedContentGate.TryMeasure(
                dir,
                reportedSubItemErrors: 0,
                namedFailedEntries: 2,
                out _,
                out _));
        }

        /// <summary>
        /// 引擎事实一个都拿不到（别的引擎 / 老调用点 / 手写结果对象）⇒ **逐字退回老口径**：
        /// 文件数与字节数够就判"已证实"（⛔ 不许把"数不出来"变成新的拦截）。
        /// </summary>
        [Fact]
        public void 拿不到引擎事实_退回老口径()
        {
            string dir = MakeFiles(count: 2, size: 128);

            Assert.True(ProducedContentGate.TryMeasure(dir, out int files, out long bytes));
            Assert.Equal(2, files);
            Assert.Equal(256, bytes);
        }

        /// <summary>老门槛原样保留：一个文件 / 零字节桩 / 目录不在 ⇒ 一律"没证实"（继续试候选）。</summary>
        [Fact]
        public void 老门槛原样保留_一个文件与零字节桩都不算证实()
        {
            string one = MakeFiles(count: 1, size: 4096);
            Assert.False(ProducedContentGate.TryMeasure(one, out _, out _));

            string empty = Path.Combine(_root, "stubs");
            Directory.CreateDirectory(empty);
            File.WriteAllBytes(Path.Combine(empty, "a.bin"), Array.Empty<byte>());
            File.WriteAllBytes(Path.Combine(empty, "b.bin"), Array.Empty<byte>());

            Assert.False(ProducedContentGate.TryMeasure(
                empty,
                reportedSubItemErrors: 0,
                namedFailedEntries: 0,
                out int files,
                out long bytes));

            Assert.Equal(0, files);
            Assert.Equal(0, bytes);

            Assert.False(ProducedContentGate.TryMeasure(
                Path.Combine(_root, "不存在"),
                reportedSubItemErrors: 0,
                namedFailedEntries: 0,
                out _,
                out _));
        }
    }
}
