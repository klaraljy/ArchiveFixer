using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Engines.WinRar;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// "这台机器上有 <c>WinRAR.exe</c>" 用例的开关（换引擎兜底那一档要靠它）。
    ///
    /// <para>路径来自产品自己的唯一出口 <see cref="ToolLocator"/>（⛔ 不在测试里另写一套探测，
    /// 也⛔ 绝不复制 <c>WinRAR.exe</c>：它是共享软件，只检测、只调用）。没装就**在发现阶段跳过**
    /// 并说明原因（⛔ 不伪装成跑过，与 <c>RarExeFactAttribute</c> / <c>SevenZipFactAttribute</c> 同一套做法）。</para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class WinRarFactAttribute : FactAttribute
    {
        public WinRarFactAttribute()
        {
            if (string.IsNullOrEmpty(SevenZipFactAttribute.LocateSevenZipPath()))
            {
                Skip = "测试机上没有可用的 7z.exe（造不出 WinZip AES 夹具），跳过需要真 WinRAR 的用例。";

                return;
            }

            if (!ToolLocator.Default.WinRarExeExists)
            {
                Skip = "本机没找到 WinRAR.exe（程序只检测与调用你自己装的那一份，不随包分发）—— "
                     + "跳过「换引擎兜底」的端到端用例。";
            }
        }
    }

    /// <summary>
    /// **真样本**用例的开关（用户 2026-10-05 给的两个环境变量，两个都设了才跑）：
    /// <c>ARCHIVEFIXER_AESZIP_CASE_DIR</c> = 放样本的目录，<c>ARCHIVEFIXER_AESZIP_PASSWORD</c> = 那一条密码。
    ///
    /// <para>⛔ 样本本体绝不进仓库、绝不复制进仓库；密码**只从环境变量读**，绝不写进任何文件、注释或日志
    /// （§8 隐私红线）。样本 = 把那份"真视频 + 尾部 WinZip AES ZIP"改名成 <c>.zip</c> 的那一份
    /// （用户自己那条实测路径）；目录里没有 <c>.zip</c> ⇒ 发现阶段跳过并说明。</para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class RealAesZipCaseFactAttribute : FactAttribute
    {
        internal const string CaseDirectoryVariable = "ARCHIVEFIXER_AESZIP_CASE_DIR";
        internal const string PasswordVariable = "ARCHIVEFIXER_AESZIP_PASSWORD";

        public RealAesZipCaseFactAttribute()
        {
            if (string.IsNullOrEmpty(SevenZipFactAttribute.LocateSevenZipPath()))
            {
                Skip = "测试机上没有可用的 7z.exe，跳过真样本用例。";

                return;
            }

            if (!ToolLocator.Default.WinRarExeExists)
            {
                Skip = "本机没找到 WinRAR.exe，跳过真样本用例。";

                return;
            }

            string? directory = Environment.GetEnvironmentVariable(CaseDirectoryVariable);
            string? password = Environment.GetEnvironmentVariable(PasswordVariable);

            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(password))
            {
                Skip = $"真样本用例要同时设 {CaseDirectoryVariable} 与 {PasswordVariable}（缺一个就不跑）。";

                return;
            }

            if (FindZipSamples(directory!).Count != 1)
            {
                Skip = $"{CaseDirectoryVariable} 里要有**恰好一份** .zip 样本（那份改名成 .zip 的副本），"
                     + "当前不是恰好一份 —— 跳过（⛔ 不猜是哪一份）。";
            }
        }

        /// <summary>目录里那一份 <c>.zip</c> 样本（0 份 / 多份都返回实际个数，由构造阶段决定跳不跳）。</summary>
        internal static List<string> FindZipSamples(string directory)
        {
            try
            {
                return Directory.Exists(directory)
                    ? Directory.GetFiles(directory, "*.zip", SearchOption.TopDirectoryOnly).OrderBy(x => x, StringComparer.Ordinal).ToList()
                    : new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }
    }

    /// <summary>
    /// **换引擎再试一遍**那一档的守门用例（用户 2026-10-05 拍板"补"；真机现场见 AGENTS.md §11.5 最后一条）。
    ///
    /// <para>三层：
    /// ① **触发判据**（纯函数，不装 WinRAR 也能跑）——该不该兜底、没装 WinRAR 时该不该如实说清；
    /// ② **参数与映射**（也是纯的）——<c>x -cfg- -ibck -o+ -y -p&lt;密码&gt; 归档 目标目录\</c>、
    ///    空密码必须是 <c>-p-</c>、密码类退出码（实测 10 / 文档 11 / 数据错 3）继续试下一个；
    /// ③ **端到端接线**（要真 WinRAR）——主引擎是个"永远报密码错"的假引擎，WinRAR 真把产物解出来，
    ///    断言产物存在、大小与字节都对得上、并且**后面的正常管线照跑**（定稿 + 发布）。</para>
    /// </summary>
    public class WinRarFallbackTriggerTests
    {
        private static PasswordFallbackTriggerFacts Facts(
            bool passwordClass = true,
            bool encrypted = true,
            bool finished = true,
            bool available = true,
            bool succeeded = false,
            bool stopped = false,
            bool hasCandidates = true,
            bool mainEngineWroteBytes = false)
        {
            return new PasswordFallbackTriggerFacts
            {
                AlreadySucceeded = succeeded,
                PasswordClassFailure = passwordClass,
                ArchiveEncrypted = encrypted,
                CandidatesFinished = finished,
                UserAskedToStop = stopped,
                HasCandidates = hasCandidates,
                EngineAvailable = available,
                MainEngineWroteBytes = mainEngineWroteBytes
            };
        }

        [Fact]
        public void 密码类失败_加密_候选跑完_本机有WinRAR_该兜底()
        {
            Assert.True(PasswordEngineFallback.ShouldFallBack(Facts()));
            Assert.False(PasswordEngineFallback.ShouldReportMissingEngine(Facts()));
        }

        [Fact]
        public void 已经成功过_不兜底()
        {
            Assert.False(PasswordEngineFallback.ShouldFallBack(Facts(succeeded: true)));
        }

        [Fact]
        public void 失败不是密码类_不兜底()
        {
            // 例：引擎报"这不是归档" / "磁盘空间不足" / "权限不足" —— 换引擎解决不了这些。
            Assert.False(PasswordEngineFallback.ShouldFallBack(Facts(passwordClass: false)));
        }

        [Fact]
        public void 归档没加密_不兜底()
        {
            Assert.False(PasswordEngineFallback.ShouldFallBack(Facts(encrypted: false)));
        }

        [Fact]
        public void 候选循环没跑完_不兜底()
        {
            // 例：用户点了「停止后续」，或循环被别的停因提前收手 —— 那不是"候选全试完了"。
            Assert.False(PasswordEngineFallback.ShouldFallBack(Facts(finished: false)));
        }

        [Fact]
        public void 用户喊了停_不兜底()
        {
            Assert.False(PasswordEngineFallback.ShouldFallBack(Facts(stopped: true)));
        }

        [Fact]
        public void 手上一條候选都没有_不兜底()
        {
            Assert.False(PasswordEngineFallback.ShouldFallBack(Facts(hasCandidates: false)));
        }

        [Fact]
        public void 本机没装WinRAR_不兜底_但要如实报没换引擎再试()
        {
            PasswordFallbackTriggerFacts facts = Facts(available: false);

            Assert.False(PasswordEngineFallback.ShouldFallBack(facts));

            // ⛔ 不许静默跳过：判据成立但没引擎时，结论里必须写清"没找到 WinRAR，所以没换引擎再试"。
            Assert.True(PasswordEngineFallback.ShouldReportMissingEngine(facts));

            PasswordEngineFallbackOutcome outcome = PasswordEngineFallback.ReportMissingEngine("pack.zip", "7-Zip 命令行", null);

            Assert.False(outcome.Attempted);
            Assert.False(outcome.Succeeded);
            Assert.True(outcome.EngineMissing);
            Assert.Contains("没找到 WinRAR", outcome.Note, StringComparison.Ordinal);
            Assert.Contains("没换引擎再试", outcome.Note, StringComparison.Ordinal);
        }

        [Fact]
        public void 判据不成立时_也不该报没引擎()
        {
            // "没换引擎"这句只在**走到密码死胡同**时才说；普通失败（没加密 / 非密码类）一个字都不加。
            Assert.False(PasswordEngineFallback.ShouldReportMissingEngine(Facts(available: false, encrypted: false)));
            Assert.False(PasswordEngineFallback.ShouldReportMissingEngine(Facts(available: false, passwordClass: false)));
        }

        /// <summary>
        /// **主引擎已经写出过字节 ⇒ 不换引擎**（2026-10-05 实测逮到的假成功，这条守卫钉住它）。
        ///
        /// <para>现场：普通 <c>-p</c> 加密的 7z、候选全不对（真 7-Zip + 真 WinRAR，本机 6.11）。
        /// 7-Zip 报了 <c>CRC Failed in encrypted file. Wrong password?</c> 并在产物目录里留下 23 字节
        /// **解密出来的乱码**；而 WinRAR 用**同一个错密码**照样回**退出码 0**，还按原大小把乱码写进同一个目录
        /// （实测：文件名、大小、时间戳与真产物逐字一样）⇒ 老的"退出码 0 且产物目录多了东西"判据当场认命中，
        /// 整条链把乱码当产物发布、结论从「密码错误」翻成「解压成功」。</para>
        ///
        /// <para>判据（只读事实）：<b>主引擎写出过非零字节</b> = 它进得去这一份的数据流 ⇒ 两个引擎给出的产物
        /// 在现有判据下分辨不出"真解开了"与"写的是乱码" ⇒ 按「判不出 ⇒ 什么都不做」不换引擎、结论照旧；
        /// 反过来"主引擎一个字节都没写出来"（真机那一档）照旧允许换引擎。</para>
        /// </summary>
        [Fact]
        public void 主引擎已经写出过字节_不换引擎_且如实说明为什么()
        {
            // 前提对照：同样的判据，只是主引擎一个字节都没写出来 ⇒ 照旧该兜底（⛔ 不许把真机那一档一起挡掉）。
            Assert.True(PasswordEngineFallback.ShouldFallBack(Facts()));

            PasswordFallbackTriggerFacts wroteBytes = Facts(mainEngineWroteBytes: true);

            Assert.False(PasswordEngineFallback.ShouldFallBack(wroteBytes));

            /*
             * "证据分辨不出来"与"本机没装 WinRAR"是**两件事**：各自对应的事实与用户要做的动作都不同，
             * ⛔ 不许混成一句（混了会让用户以为装个 WinRAR 就能解决）。
             */
            Assert.False(PasswordEngineFallback.ShouldReportMissingEngine(wroteBytes));
            Assert.True(PasswordEngineFallback.ShouldReportIndistinguishableEvidence(wroteBytes));

            var logs = new List<string>();

            PasswordEngineFallback.ReportIndistinguishableEvidence(
                "pack.7z",
                "7-Zip 命令行",
                (level, message) => logs.Add($"[{level}] {message}"));

            // 如实说明：点名是哪个引擎、说清"它已经写出过字节"、并说清结论照旧（⛔ 不许静默跳过）。
            Assert.Contains(
                logs,
                line => line.Contains("7-Zip 命令行", StringComparison.Ordinal)
                        && line.Contains("写出过字节", StringComparison.Ordinal)
                        && line.Contains("结论照旧", StringComparison.Ordinal));

            // ⛔ 一个明文 / 一个候选名都不许出现（这一行只说事实）。
            Assert.DoesNotContain(logs, line => line.Contains("wrong-", StringComparison.Ordinal));

            // 判据本来就不成立时（没加密 / 非密码类）一个字都不加。
            Assert.False(PasswordEngineFallback.ShouldReportIndistinguishableEvidence(
                Facts(encrypted: false, mainEngineWroteBytes: true)));
            Assert.False(PasswordEngineFallback.ShouldReportIndistinguishableEvidence(
                Facts(passwordClass: false, mainEngineWroteBytes: true)));
        }
    }

    /// <summary>参数表、密码参数与退出码映射（纯函数，不需要真的起 WinRAR）。</summary>
    public class WinRarFallbackArgumentTests
    {
        [Fact]
        public void 参数表_按既有口径拼且不拼命令行字符串()
        {
            var runner = new WinRarProcessRunner();

            List<string> args = runner.BuildExtractArguments(@"C:\tmp\pack.zip", @"C:\tmp\out", "pw123");

            Assert.Equal(
                new[] { "x", "-cfg-", "-ibck", "-o+", "-y", "-ppw123", @"C:\tmp\pack.zip", @"C:\tmp\out\" },
                args);
        }

        [Fact]
        public void 目标目录必须带尾随分隔符()
        {
            var runner = new WinRarProcessRunner();

            // 不带尾随分隔符时 WinRAR 会把目标当成"新文件名"（实测报"无法打开 <目录>.rar"）。
            Assert.EndsWith(
                Path.DirectorySeparatorChar.ToString(),
                runner.BuildExtractArguments("a.zip", @"C:\tmp\out", "pw")[^1],
                StringComparison.Ordinal);

            Assert.EndsWith(
                Path.DirectorySeparatorChar.ToString(),
                runner.BuildExtractArguments("a.zip", @"C:\tmp\out\", "pw")[^1],
                StringComparison.Ordinal);
        }

        [Fact]
        public void 空密码必须写成_p减号_绝不裸_p()
        {
            // 实测：裸的 -p 会让 WinRAR 弹窗问密码并**永久等待**（进程挂住，只能杀掉）。
            Assert.Equal("-p-", WinRarProcessRunner.BuildPasswordArgument(null));
            Assert.Equal("-p-", WinRarProcessRunner.BuildPasswordArgument(string.Empty));
            Assert.Equal("-pabc", WinRarProcessRunner.BuildPasswordArgument("abc"));
        }

        [Fact]
        public void 必须带_cfg减号_不然本机那个删除压缩包设置会把源包删掉()
        {
            // 实测（本机 WinRAR 6.11）：不带 -cfg- 时，HKCU 里「解压后删除压缩包 = 总是」真的生效 ——
            // 解压成功、退出码 0、**源包消失**。那是不可逆的数据丢失，与不变量 1 直接冲突。
            var runner = new WinRarProcessRunner();

            Assert.Contains("-cfg-", runner.BuildExtractArguments("a.zip", "out", "pw"));
        }

        [Theory]
        [InlineData(3, true)]
        [InlineData(10, true)]
        [InlineData(11, true)]
        [InlineData(0, false)]
        [InlineData(1, false)]
        [InlineData(2, false)]
        [InlineData(255, false)]
        public void 哪些退出码算这个候选没成(int exitCode, bool expected)
        {
            Assert.Equal(expected, PasswordEngineFallback.IsRejectedCandidateExitCode(exitCode));
        }

        [Fact]
        public void 退出码0才算成功_10与11算密码错_1算别的失败()
        {
            var runner = new WinRarProcessRunner();

            Assert.True(runner.BuildResult(0, string.Empty, string.Empty, "pw", TimeSpan.Zero).Success);

            ArchiveOperationResult wrong = runner.BuildResult(10, string.Empty, string.Empty, "pw", TimeSpan.Zero);

            Assert.False(wrong.Success);
            Assert.Equal(EngineErrorTypes.WrongPassword, wrong.DetectedErrorType);
            Assert.Equal(StatusText.WrongPassword, wrong.Status);

            Assert.Equal(
                EngineErrorTypes.WrongPassword,
                runner.BuildResult(11, string.Empty, string.Empty, "pw", TimeSpan.Zero).DetectedErrorType);

            // ⛔ 退出码 1（实测：不是归档的文件）**不许**硬塞进"密码错误"那一档。
            Assert.Equal(
                EngineErrorTypes.UnknownError,
                runner.BuildResult(1, string.Empty, string.Empty, "pw", TimeSpan.Zero).DetectedErrorType);

            // 溯源（不变量 14）：结果上必须盖着"这是 WinRAR 干的活"。
            Assert.Equal(EngineIds.WinRarFallback, runner.BuildResult(0, string.Empty, string.Empty, "pw", TimeSpan.Zero).EngineId);
        }
    }

    /// <summary>候选被每层上限截断时"点名哪几条没试到"（用户 2026-10-05 真机第七批，要求 ①）。</summary>
    public class PasswordCandidateGapTests
    {
        private static List<PasswordItem> BuildCandidates(int count)
        {
            var items = new List<PasswordItem>();

            for (int index = 0; index < count; index++)
            {
                items.Add(new PasswordItem
                {
                    Value = $"secret-{index + 1}",
                    Source = "ImportedList",
                    IsEnabled = true
                });
            }

            return items;
        }

        [Fact]
        public void 被上限截断时_点名那几条没试到_且绝不出明文()
        {
            List<PasswordItem> candidates = BuildCandidates(12);

            // 上限 10 ⇒ 第 11、12 条从没被试过（真机那句"候选共 12 个、每层上限 10"的形状）。
            PasswordCandidateGap.Gap? gap = PasswordCandidateGap.Describe(
                candidates,
                10,
                11,
                (item, ordinal) => new PasswordService().BuildTryPasswordLogText(item, ordinal));

            Assert.True(gap.HasValue);
            Assert.Equal(2, gap!.Value.UntriedCount);
            Assert.Contains("尝试密码列表第 11 项", gap.Value.Named, StringComparison.Ordinal);
            Assert.Contains("尝试密码列表第 12 项", gap.Value.Named, StringComparison.Ordinal);

            // 只剩两条、没超过"最多点名三个"，所以既不折行、也不该出现"还有 K 条"。
            Assert.DoesNotContain("还有", gap.Value.Named, StringComparison.Ordinal);

            // ⛔ 一个明文都不许出现（描述器只输出占位符）。
            Assert.DoesNotContain("secret-", gap.Value.Named, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-11", gap.Value.Named, StringComparison.Ordinal);
        }

        [Fact]
        public void 超过三个时只列前三个_总数写在文案前半句()
        {
            PasswordCandidateGap.Gap? gap = PasswordCandidateGap.Describe(
                BuildCandidates(30),
                1,
                2,
                (item, ordinal) => new PasswordService().BuildTryPasswordLogText(item, ordinal));

            Assert.True(gap.HasValue);
            Assert.Equal(29, gap!.Value.UntriedCount);

            // 只列前三个（⛔ 点名后面不许再挂"还有 K 条"那种尾巴：脱敏器会因此整行擦掉）。
            // 从第 2 项（下标 1）开始数 ⇒ 点是第 2、3、4 项。
            Assert.Equal(2, gap.Value.Named.Count(character => character == '、'));
            Assert.EndsWith("尝试密码列表第 4 项：******", gap.Value.Named, StringComparison.Ordinal);
        }

        [Fact]
        public void 没有没试到的候选时_一个字都不写()
        {
            Assert.Null(PasswordCandidateGap.Describe(BuildCandidates(3), 3, 4, (item, ordinal) => "x"));
            Assert.Null(PasswordCandidateGap.Describe(new List<PasswordItem>(), 0, 1, (item, ordinal) => "x"));
            Assert.Null(PasswordCandidateGap.Describe(BuildCandidates(3), 0, 1, null));
        }

        [Fact]
        public void 收尾那行与结论那句都用既有文案常量()
        {
            PasswordCandidateGap.Gap gap = new(6, "尝试密码列表第 4 项：******、尝试密码列表第 5 项：******");

            string line = PasswordCandidateGap.BuildLogLine("pack.zip", 3, gap);

            Assert.Contains("每层上限 3", line, StringComparison.Ordinal);
            Assert.Contains("还有 6 条", line, StringComparison.Ordinal);
            Assert.Contains("尝试密码列表第 4 项", line, StringComparison.Ordinal);
            Assert.EndsWith("尝试密码列表第 5 项：******", line, StringComparison.Ordinal);
            // ⛔ 用户可见字符串里不许有 Markdown 的星号加粗（强调一律用「」）。
            Assert.DoesNotContain("**", line.Replace("******", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.DoesNotContain(
                "**",
                PasswordCandidateGap.BuildConclusionSuffix(gap).Replace("******", string.Empty, StringComparison.Ordinal),
                StringComparison.Ordinal);
        }

        [Fact]
        public void 点名那一行过完脱敏器之后每一条都还在()
        {
            /*
             * ⚠ 这条是"说话要对"的命门：脱敏器只认「描述：******」收尾的已知安全形状，
             * 一行里有好几条时必须整串都留得住 —— 否则真机那句"哪几条没试到"又只剩「尝试密码 ******」。
             */
            PasswordCandidateGap.Gap gap = new(
                6,
                "尝试密码列表第 4 项：******、尝试密码列表第 5 项：******、尝试密码列表第 6 项：******");

            string line = PasswordCandidateGap.BuildLogLine("pack.zip", 3, gap);
            string masked = PasswordMasker.Sanitize(line);

            Assert.Contains("尝试密码列表第 4 项", masked, StringComparison.Ordinal);
            Assert.Contains("尝试密码列表第 5 项", masked, StringComparison.Ordinal);
            Assert.Contains("尝试密码列表第 6 项", masked, StringComparison.Ordinal);
            Assert.DoesNotContain("尝试密码 ******", masked, StringComparison.Ordinal);
        }
    }

    /// <summary>WinRAR.exe 的定位（唯一出口 <see cref="ToolLocator"/>）。</summary>
    public class WinRarLocatorTests
    {
        [Fact]
        public void 关掉已装WinRAR那一档_就当这台机器上没有WinRAR()
        {
            // 与"模拟没装 UnRAR"同一套做法：关掉这一档 = 解析结果就是"没找到"，
            // ⛔ 绝不悄悄回落到一个存在的路径上（那样兜底会以为换得动引擎，真开跑才炸）。
            var tools = new ToolLocator { UseWinRarInstallation = false };

            Assert.False(tools.WinRarExeExists);
            Assert.Null(tools.WinRarExePath);
            Assert.Contains("未找到 WinRAR", tools.DescribeWinRarResolution(), StringComparison.Ordinal);
        }

        [Fact]
        public void 候选里包含注册表那两档与ProgramFiles那两档()
        {
            var tools = new ToolLocator();

            IReadOnlyList<string> candidates = tools.WinRarGuiInstallationCandidates();

            Assert.All(candidates, candidate => Assert.EndsWith("WinRAR.exe", candidate, StringComparison.OrdinalIgnoreCase));

            // 本机装过 WinRAR 时（真机就是）至少要能找到一个。
            if (tools.WinRarExeExists)
            {
                Assert.Contains(tools.WinRarExePath!, candidates, StringComparer.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void 自选那一格指向Rar点exe时_不算WinRAR点exe()
        {
            // CustomRarExePath 是"打包用的控制台版"，而这条路要的是 GUI 版（只有它认 -ibck）。
            var tools = new ToolLocator { UseWinRarInstallation = false };

            string rarPath = Path.Combine(Path.GetTempPath(), "Rar.exe");
            tools.CustomRarExePath = rarPath;

            Assert.Null(tools.WinRarExePath);
        }
    }

    /// <summary>
    /// **端到端接线**：主引擎是个"永远报密码错"的假引擎，WinRAR 真把产物解出来
    /// （用户 2026-10-05 的那条验收路径）。
    ///
    /// <para>夹具是**真的 WinZip AES（压缩法 99）ZIP**（内置 7z 用 <c>-mem=AES256</c> 造的），
    /// 与真机那一份同一种形状；密码是本用例现造的占位值（⛔ 与用户的密码本无关）。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class WinRarFallbackSingleLayerEndToEndTests : IDisposable
    {
        private const string FixturePassword = "ArchiveFixer-AES-Probe-2026";

        private readonly string _root;

        public WinRarFallbackSingleLayerEndToEndTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerWinRarFallback", Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响测试结论。
            }
        }

        [WinRarFact]
        public async Task 单层路_七Zip报密码错_换WinRAR把同一批候选再试一遍_产物落盘且继续走正常管线()
        {
            WinRarFixture fixture = WinRarFixture.Create(_root, FixturePassword);
            Harness harness = CreateHarness();

            string source = Path.Combine(_root, "src", "pack.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.Copy(fixture.ArchivePath, source);

            ArchiveTask task = harness.AddTask(source);

            // 候选表里就那一条真密码：假引擎每次都报密码错，于是候选循环跑完 ⇒ 该换引擎了。
            harness.SetPasswordCandidates(FixturePassword);

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            // ① 结论必须从「密码错误」翻成「解压成功」（这就是这次要修的那条假结论）。
            Assert.True(
                task.Status == StatusText.ExtractSuccess,
                $"任务应当靠 WinRAR 兜底成功，实际状态：{task.Status} / {task.ErrorMessage}"
                + Environment.NewLine + "日志："
                + string.Join(Environment.NewLine, harness.Log.Logs.Select(x => x.Message)));
            Assert.Equal(StatusText.PasswordCorrect, task.PasswordStatus);
            Assert.Equal(TaskOutcome.Succeeded, task.Outcome);

            // ② 假引擎一次都没解开过（它每次都报密码错），产物只可能来自 WinRAR。
            Assert.All(harness.Engine.Extracted, password => Assert.Equal(FixturePassword, password));
            Assert.True(harness.Engine.Extracted.Count > 0, "候选循环必须先真的试过一次（否则测的不是「换引擎再试」）");

            // ③ 产物存在、大小对、字节逐字节相同（⛔ 不许只看退出码）。
            //    失败时把日志一起带出来：这一档出问题要能一眼看出产物去了哪 / 被谁清了。
            try
            {
                WinRarFixture.AssertProductsPublished(task.OutputPath, fixture);
            }
            catch (Exception ex)
            {
                Assert.Fail(
                    ex.Message + Environment.NewLine + "日志："
                    + string.Join(Environment.NewLine, harness.Log.Logs.Select(x => x.Message)));
            }

            // ④ 日志点名了两个引擎（"谁报的密码错" + "已换 WinRAR 再试一遍"），
            //    而且**命中那一行也必须留住引擎名与版本**（脱敏器不许把它擦掉）。
            Assert.Contains(
                harness.Log.Logs,
                x => x.Message.Contains("WinRAR", StringComparison.Ordinal)
                     && x.Message.Contains("已换", StringComparison.Ordinal));
            Assert.Contains(
                harness.Log.Logs,
                x => x.Message.Contains("WinRAR 兜底成功", StringComparison.Ordinal)
                     && x.Message.Contains("用 WinRAR", StringComparison.Ordinal));

            // ⑤ 源包原地不动（不变量 1：WinRAR 那条"解压后删除压缩包"的设置必须被 -cfg- 挡住）。
            Assert.True(File.Exists(source), "兜底解压之后源包必须还在原地");

            // ⑥ 后面的正常管线真的跑完了（收尾那一段也跑了：EndTime / 进度文案）。
            Assert.NotNull(task.EndTime);
            Assert.Equal(StatusText.ProgressCompleted, task.ProgressText);
            Assert.Equal(0, outcome.ContinuationLayers);
        }

        [WinRarFact]
        public async Task 单层路_候选在WinRAR上也不对时_结论照旧且如实说清换过引擎()
        {
            WinRarFixture fixture = WinRarFixture.Create(_root, FixturePassword);
            Harness harness = CreateHarness();

            string source = Path.Combine(_root, "src-wrongpw", "pack.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.Copy(fixture.ArchivePath, source);

            ArchiveTask task = harness.AddTask(source);

            // 候选表里**没有**那条真密码：WinRAR 也解不开 ⇒ 结论必须照旧（⛔ 不许改成"密码对"）。
            harness.SetPasswordCandidates("wrong-one", "wrong-two");

            await harness.RunOneClickAsync();

            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);
            Assert.Contains("密码", task.Status, StringComparison.Ordinal);

            // 但必须如实说清"已经换 WinRAR 再试过一遍"。
            Assert.Contains("已换 WinRAR", task.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains(harness.Log.Logs, x => x.Message.Contains("仍然没解开", StringComparison.Ordinal));

            Assert.True(File.Exists(source), "兜底失败时源包必须还在原地");
        }

        [WinRarFact]
        public async Task 单层路_候选被每层上限截断_收尾必须点名哪几条没试到()
        {
            WinRarFixture fixture = WinRarFixture.Create(_root, FixturePassword);
            Harness harness = CreateHarness(settings => settings.MaxPasswordAttemptsPerLayer = 3);

            string source = Path.Combine(_root, "src-truncated", "pack.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.Copy(fixture.ArchivePath, source);

            ArchiveTask task = harness.AddTask(source);

            // 12 条候选、没有一条是对的；每层上限 3 ⇒ 第 4 条起**从没被试过**（真机那句"第 11 条从没被试过"的形状）。
            harness.SetPasswordCandidates(
                Enumerable.Range(1, 12).Select(index => $"wrong-{index:D2}").ToArray());

            await harness.RunOneClickAsync();

            // ① 结论必须是「达到密码尝试上限」（⛔ 不是"密码错误"）。
            Assert.Equal(StatusText.PasswordAttemptLimitReached, task.Status);

            // ② 收尾那行必须**点名**哪几条没试到（用既有描述器）。
            Assert.True(
                task.ErrorMessage.Contains("尝试密码列表第 4 项", StringComparison.Ordinal),
                $"没点名。ErrorMessage=「{task.ErrorMessage}」；相关日志："
                + string.Join(
                    " ｜ ",
                    harness.Log.Logs
                        .Select(x => x.Message)
                        .Where(message => message.Contains("没试到", StringComparison.Ordinal)
                                          || message.Contains("上限", StringComparison.Ordinal))));

            Assert.Contains("还有 9 条", task.ErrorMessage, StringComparison.Ordinal);

            // ③ ⛔ 一个明文都不许出现（点名只走描述器）。
            Assert.DoesNotContain("wrong-", task.ErrorMessage, StringComparison.Ordinal);

            // ④ 日志那一行要说清是"每层上限卡住的"，并给出去④页调上限 / 调顺序的出口。
            Assert.Contains(
                harness.Log.Logs,
                x => x.Message.Contains("每层上限 3", StringComparison.Ordinal)
                     && x.Message.Contains("没试到", StringComparison.Ordinal));

            // ⑤ 换引擎兜底照旧跑过（它试的是同一批 3 个候选），结论照旧、但要如实说清。
            Assert.Contains("已换 WinRAR", task.ErrorMessage, StringComparison.Ordinal);
        }

        /// <summary>
        /// **兜底那一句"命中"只说明 WinRAR 退出码 0 且产物目录里多出了东西 —— 不说明那些东西就是这一层的产物。**
        ///
        /// <para>所以收尾必须与候选循环里"引擎说成功也得先过校验"那一支**同一处置**：产物校验判否 ⇒
        /// **这个候选不算数** —— ⛔ 不落「解压成功」、⛔ 不定稿发布、⛔ 不改结论（结论照旧是密码类那条）。</para>
        ///
        /// <para>怎么造出"兜底解出来了、但产物过不了校验"：假引擎的**清单故意把两个条目报大**
        /// （预期 &gt; 实际 ⇒ <c>OutputVerifier</c> 的 <c>actual &gt;= expected</c> 判否），
        /// 而 WinRAR 真能把这份夹具解开 ⇒ 兜底命中、产物真落在暂存目录里，只是对不上这一层的清单。</para>
        ///
        /// <para><b>红检</b>：把 <c>FinishFallbackSuccessAsync</c> 里那道
        /// <c>stage.Verification.Verified</c> 闸门撤掉（照老口径先落成功、再发布）⇒ 本用例变红
        /// （任务落「解压成功」、产物被搬进目标目录）。</para>
        /// </summary>
        [WinRarFact]
        public async Task 单层路_兜底解出来的产物过不了校验_不算命中_不发布_结论照旧()
        {
            WinRarFixture fixture = WinRarFixture.Create(_root, FixturePassword);
            Harness harness = CreateHarness();

            string source = Path.Combine(_root, "src-mismatch", "pack.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.Copy(fixture.ArchivePath, source);

            ArchiveTask task = harness.AddTask(source);

            /*
             * 清单：条目名与夹具一致（产物找得到），但**大小故意报大** ⇒ 预期 > 实际 ⇒ 校验判否。
             * 总量刻意压在 64 MiB 以下：那是"密码探针"的门槛，这条用例不验探针那一档。
             */
            long inflatedSize = 8L * 1024 * 1024;

            harness.Engine.ListHandler = _ => new ArchiveListResult
            {
                Success = true,
                Entries = fixture.Entries
                    .Select(entry => new ArchiveEntry
                    {
                        Path = "payload\\" + entry.RelativePath.Replace('/', '\\'),
                        Size = inflatedSize
                    })
                    .ToList(),
                FileCount = fixture.Entries.Count,
                TotalUncompressedSize = inflatedSize * fixture.Entries.Count,
                IsEncrypted = true,
                EngineId = EngineIds.SevenZip,
                EngineVersion = "0.0"
            };

            harness.SetPasswordCandidates(FixturePassword);

            await harness.RunOneClickAsync();

            // ① 结论照旧是密码类：⛔ 不许被"WinRAR 命中了"翻成「解压成功」（不变量 6）。
            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);
            Assert.Contains("密码", task.Status, StringComparison.Ordinal);

            // ② 一个字节都不许发布到目标目录（没定稿、没搬出去）。
            string outputPath = task.OutputPath ?? string.Empty;

            Assert.False(
                Directory.Exists(outputPath) && Directory.EnumerateFileSystemEntries(outputPath).Any(),
                $"兜底产物过不了校验，就不该出现在目标目录里：{outputPath}");

            // ③ 源包原地不动（不变量 1）。
            Assert.True(File.Exists(source), "兜底没算数时源包必须还在原地");
        }

        /// <summary>
        /// **单层路同一个判据**：主引擎在这一层的候选上**写出过非零字节** ⇒ ⛔ 不换引擎再试
        /// （两个引擎给不出可分辨的证据），结论照旧、但必须如实说明为什么（⛔ 不许静默跳过）。
        ///
        /// <para>假引擎在报"密码错"之前先往暂存目录里写 23 字节（真 7-Zip 用错密码解 stored 的包时
        /// 就是这么干的）；夹具是一份 WinRAR 真解得开的包 ⇒ 闸门一旦失效，兜底当场"命中"、
        /// 结论翻成「解压成功」、乱码被发布出去。</para>
        ///
        /// <para><b>红检</b>：把候选循环里那三处 <c>anyCandidateWroteBytes |= …</c> 去掉（恒 false）⇒
        /// 本用例变红（日志里出现「已换 WinRAR」、结论不再是密码类）。</para>
        /// </summary>
        [WinRarFact]
        public async Task 单层路_主引擎写出过字节_不换引擎_结论照旧且如实说明()
        {
            WinRarFixture fixture = WinRarFixture.Create(_root, FixturePassword);
            Harness harness = CreateHarness();

            string source = Path.Combine(_root, "src-wrote-bytes", "pack.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.Copy(fixture.ArchivePath, source);

            ArchiveTask task = harness.AddTask(source);

            harness.Engine.ListHandler = _ => WinRarFixture.CurrentListing;

            harness.Engine.BeforeWrong = outputPath =>
            {
                if (!string.IsNullOrWhiteSpace(outputPath))
                {
                    Directory.CreateDirectory(outputPath);
                    File.WriteAllBytes(Path.Combine(outputPath, "payload.bin"), new byte[23]);
                }
            };

            // 候选表里就是那条真密码：没有这道闸门时 WinRAR 一定能解开（= 假命中）。
            harness.SetPasswordCandidates(FixturePassword);

            await harness.RunOneClickAsync();

            // ① 结论照旧是密码类（⛔ 不许被"WinRAR 命中了"翻成「解压成功」）。
            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);
            Assert.Contains("密码", task.Status, StringComparison.Ordinal);

            // ② 兜底一次都不许跑（⛔ 证据分辨不出来时，一个进程都不起）。
            Assert.DoesNotContain(
                harness.Log.Logs,
                x => x.Message.Contains("已换", StringComparison.Ordinal)
                     || x.Message.Contains("WinRAR 兜底", StringComparison.Ordinal));

            // ③ 但必须如实说明"为什么没换引擎"（⛔ 不许静默跳过）。
            Assert.Contains(
                harness.Log.Logs,
                x => x.Message.Contains("写出过字节", StringComparison.Ordinal)
                     && x.Message.Contains("结论照旧", StringComparison.Ordinal));

            // ④ 源包原地不动（不变量 1）。
            Assert.True(File.Exists(source), "没换引擎时源包必须还在原地");
        }

        private Harness CreateHarness(Action<AppSettings>? configure = null)
        {
            string dataRoot = Path.Combine(_root, "data");
            string outputRoot = Path.Combine(_root, "out");

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CustomOutputDirectory = outputRoot;
            settings.ExtractToOriginalDirectory = false;
            settings.KeepArchiveNameFolder = true;
            settings.RecursionMode = "SingleLayer";
            settings.AutoScanAfterDrop = false;
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.RestHandlingAfterVerify = RestHandlingModes.Keep;

            configure?.Invoke(settings);

            settingsService.Save(settings);

            var engine = new AlwaysWrongPasswordEngine();

            var passwordService = new PasswordService();
            var logService = new LogService(pathService);

            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;

            var vm = new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                engine,
                passwordService,
                logService,
                settingsService,
                pathService,
                new TaskSummaryService(),
                new ClipboardService(),
                new DialogService());

            RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;

            var coordinator = new ExtractionCoordinator(vm, engine, passwordService, pathService, new DialogService());
            coordinator.KeepTaskDetailInLog = true;

            var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), new DialogService());
            var rename = new RenameCoordinator(vm, scan, new RenameService(), new DialogService());
            var oneClick = new OneClickCoordinator(vm, scan, rename, coordinator, new DialogService());

            return new Harness(vm, engine, passwordService, coordinator, oneClick, logService, outputRoot);
        }

        private sealed class Harness
        {
            public Harness(
                MainViewModel vm,
                AlwaysWrongPasswordEngine engine,
                PasswordService passwordService,
                ExtractionCoordinator coordinator,
                OneClickCoordinator oneClick,
                LogService log,
                string outputRoot)
            {
                Vm = vm;
                Engine = engine;
                PasswordService = passwordService;
                Coordinator = coordinator;
                OneClick = oneClick;
                Log = log;
                OutputRoot = outputRoot;
            }

            public MainViewModel Vm { get; }

            public AlwaysWrongPasswordEngine Engine { get; }

            public PasswordService PasswordService { get; }

            public ExtractionCoordinator Coordinator { get; }

            public OneClickCoordinator OneClick { get; }

            public LogService Log { get; }

            public string OutputRoot { get; }

            public ArchiveTask AddTask(string sourcePath)
            {
                var task = new ArchiveTask(sourcePath, Vm.Tasks.Count + 1)
                {
                    IsArchive = true,
                    DetectedFormat = "ZIP",
                    IsEncrypted = true,
                    ExtensionStatus = StatusText.ExtensionNormal,
                    Status = StatusText.Recognized,
                    IsSelected = true
                };

                task.CaptureSourceSnapshot();
                Vm.Tasks.Add(task);

                return task;
            }

            /// <summary>喂候选（走真的 PasswordService，候选顺序与真实运行一致）。</summary>
            public void SetPasswordCandidates(params string[] values)
            {
                PasswordService.ClearPasswords();

                foreach (string value in values)
                {
                    PasswordService.AddPassword(value);
                }
            }

            public Task<OneClickOutcome> RunOneClickAsync() => OneClick.RunPipelineAsync(Vm.Tasks.ToList());
        }

        /// <summary>
        /// 假主引擎：**永远报密码错**（真机那条缺陷的形状 —— 引擎吃不下这一份，说出来的却是"密码不对"）。
        /// 默认产物一个字节都不写（所以盘上的东西只可能是 WinRAR 解的）；
        /// 需要"引擎写出过字节"那一档时用 <see cref="BeforeWrong"/> 在报错前留下东西。
        /// </summary>
        private sealed class AlwaysWrongPasswordEngine : IArchiveEngine
        {
            public AlwaysWrongPasswordEngine()
            {
                ListHandler = _ => null;
            }

            /// <summary>列目录的答案（不给就用夹具自己的清单：名字 + 大小必须与真产物对得上，校验才过）。</summary>
            public Func<ArchiveRequest, ArchiveListResult?> ListHandler { get; set; }

            /// <summary>
            /// 报"密码错"**之前**做点什么（可空）。用途：造出"引擎在这一份上写出过字节"那一档
            /// （真 7-Zip 用错密码解 stored 的包时就是这样：先把解密出来的乱码按原大小写进产物目录，再报错）。
            /// </summary>
            public Action<string?>? BeforeWrong { get; set; }

            /// <summary>每次解压用的密码（断言"候选循环真的试过"靠它）。</summary>
            public List<string> Extracted { get; } = new();

            public string Id => EngineIds.SevenZip;

            public string DisplayName => "7-Zip 命令行";

            public string Version => "0.0";

            public bool IsAvailable => true;

            public EngineCapabilities Capabilities { get; } = new()
            {
                CanProbe = true,
                CanList = true,
                CanTest = true,
                CanExtract = true,
                SupportsPassword = true
            };

            public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new ArchiveProbeResult { IsArchive = true, Format = "ZIP", IsEncrypted = true });

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                ArchiveListResult? result = ListHandler(request) ?? WinRarFixture.CurrentListing;

                return Task.FromResult(result ?? ArchiveListResult.Failure(
                    EngineErrorTypes.UnknownError,
                    "假引擎还没有拿到夹具清单",
                    EngineIds.SevenZip,
                    "0.0"));
            }

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(Wrong());

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                Extracted.Add(request.Password ?? string.Empty);

                BeforeWrong?.Invoke(request.OutputPath);

                return Task.FromResult(Wrong());
            }

            private static ArchiveOperationResult Wrong() => new()
            {
                Success = false,
                ExitCode = 2,
                Status = StatusText.WrongPassword,
                Message = "Cannot open encrypted archive. Wrong password?",
                DetectedErrorType = EngineErrorTypes.WrongPassword,
                EngineId = EngineIds.SevenZip,
                EngineDisplayName = "7-Zip 命令行",
                EngineVersion = "0.0"
            };
        }
    }

    /// <summary>
    /// **递归路**的端到端接线（用户 2026-10-05 拍板"补"的那两条路之一）：
    /// 假引擎永远报密码错，WinRAR 把这一层解开 ⇒ 整条链跑完、产物发布出去。
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class WinRarFallbackRecursiveEndToEndTests : IDisposable
    {
        private const string FixturePassword = "ArchiveFixer-AES-Probe-2026";

        private readonly string _root;

        public WinRarFallbackRecursiveEndToEndTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerWinRarFallbackRec", Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响测试结论。
            }
        }

        [WinRarFact]
        public async Task 递归路_七Zip报密码错_换WinRAR解开这一层_整条链跑完且产物发布()
        {
            WinRarFixture fixture = WinRarFixture.Create(_root, FixturePassword);

            string source = Path.Combine(_root, "src", "pack.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.Copy(fixture.ArchivePath, source);

            var engine = new RecursionFixHarness.ScriptedEngine
            {
                OnList = _ => WinRarFixture.CurrentListing!,
                OnExtract = _ => RecursionFixHarness.WrongPassword()
            };

            RecursiveExtractor extractor = RecursionFixHarness.CreateExtractor(
                engine,
                _ => new[] { string.Empty, FixturePassword },
                null,
                out List<string> logs);

            string output = Path.Combine(_root, "out");

            RecursionResult result = await extractor.ExtractAsync(
                new ArchiveTask(source),
                output,
                RecursionMode.AllBranches,
                null,
                CancellationToken.None);

            Assert.True(
                result.Completed,
                $"整条链应当跑完，实际停因：{result.StopReason} / {result.Summary}"
                + Environment.NewLine + "日志：" + string.Join(Environment.NewLine, logs));
            Assert.Equal(RecursionStopReason.Completed, result.StopReason);

            // 产物必须真在盘上（大小 + 字节逐字节）。
            WinRarFixture.AssertProductsPublished(result.FinalOutputPath, fixture);

            // 两份日志都必须点名 WinRAR（"谁报的密码错" + "换它再试了一遍"）。
            Assert.Contains(logs, line => line.Contains("WinRAR", StringComparison.Ordinal)
                                          && line.Contains("已换", StringComparison.Ordinal));
            Assert.Contains(logs, line => line.Contains("WinRAR 兜底成功", StringComparison.Ordinal));

            // 源包原地不动（不变量 1）。
            Assert.True(File.Exists(source), "兜底解压之后源包必须还在原地");
        }

        [WinRarFact]
        public async Task 递归路_候选被每层上限截断_收尾必须点名哪几条没试到()
        {
            WinRarFixture fixture = WinRarFixture.Create(_root, FixturePassword);

            string source = Path.Combine(_root, "src-truncated", "pack.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.Copy(fixture.ArchivePath, source);

            var engine = new RecursionFixHarness.ScriptedEngine
            {
                OnList = _ => WinRarFixture.CurrentListing!,
                OnExtract = _ => RecursionFixHarness.WrongPassword()
            };

            RecursiveExtractor extractor = RecursionFixHarness.CreateExtractor(
                engine,
                _ => new[] { "wrong-01", "wrong-02", "wrong-03", "wrong-04" },
                new RecursionLimits { MaxPasswordAttemptsPerLayer = 2 },
                out List<string> logs);

            RecursionResult result = await extractor.ExtractAsync(
                new ArchiveTask(source),
                Path.Combine(_root, "out-truncated"),
                RecursionMode.AllBranches,
                null,
                CancellationToken.None);

            // ① 停因是「达到密码尝试上限」（⛔ 不是"密码错误"：候选根本没试完）。
            Assert.False(result.Completed);
            Assert.Equal(RecursionStopReason.PasswordAttemptsExceeded, result.StopReason);

            // ② 收尾那一行点名哪几条没试到（第 3、第 4 条）。
            Assert.Contains(
                logs,
                line => line.Contains("每层上限 2", StringComparison.Ordinal)
                        && line.Contains("没试到", StringComparison.Ordinal)
                        && line.Contains("第 3 项", StringComparison.Ordinal));

            // ③ 结论里也有这一句（①页详情 / 失败清单看得到），而且不出明文。
            Assert.Contains("没试到", result.Summary, StringComparison.Ordinal);
            Assert.Contains("第 3 项", result.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain("wrong-", result.Summary, StringComparison.Ordinal);

            // ④ 换引擎兜底照旧跑过，如实说清。
            Assert.Contains("已换 WinRAR", result.Summary, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// **命中判据 = 退出码 0 且产物目录里真多出了东西**（⛔ 不许只看退出码）。
    ///
    /// <para>实测（2026-10-05，用户原话也是"不许只看退出码"）：<c>-mhe</c>（连文件名一起加密）的 7z
    /// 用**错密码**时，WinRAR 照样回**退出码 0**、却一个文件都没解出来 —— 只看退出码就会把它当成
    /// "这个密码是对的"，一路走到**假的成功**（不变量 6）。这条用例把两个方向都钉住：
    /// 错密码不算命中（⛔ 退出码 0 也不行），正确密码才算命中（⛔ 判据不许把对的也挡掉）。</para>
    /// </summary>
    public class WinRarFallbackContentGateTests : IDisposable
    {
        private const string FixturePassword = "ArchiveFixer-AES-Probe-2026";

        private readonly string _root;

        public WinRarFallbackContentGateTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerWinRarGate", Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响测试结论。
            }
        }

        [WinRarFact]
        public async Task 加密头的包_错密码回退出码0但没产物_不算命中_正确密码才算()
        {
            string work = Path.Combine(_root, "mhe");
            string payload = Path.Combine(work, "payload");

            Directory.CreateDirectory(payload);
            File.WriteAllText(Path.Combine(payload, "secret.txt"), "加密内容\n");

            string archive = Path.Combine(work, "secret.7z");

            RecursionFixHarness.RequireSevenZip();
            RecursionFixHarness.Run7z(
                work,
                "a",
                "-t7z",
                archive,
                "-p" + FixturePassword,
                "-mhe=on",
                Path.Combine("payload", "*"));

            Assert.True(File.Exists(archive), "夹具没造出来（7z 应当退出码 0）");

            string output = Path.Combine(_root, "out");
            var logs = new List<string>();

            Func<CancellationToken, Task> reset = _ =>
            {
                if (Directory.Exists(output))
                {
                    Directory.Delete(output, recursive: true);
                }

                Directory.CreateDirectory(output);

                return Task.CompletedTask;
            };

            async Task<PasswordEngineFallbackOutcome> RunAsync(string candidate)
            {
                return await PasswordEngineFallback.TryAsync(new PasswordFallbackRequest
                {
                    ArchivePath = archive,
                    Candidates = new[] { candidate },
                    TargetDirectory = output,
                    Label = "secret.7z",
                    FailedEngineName = "7-Zip 命令行",
                    FailedErrorType = EngineErrorTypes.WrongPassword,
                    ResetTarget = reset,
                    Log = (level, message) => logs.Add($"[{level}] {message}"),
                    CancellationToken = CancellationToken.None
                });
            }

            // ① 错密码：实测 WinRAR 回退出码 0，却一个文件都不解 ⇒ ⛔ 绝不算命中。
            PasswordEngineFallbackOutcome wrong = await RunAsync("wrong-one");

            Assert.True(wrong.Attempted);
            Assert.False(wrong.Succeeded, "退出码 0 + 一个文件都没解出来，绝不能算命中（否则是假的成功）");
            Assert.Contains(
                logs,
                line => line.Contains("退出码 0", StringComparison.Ordinal)
                        && line.Contains("不算命中", StringComparison.Ordinal));

            Assert.Empty(
                Directory.Exists(output)
                    ? Directory.GetFiles(output, "*", SearchOption.AllDirectories)
                    : Array.Empty<string>());

            // ② 正确密码：真解出东西 ⇒ 必须算命中（同一条判据不许把对的也挡掉）。
            PasswordEngineFallbackOutcome right = await RunAsync(FixturePassword);

            Assert.True(right.Succeeded);
            Assert.Equal(1, right.HitOrdinal);
            Assert.NotEmpty(Directory.GetFiles(output, "*", SearchOption.AllDirectories));
        }
    }

    /// <summary>
    /// **真样本**（用户 2026-10-05 那份"真视频 + 尾部 WinZip AES ZIP"改名成 <c>.zip</c> 的副本）。
    ///
    /// <para>两个环境变量都设了才跑：<c>ARCHIVEFIXER_AESZIP_CASE_DIR</c> + <c>ARCHIVEFIXER_AESZIP_PASSWORD</c>。
    /// ⛔ 样本只读、绝不复制进仓库；⛔ 密码只从环境变量读，绝不落进任何文件或日志（§8）。</para>
    ///
    /// <para>这条用例钉的是**真机那条判据**：同一条密码，7-Zip 吃不下，换 WinRAR 就能解开 ——
    /// 而程序以前把前者如实报成了「达到密码尝试上限」。</para>
    /// </summary>
    public class WinRarFallbackRealSampleTests : IDisposable
    {
        private readonly string _root;
        private readonly ITestOutputHelper _output;

        public WinRarFallbackRealSampleTests(ITestOutputHelper output)
        {
            _output = output;
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerAesZipReal", Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响测试结论。
            }
        }

        [RealAesZipCaseFact]
        public async Task 真样本_同一条密码_七Zip吃不下_换WinRAR兜底能解开()
        {
            string directory = Environment.GetEnvironmentVariable(RealAesZipCaseFactAttribute.CaseDirectoryVariable)!;
            string password = Environment.GetEnvironmentVariable(RealAesZipCaseFactAttribute.PasswordVariable)!;

            List<string> samples = RealAesZipCaseFactAttribute.FindZipSamples(directory);

            Assert.Single(samples);

            string sample = samples[0];

            // ⛔ 只写文件名（§8：个人路径不进任何输出）。
            _output.WriteLine($"样本：{Path.GetFileName(sample)}（{new FileInfo(sample).Length} 字节）");

            // ① 先把清单拿下来（7-Zip 读中央目录不需要能解密 —— 真机那份正是这样：列得出、解不开）。
            var sevenZip = new SevenZipEngine();
            ArchiveListResult listing = await sevenZip.ListAsync(ArchiveRequest.For(sample, password), CancellationToken.None);

            // ② 前提：同一个文件、同一条密码交给 7-Zip **解不出来**（真机：退出码 2 / Wrong password）。
            string sevenZipOutput = Path.Combine(_root, "sevenzip-out");
            ArchiveOperationResult sevenZipExtract = await sevenZip.ExtractAsync(
                new ArchiveRequest { ArchivePath = sample, OutputPath = sevenZipOutput, Password = password },
                new ExtractOptions(),
                CancellationToken.None);

            /*
             * ⚠ 前提**如实记录、不当失败**：本机自造的 AES 包 7-Zip 是解得开的（它就是 7-Zip 造的），
             * 所以这一条只在**真机那一份**（WinZip AES / 压缩法 99）上成立。用例真正要钉的是
             * "同一条密码，WinRAR 兜底能解开，产物字节对得上" —— 前提不成立时这一行会出现在测试输出里。
             */
            _output.WriteLine(
                $"7-Zip 用这条密码解这一份：成功={sevenZipExtract.Success}、"
                + $"退出码={sevenZipExtract.ExitCode}、错误类型={sevenZipExtract.DetectedErrorType}（真机那一份必须是 false）");

            // ③ 兜底：同一条密码交给 WinRAR ⇒ 必须解开。
            string fallbackOutput = Path.Combine(_root, "winrar-out");

            PasswordEngineFallbackOutcome outcome = await PasswordEngineFallback.TryAsync(new PasswordFallbackRequest
            {
                ArchivePath = sample,
                Candidates = new[] { password },
                TargetDirectory = fallbackOutput,
                Label = "真样本",
                FailedEngineName = sevenZip.DisplayName,
                FailedErrorType = sevenZipExtract.DetectedErrorType,
                Log = (level, message) => _output.WriteLine($"[{level}] {message}"),
                CancellationToken = CancellationToken.None
            });

            Assert.True(outcome.Succeeded, $"WinRAR 兜底没解开（退出码 {outcome.LastExitCode}）");
            Assert.Equal(1, outcome.HitOrdinal);
            _output.WriteLine($"WinRAR {outcome.EngineVersion}：兜底成功，命中第 {outcome.HitOrdinal} 个候选");

            // ④ 产物：一个都不许是 0 字节；列得出清单时逐条比总字节。
            List<string> produced = Directory.GetFiles(fallbackOutput, "*", SearchOption.AllDirectories).ToList();

            Assert.NotEmpty(produced);
            Assert.All(produced, file => Assert.True(new FileInfo(file).Length > 0, "0 字节的桩不算产物"));

            long producedBytes = produced.Sum(file => new FileInfo(file).Length);

            _output.WriteLine($"产物 {produced.Count} 个 / 共 {producedBytes} 字节；清单自述总量 {listing.TotalUncompressedSize} 字节");

            if (listing.Success)
            {
                Assert.Equal(listing.TotalUncompressedSize, producedBytes);
            }

            // ⑤ 源样本原地不动（只读验收）。
            Assert.True(File.Exists(sample));
        }
    }

    /// <summary>
    /// 造一份**真的 WinZip AES（压缩法 99）ZIP**（内置 7z 的 <c>-mem=AES256</c>），
    /// 外加"产物应当长什么样"的清单与断言工具（两条端到端用例共用，⛔ 不各写一份）。
    /// </summary>
    internal sealed class WinRarFixture
    {
        private WinRarFixture(string archivePath, IReadOnlyList<FixtureEntry> entries)
        {
            ArchivePath = archivePath;
            Entries = entries;
        }

        public string ArchivePath { get; }

        public IReadOnlyList<FixtureEntry> Entries { get; }

        /// <summary>最近一次造出来的夹具清单（假引擎靠它回答"列目录"，两条路共用一份，⛔ 不各造一份）。</summary>
        internal static ArchiveListResult? CurrentListing { get; private set; }

        internal sealed record FixtureEntry(string RelativePath, long Size, byte[] Content);

        public static WinRarFixture Create(string root, string password)
        {
            string work = Path.Combine(root, "fixture");
            string payload = Path.Combine(work, "payload");

            Directory.CreateDirectory(Path.Combine(payload, "sub"));

            /*
             * 两个条目、其中一个在子目录里：既钉住"包内路径要保留"，又让清单/产物有一点体积
             * （不至于落进"0 字节桩"那一档）。
             *
             * ⚠ 名字**刻意不像分卷**（真机那种 `*.7z.001/.002` 会命中"这一组卷不完整"那道闸门，
             * 定稿当场拒绝发布 —— 那是另一条口径，不是这条用例要验的东西）。
             */
            var entries = new List<FixtureEntry>
            {
                WriteEntry(payload, Path.Combine("sub", "content-a.bin"), 200 * 1024, seed: 11),
                WriteEntry(payload, "content-b.bin", 137 * 1024, seed: 12)
            };

            string archivePath = Path.Combine(work, "pack.zip");

            // 内置 7z 造 WinZip AES（-mem=AES256 = 压缩法 99，与真机那一份同一种形状）。
            RecursionFixHarness.RequireSevenZip();
            RecursionFixHarness.Run7z(
                work,
                "a",
                "-tzip",
                "-mem=AES256",
                "-p" + password,
                archivePath,
                "payload");

            Assert.True(File.Exists(archivePath), "夹具没造出来（7z 应当退出码 0）");

            CurrentListing = new ArchiveListResult
            {
                Success = true,
                Entries = entries
                    .Select(entry => new ArchiveEntry
                    {
                        Path = "payload\\" + entry.RelativePath.Replace('/', '\\'),
                        Size = entry.Size
                    })
                    .ToList(),
                FileCount = entries.Count,
                TotalUncompressedSize = entries.Sum(entry => entry.Size),
                IsEncrypted = true,
                EngineId = EngineIds.SevenZip,
                EngineVersion = "0.0"
            };

            return new WinRarFixture(archivePath, entries);
        }

        /// <summary>断言产物真的发布出来了：存在、大小对、**字节逐字节相同**（⛔ 不许只看退出码）。</summary>
        public static void AssertProductsPublished(string? outputRoot, WinRarFixture fixture)
        {
            Assert.False(string.IsNullOrWhiteSpace(outputRoot), "产物根目录是空的");

            /*
             * 从"这一单的落点"开始找；落点目录本身还没建（比如产物落在它下面某一层）时退到它的父目录 ——
             * 这条用例钉的是"有没有、字节对不对"，⛔ 不替落点模型再立一套断言（那有它自己的用例）。
             */
            string root = outputRoot!;
            string searchRoot = Directory.Exists(root) ? root : (Path.GetDirectoryName(root) ?? root);

            Assert.True(
                Directory.Exists(searchRoot),
                $"产物根目录不存在：{root}（父目录 {searchRoot} 也不在）");

            foreach (FixtureEntry entry in fixture.Entries)
            {
                string? found = FindBySuffix(searchRoot, entry.RelativePath);

                Assert.True(
                    found != null,
                    $"没找到产物 {entry.RelativePath}；{searchRoot} 下实际有："
                    + string.Join(
                        "、",
                        Directory.GetFiles(searchRoot, "*", SearchOption.AllDirectories)
                            .Select(file => Path.GetRelativePath(searchRoot, file))));

                Assert.Equal(entry.Size, new FileInfo(found!).Length);

                byte[] actual = File.ReadAllBytes(found!);

                Assert.Equal(entry.Content.Length, actual.Length);
                Assert.True(
                    actual.AsSpan().SequenceEqual(entry.Content),
                    $"产物字节与原始内容不一致：{entry.RelativePath}");
            }
        }

        /// <summary>
        /// 在产物树里按"相对路径后缀"找那一份文件。
        ///
        /// <para>为什么不写死绝对层级：<c>task.OutputPath</c> 之下还会有既有的落点模型那一层
        /// （包名目录 / 就地替换），而这条用例要钉的是"**有没有、字节对不对**"，
        /// ⛔ 不是再给落点模型立一套断言（那有它自己的用例）。</para>
        /// </summary>
        private static string? FindBySuffix(string root, string relativePath)
        {
            string suffix = relativePath.Replace('\\', '/').TrimStart('/');

            return Directory
                .GetFiles(root, "*", SearchOption.AllDirectories)
                .FirstOrDefault(file => file
                    .Replace('\\', '/')
                    .EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        }

        private static FixtureEntry WriteEntry(string payloadRoot, string relativePath, int size, int seed)
        {
            string full = Path.Combine(payloadRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);

            var content = new byte[size];
            var random = new Random(seed);
            random.NextBytes(content);

            File.WriteAllBytes(full, content);

            return new FixtureEntry(relativePath.Replace('\\', '/'), size, content);
        }
    }
}
