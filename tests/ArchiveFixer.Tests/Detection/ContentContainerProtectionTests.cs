using System;
using System.IO;
using ArchiveFixer.Security;
using Xunit;

namespace ArchiveFixer.Tests
{
    public sealed class ContentContainerProtectionTests
    {
        [Theory]
        [InlineData("说明.docx")]
        [InlineData("表格.XLSX")]
        [InlineData("幻灯片.pptx")]
        [InlineData("书.epub")]
        [InlineData("漫画.cbz")]
        [InlineData("地图.kmz")]
        [InlineData("资料.pages")]
        [InlineData("安装包.apk")]
        [InlineData("组件.jar")]
        [InlineData("扩展.vsix")]
        public void 默认内容容器不递归解压(string name)
        {
            Assert.True(ContentContainerProtection.ShouldProtect(name));
            Assert.True(ContentKeepRules.Empty.ShouldKeep(name));
        }

        [Theory]
        [InlineData("普通.zip")]
        [InlineData("普通.7z")]
        [InlineData("说明.txt")]
        [InlineData("说明.docx.bak")]
        public void 普通压缩包和普通文件不受内容容器保护(string name)
        {
            Assert.False(ContentContainerProtection.ShouldProtect(name));
            Assert.False(ContentKeepRules.Empty.ShouldKeep(name));
        }
    }
}
