using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **「判不出 ⇒ 什么都不做」的分支必须把判据输入写进日志**（用户 2026-10-10 目标单③）：
    /// 只拿一份用户日志，就要能看出"这一片被判成哪一族、第几卷、同层有哪些候选、为什么没选它"。
    ///
    /// <para><b>为什么非有不可</b>：那些分支原先只写**结论**（「还原伪装后缀没做」「判不出该怎么收」），
    /// 而**判据吃进去的东西**一个字都没有。上一轮真机 FFFF 就卡在这儿 ——
    /// 光看「同目录里没有找到像后续卷的文件」，分不清是尺子不认那个坏名字、还是那一片真的不在，
    /// 于是整轮排查只能靠再跑一遍加探针。</para>
    ///
    /// <para><b>红检</b>：把 <c>CriterionInputsRestoreFormat</c> 那一调注掉 ⇒ 本条当场变红
    /// （日志里只剩"没做"那半句，名字/族/卷号/候选一个都读不到）。</para>
    /// </summary>
    public class CriterionInputsLogTests : IDisposable
    {
        private readonly string _root;
        private readonly ITestOutputHelper _output;

        public CriterionInputsLogTests(ITestOutputHelper output)
        {
            _output = output;
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerCriterionInputs", Guid.NewGuid().ToString("N"));
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
                // 临时目录删不掉不影响结论。
            }
        }

        /// <summary>
        /// 真机 FFFF 的形状：一层里只躺着一片**改名改坏**的 RAR 新式分卷（`111_.par删t1.ra除r`）——
        /// 它自己配不出自洽的一组 ⇒ 「还原」这一档判不出 ⇒ **必须把判据输入写出来**。
        /// </summary>
        [Fact]
        public void 还原这一档判不出时_日志里要有名字族卷号与同层候选()
        {
            string piece = Path.Combine(_root, "111_.par删t1.ra除r");

            // 内容按 RAR5 魔数起头（判据只看魔数，不看后缀）。
            File.WriteAllBytes(piece, new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 }.Concat(new byte[512]).ToArray());

            var logs = new List<(string Level, string Text)>();

            VolumeNameRepair.RestoreDisguisedInnerPackageNames(
                new[] { piece },
                (level, text) => logs.Add((level, text)));

            foreach ((string level, string text) in logs)
            {
                _output.WriteLine($"[{level}] {text}");
            }

            // ① 结论那一行照旧在（用户看得懂的那句）。
            Assert.Contains(
                logs,
                entry => entry.Text.Contains(StatusText.InnerRestoreBlockedFormat.Split('：')[0], StringComparison.Ordinal));

            // ② 判据输入那一行也必须在，而且四个字段一个不少。
            (string Level, string Text) inputs = logs.SingleOrDefault(
                entry => entry.Text.Contains("判据输入", StringComparison.Ordinal));

            Assert.False(string.IsNullOrEmpty(inputs.Text), "日志里没有「判据输入」那一行：判不出时没把判据输入写出来。");
            Assert.Equal("WARN", inputs.Level);

            Assert.Contains("111_.par删t1.ra除r", inputs.Text, StringComparison.Ordinal);   // 这一片的名字
            Assert.Contains("同族第 1 卷=", inputs.Text, StringComparison.Ordinal);          // 判成哪一族
            Assert.Contains("卷号=", inputs.Text, StringComparison.Ordinal);                 // 第几卷
            Assert.Contains("同层候选", inputs.Text, StringComparison.Ordinal);              // 同层候选
            Assert.Contains("111_.par删t1.ra除r", inputs.Text, StringComparison.Ordinal);
        }
    }
}
