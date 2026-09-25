using System;
using ArchiveFixer.Models;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 源包处理档的设置项（决策 D-9；默认档见 2026-09-25 第 32 条）：
    /// 默认 = <c>KeepInPlace</c>（**源包原来位置不动** —— 用户在 ③「清理与删除」页亲自定的默认），
    /// 空 / 非法一律回落这一档，旧配置不报错。
    ///
    /// 为什么容错必须在这一层：这个字符串可能来自旧配置（没有这个字段）、用户手改的 json，
    /// 或将来改名后的枚举。到解压那一刻才发现读不懂是最糟的 —— 用户已经点了一键处理，
    /// 而"要不要搬走他的源文件"却靠猜。
    /// </summary>
    public class SourceHandlingSettingTests
    {
        [Fact]
        public void 默认档_是留在原地()
        {
            var settings = new AppSettings();

            Assert.Equal(nameof(SourceHandlingMode.KeepInPlace), settings.SourceHandling);
            Assert.Equal(SourceHandlingMode.KeepInPlace, AppSettings.ParseSourceHandling(settings.SourceHandling));
            Assert.Equal(SourceHandlingMode.KeepInPlace, AppSettings.ParseSourceHandling(AppSettings.CreateDefault().SourceHandling));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Whatever")]
        [InlineData("99")]           // 数字能骗过 Enum.TryParse，必须被 IsDefined 拦住
        [InlineData("-1")]
        public void 空或非法值一律回落默认档_不抛异常(string? stored)
        {
            Assert.Equal(SourceHandlingMode.KeepInPlace, AppSettings.ParseSourceHandling(stored));
        }

        [Theory]
        [InlineData("MoveToRest", SourceHandlingMode.MoveToRest)]
        [InlineData("KeepInPlace", SourceHandlingMode.KeepInPlace)]
        [InlineData("keepplace", SourceHandlingMode.KeepInPlace)]         // 写错的枚举名 → 默认档
        [InlineData(" KeepInPlace ", SourceHandlingMode.KeepInPlace)]     // 手改 json 时多打空格
        [InlineData("movetorest", SourceHandlingMode.MoveToRest)]         // 大小写不敏感
        public void 合法值原样解析(string stored, SourceHandlingMode expected)
        {
            Assert.Equal(expected, AppSettings.ParseSourceHandling(stored));
        }

        [Fact]
        public void 归一化_把设置里的字符串写成合法枚举名()
        {
            var settings = new AppSettings { SourceHandling = "Nonsense" };

            settings.Normalize();

            Assert.Equal(nameof(SourceHandlingMode.KeepInPlace), settings.SourceHandling);

            settings.SourceHandling = "  MoveToRest  ";
            settings.Normalize();

            Assert.Equal(nameof(SourceHandlingMode.MoveToRest), settings.SourceHandling);
        }

        [Fact]
        public void 反解成落盘字符串_与枚举同名()
        {
            foreach (SourceHandlingMode mode in Enum.GetValues<SourceHandlingMode>())
            {
                string stored = AppSettings.ToSourceHandlingValue(mode);

                Assert.Equal(mode.ToString(), stored);
                Assert.Equal(mode, AppSettings.ParseSourceHandling(stored));
            }
        }
    }
}
