using System;
using System.Globalization;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 日志 / 报告的时间戳必须是**固定格式**，不随系统区域设置变（抄 WinRAR"生成报告"的
    /// 固定 `YYYY-MM-DD hh:mm`：报告是要被解析、被比对、被贴进问题反馈的文本）。
    ///
    /// 这里真正要防的不是"格式串写错"，而是 <c>DateTime.ToString("yyyy-MM-dd …")</c> 的隐含行为：
    /// 它用的是**当前区域的日历** —— 泰历（th-TH）里同一个时刻会写成 2569 年。
    /// 所以测试会临时把当前线程的区域切成泰历，验证结果仍是公历年份。
    /// </summary>
    public class LogTimestampFormatTests
    {
        private static readonly DateTime Sample = new(2026, 9, 22, 19, 20, 21);

        [Fact]
        public void 时间戳格式是固定的一种_且与屏幕日志同形态()
        {
            Assert.Equal("yyyy-MM-dd HH:mm:ss", LogService.TimestampFormat);
            Assert.Equal("yyyyMMdd_HHmmss", LogService.FileNameTimestampFormat);

            // 屏幕日志那一行的形态（Models/OperationLogItem）—— 两处口径必须一致，
            // 否则"日志文件"和"界面日志"会出现两种时间戳。
            var item = new OperationLogItem("INFO", "x") { Time = Sample };

            Assert.Equal($"[{LogService.FormatTimestamp(Sample)}] [INFO] x", item.DisplayText);
        }

        [Fact]
        public void 日志行的时间戳在泰历区域下仍然是公历年份()
        {
            string line = WithCulture("th-TH", () => LogService.FormatLogLine(Sample, "WARN", "示例"));

            Assert.Equal("[2026-09-22 19:20:21] [WARN] 示例", line);
        }

        [Fact]
        public void 日志文件名的时间戳在泰历区域下仍然是公历年份()
        {
            string name = WithCulture("th-TH", () => LogService.FormatFileTimestamp(Sample));

            Assert.Equal("20260922_192021", name);
        }

        [Fact]
        public void 固定格式与区域无关_而裸ToString会跟着区域漂()
        {
            /*
             * 这条测试同时是"证据"：如果哪天有人把 InvariantCulture 去掉，
             * 上面两条会红；而这条下面那句对比会告诉你"为什么必须留着它"。
             */
            WithCulture("th-TH", () =>
            {
                Assert.Equal("2026-09-22 19:20:21", LogService.FormatTimestamp(Sample));

                var thai = new CultureInfo("th-TH");

                if (thai.DateTimeFormat.Calendar is not GregorianCalendar)
                {
                    // 平台上的 th-TH 确实是佛历：裸 ToString 会给出 2569，证明这个防御不是空转。
                    Assert.NotEqual(
                        LogService.FormatTimestamp(Sample),
                        Sample.ToString(LogService.TimestampFormat, thai));
                }

                return true;
            });
        }

        /// <summary>临时切换当前线程的区域并还原。区域是线程级的，不会影响并行跑的其他测试。</summary>
        private static T WithCulture<T>(string cultureName, Func<T> action)
        {
            CultureInfo original = CultureInfo.CurrentCulture;

            try
            {
                CultureInfo.CurrentCulture = new CultureInfo(cultureName);
                return action();
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }
    }
}
