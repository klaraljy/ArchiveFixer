using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **日志的两份一比一**（用户 2026-10-11 大测试口径：「用户只会发日志，文件你是真的看不到，
    /// 也不可能做一步步的 debug，所以要尽量详细」）。
    ///
    /// <para>契约：人读的 <c>.log</c> 一行 ⇒ 机器读的 <c>.jsonl</c> 一行，字段固定
    /// （<c>ts/level/batch/round/sample/stage/task/layer/file/identity/criterion/inputs/verdict/action/msg</c>）；
    /// 判据走 <see cref="LogService.Criterion"/> 时那四个结构化字段各占一格（⛔ 不埋在散文里）。</para>
    ///
    /// <para>⛔ 本文件不改任何既有用例；它只钉"日志这一层"的契约。</para>
    /// </summary>
    public sealed class LogSchemaTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "ArchiveFixerLogSchema",
            Guid.NewGuid().ToString("N"));

        public LogSchemaTests()
        {
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
                // 清理失败不影响判据。
            }
        }

        /// <summary>人读行与机器读行**行数必须相等**（一比一；红检：把 JSONL 那一次写入注掉 ⇒ 本条当场红）。</summary>
        [Fact]
        public void 人读行与机器读行一比一()
        {
            LogService log = CreateLog(out string human, out string jsonl);

            log.WriteInfo("第一行");
            log.WriteWarning("第二行");
            log.WriteError("第三行");

            string[] humanLines = File.ReadAllLines(human);
            string[] jsonLines = File.ReadAllLines(jsonl);

            Assert.Equal(humanLines.Length, jsonLines.Length);

            foreach (string line in jsonLines)
            {
                using JsonDocument document = JsonDocument.Parse(line);

                Assert.True(document.RootElement.TryGetProperty("ts", out _));
                Assert.True(document.RootElement.TryGetProperty("level", out _));
                Assert.True(document.RootElement.TryGetProperty("msg", out _));
            }

            // 最后一行的人读文本与同一行 JSONL 里的 msg 是同一件事（⛔ 不是各写一套）。
            using JsonDocument last = JsonDocument.Parse(jsonLines[^1]);
            string message = last.RootElement.GetProperty("msg").GetString()!;

            Assert.Contains(message, humanLines[^1], StringComparison.Ordinal);
        }

        /// <summary>判据的四个字段各占一格（名字 / 输入 / 结论 / 动作），⛔ 不许只埋在散文里。</summary>
        [Fact]
        public void 判据四个字段各占一格()
        {
            LogService log = CreateLog(out string human, out string jsonl);

            log.Criterion(
                criterion: "接片那一档",
                inputs: "内容成员身份=ProvablyNotVolumeMember；名字线索=卷号1",
                verdict: "不接",
                action: "一个字节都不动",
                file: @"H:\…\111.rar",
                identity: "7A7EB341:700000000D22 7");

            string[] jsonLines = File.ReadAllLines(jsonl);
            using JsonDocument document = JsonDocument.Parse(jsonLines[^1]);
            JsonElement root = document.RootElement;

            Assert.Equal("接片那一档", root.GetProperty("criterion").GetString());
            Assert.Equal("不接", root.GetProperty("verdict").GetString());
            Assert.Equal("一个字节都不动", root.GetProperty("action").GetString());
            Assert.Contains("ProvablyNotVolumeMember", root.GetProperty("inputs").GetString()!, StringComparison.Ordinal);
            Assert.Contains("111.rar", root.GetProperty("file").GetString()!, StringComparison.Ordinal);

            // 人读那一行也要看得见判据名与结论。
            Assert.Contains("接片那一档", File.ReadAllText(human), StringComparison.Ordinal);
            Assert.Contains("结论：不接", File.ReadAllText(human), StringComparison.Ordinal);
        }

        /// <summary>上下文（批次/轮次/样本/阶段/任务/层）写进**每一行**——"只看日志"要能按轮次与样本筛。</summary>
        [Fact]
        public void 上下文写进每一行()
        {
            LogService log = CreateLog(out _, out string jsonl);

            log.UpdateRunContext(batch: "大测试", round: 3, sample: "3333", stage: "第二大步");
            log.WriteInfo("带上下文的一行");
            log.UpdateRunContext(task: "111.part1.rar", layer: 2);
            log.WriteInfo("换了任务与层");

            string[] lines = File.ReadAllLines(jsonl);

            // ⛔ 按**消息**找，不按行号（抬头的"日志初始化"等内部行会占位，行号会漂）。
            JsonElement contextLine = FindByMessage(lines, "带上下文的一行");
            JsonElement taskLine = FindByMessage(lines, "换了任务与层");

            Assert.Equal("大测试", contextLine.GetProperty("batch").GetString());
            Assert.Equal(3, contextLine.GetProperty("round").GetInt32());
            Assert.Equal("3333", contextLine.GetProperty("sample").GetString());
            Assert.Equal("第二大步", contextLine.GetProperty("stage").GetString());

            Assert.Equal("111.part1.rar", taskLine.GetProperty("task").GetString());
            Assert.Equal(2, taskLine.GetProperty("layer").GetInt32());

            // 没给的格子保持上一次的值（⛔ 不许被清成空）。
            Assert.Equal("大测试", taskLine.GetProperty("batch").GetString());
            Assert.Equal(3, taskLine.GetProperty("round").GetInt32());
        }

        /// <summary>按 <c>msg</c> 精确定位一条 JSONL 记录（行号会漂，消息不会）。</summary>
        private static JsonElement FindByMessage(IEnumerable<string> jsonLines, string message)
        {
            foreach (string line in jsonLines)
            {
                JsonDocument document = JsonDocument.Parse(line);

                if (string.Equals(
                        document.RootElement.GetProperty("msg").GetString(),
                        message,
                        StringComparison.Ordinal))
                {
                    return document.RootElement.Clone();
                }
            }

            throw new InvalidOperationException("JSONL 里没有这一条：" + message);
        }

        /// <summary>JSON 转义：引号 / 反斜杠 / 换行 / 制表 / 控制字符都要还原得回来（⛔ 换行不许漏进记录）。</summary>
        [Fact]
        public void 转义之后还能原样解回来()
        {
            const string nasty = "有\"引号\"、有\\反斜杠、有\n换行、有\t制表、还有\u0001控制字符";

            string escaped = LogService.EscapeJson(nasty);
            string roundTrip = JsonSerializer.Deserialize<string>("\"" + escaped + "\"")!;

            Assert.Equal(nasty, roundTrip);
            Assert.DoesNotContain("\n", escaped, StringComparison.Ordinal);
        }

        /// <summary>**密码明文永不进任何一份日志**（人读那一行照旧掩码；机器读那一行同理）。</summary>
        [Fact]
        public void 密码明文不进日志()
        {
            const string secret = "SuperSecret123!";

            LogService log = CreateLog(out string human, out string jsonl);

            log.WriteInfo($"开始解压，密码候选 1/9，尝试密码列表第 1 项：{secret}");
            log.WriteInfo("尝试手动密码第 2 项：******");

            string humanText = File.ReadAllText(human);
            string jsonText = File.ReadAllText(jsonl);

            Assert.DoesNotContain(secret, humanText, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, jsonText, StringComparison.Ordinal);
        }

        private LogService CreateLog(out string humanPath, out string jsonlPath)
        {
            var log = new LogService(new PathService { DataRootDirectory = _root });
            log.Initialize();
            log.WriteInfo("日志初始化（夹具）");

            humanPath = log.CurrentLogFilePath;
            jsonlPath = log.CurrentJsonlFilePath;

            Assert.True(File.Exists(humanPath), "人读日志没建出来：" + humanPath);
            Assert.True(File.Exists(jsonlPath), "机器读日志没建出来：" + jsonlPath);

            return log;
        }
    }
}
