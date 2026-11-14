using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Concurrent;
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
    /// 解压管线（<see cref="ExtractionCoordinator"/>）的五个修复点的回归测试。
    ///
    /// 全部走**真 MainViewModel + 真解压管线**，只把归档引擎换成可控的假引擎：
    /// 这样能精确摆出"大产物目录"、"密码候选几百条"、"收尾时按下取消"这些现场，
    /// 而不用去读用户机器上的任何真实文件（AGENTS.md §8 隐私红线）。
    ///
    /// 和 InnerLayerContinuationTests 同一组：MainViewModel 的构造会写进程级静态
    /// （7z 路径 / 递归工作区根目录），必须和其他测试集串行，并在装配后立刻还原。
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    [Trait("Category", "Stress")]
    public partial class ExtractionPipelineFixTests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string _root;

        public ExtractionPipelineFixTests(ITestOutputHelper output)
        {
            _output = output;
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerExtractFix", Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响结论（句柄可能还在释放中）。
            }
        }

    }
}
