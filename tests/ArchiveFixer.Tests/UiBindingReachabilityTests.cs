using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using ArchiveFixer.Views;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 「界面上的绑定名是不是真的存在」的**运行时**检查（2026-09-22 专项体检，清单第 2 条）。
    ///
    /// <para><b>要防的是什么</b>：WPF 的绑定写错属性名时**不报错、不抛异常** ——
    /// 它只是静默地什么都不显示（可选属性）或显示默认值。于是"界面看起来有这一项、实际上永远是空的"
    /// 这类缺陷可以一路通过 <c>dotnet build</c>、静态检查、甚至"窗口能打开"的冒烟测试。
    /// <c>XamlBindingSafetyTests</c> 覆盖的是另一类（只读属性上的默认双向绑定 → 模板实例化时抛异常），
    /// 它不看"路径找不找得到成员"。</para>
    ///
    /// <para><b>怎么查</b>：在**真正创建窗口的那条 STA 线程上**打开 WPF 自己的绑定跟踪
    /// （<c>PresentationTraceSources.DataBindingSource</c> → <c>SourceLevels.Warning</c>），
    /// 然后把每个窗口**带数据**离屏显示一遍（没有数据的列表/表格不会实例化模板，
    /// 模板里的绑定也就永远不会被求值）。绑定路径找不到成员时，WPF 会往这个 TraceSource
    /// 写一条 <c>System.Windows.Data Error: 40 : BindingExpression path error: …</c>。</para>
    ///
    /// <para><b>§13 纪律</b>：全部离屏（<c>-32000,-32000</c> + 不进任务栏 + 不激活），
    /// 不置前、不最大化、不碰鼠标键盘；跑完把所有窗口关掉并把 <c>Application.Current</c> 还原成 null
    /// （与 <see cref="WindowInstantiationTests"/> 同一套做法，见那里的说明）。</para>
    ///
    /// <para><b>不自欺</b>：用例里有一个**正向对照** —— 故意写错一条绑定路径的窗口。
    /// 收集器如果连它都抓不到，说明监听根本没生效，此时用例会明确报出来（环境显示不了窗口时
    /// 退回静态检查，并把"检查了几条绑定"断言成正数），绝不静默通过。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class UiBindingReachabilityTests
    {
        /// <summary>正向对照用的假 ViewModel：故意没有 <see cref="ControlProbeBindingPath"/> 这个属性。</summary>
        public sealed class BindingProbeControl
        {
            public string ExistingProperty => "对照";
        }

        /// <summary>对照窗口绑定的、**不存在**的路径。</summary>
        public const string ControlProbeBindingPath = "ThisPropertyDoesNotExistOnPurpose";

        private readonly ITestOutputHelper _output;
        private readonly string _root;

        public UiBindingReachabilityTests(ITestOutputHelper output)
        {
            _output = output;
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerBindingProbe", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [Fact]
        public void 所有窗口的绑定路径在运行时都能找到成员()
        {
            string? previousWorkspaceRoot = ArchiveFixer.Extraction.RecursiveExtractor.ConfiguredWorkspaceRoot;
            string previousSevenZipPath = ArchiveFixer.Engines.ToolLocator.Default.CustomSevenZipExePath;

            var collector = new BindingErrorCollector();

            UiProbeReport report;

            try
            {
                report = UiProbe.Run(
                    _output.WriteLine,
                    _root,
                    onProbeThread: () => collector.Attach(ShowControlProbeWindow));
            }
            finally
            {
                ArchiveFixer.Extraction.RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
                ArchiveFixer.Engines.ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;
                ApplicationStash.RestoreNull();

                TryDeleteRoot();
            }

            // 收集器必须先被证明有效，否则"零发现"毫无意义。
            Assert.True(
                collector.SawControlProbeError,
                "绑定跟踪没有抓到正向对照窗口的错误 —— 监听没生效，本次检查什么都没验证到。"
                + Environment.NewLine
                + $"探针跳过原因：{report.SkipReason ?? "（无）"}"
                + Environment.NewLine
                + $"探针失败：{report.FailureText ?? "（无）"}"
                + Environment.NewLine
                + $"已显示的窗口：{(report.ShownWindows.Count == 0 ? "（一个都没有）" : string.Join("、", report.ShownWindows.Select(w => w.Name)))}"
                + Environment.NewLine
                + $"Application 静态字段残留：{ApplicationStash.LiveInstanceFieldCount()} 个"
                + Environment.NewLine
                + $"监听器数量：{collector.AttachedListenerCount}"
                + Environment.NewLine
                + collector.Dump());

            /*
             * 还要证明"被检查的那几个窗口真的被显示过"：否则零发现可能只是"一个窗口都没打开"。
             * 显示不了窗口（无桌面会话）时退化成显式报告，并断言对照窗口自身已经把机制跑通了 ——
             * 绝不把"什么都没看"写成通过。
             */
            if (report.SkipReason != null)
            {
                _output.WriteLine("⚠ 这台机器显示不了窗口，只验证了绑定跟踪机制本身：" + report.SkipReason);
            }
            else
            {
                Assert.Equal(UiProbe.ExpectedWindowNames.Count, report.ShownWindows.Count);
                Assert.All(
                    UiProbe.ExpectedWindowNames,
                    name => Assert.Contains(report.ShownWindows, window => window.Name == name));
            }

            List<string> real = collector.Messages
                .Where(message => !message.Contains(ControlProbeBindingPath, StringComparison.Ordinal))
                .ToList();

            Assert.True(
                real.Count == 0,
                "界面上的绑定路径有找不到成员的（WPF 会**静默**吞掉，界面那一格永远是空的）："
                + Environment.NewLine
                + string.Join(Environment.NewLine, real));

            Assert.True(
                report.FailureText == null,
                "窗口显示过程中抛异常：" + report.FailureText);
        }

        /// <summary>正向对照：一条**故意写错**的绑定路径，用来证明监听真的在工作。</summary>
        private static void ShowControlProbeWindow()
        {
            // TextBlock.Text 是普通字符串属性，只能靠 SetBinding 挂一条（故意写错的）绑定。
            var text = new TextBlock { DataContext = new BindingProbeControl() };
            text.SetBinding(TextBlock.TextProperty, new Binding(ControlProbeBindingPath));

            var window = new Window
            {
                Title = "绑定可达性对照窗口",
                Width = 200,
                Height = 100,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                ShowInTaskbar = false,
                ShowActivated = false,
                Content = text
            };

            try
            {
                window.Show();
                window.UpdateLayout();
            }
            finally
            {
                try
                {
                    window.Close();
                }
                catch
                {
                    // 关不掉不影响结论：进程退出时会被清理。
                }
            }
        }

        private void TryDeleteRoot()
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
    }

    /// <summary>
    /// 收集 WPF 绑定跟踪里的报错（**必须在被测窗口所在的那条线程**上 <see cref="Attach"/>）。
    ///
    /// <para>
    /// <c>PresentationTraceSources.DataBindingSource</c> 是按线程生效的：在测试线程上装监听、
    /// 在探针线程上建窗口，什么也收不到。所以 <see cref="Attach"/> 由探针的前置钩子调用。
    /// </para>
    /// </summary>
    internal sealed class BindingErrorCollector
    {
        private readonly object _gate = new();
        private readonly List<string> _messages = new();
        private readonly StringBuilder _pending = new();

        /// <summary>收集到的绑定报错（已按行切开）。</summary>
        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_gate)
                {
                    return _messages.ToList();
                }
            }
        }

        /// <summary>是否抓到正向对照窗口那条"故意写错"的绑定错误。</summary>
        public bool SawControlProbeError
        {
            get
            {
                lock (_gate)
                {
                    return _messages.Any(
                        message => message.Contains(
                            UiBindingReachabilityTests.ControlProbeBindingPath,
                            StringComparison.Ordinal));
                }
            }
        }

        /// <summary>实际挂上的监听器个数（守卫失败时要能看出"是不是根本没挂上"）。</summary>
        public int AttachedListenerCount { get; private set; }

        public void Attach(Action? afterAttach = null)
        {
            try
            {
                PresentationTraceSources.Refresh();

                PresentationTraceSources.DataBindingSource.Listeners.Add(new CollectorTraceListener(this));
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

                AttachedListenerCount = PresentationTraceSources.DataBindingSource.Listeners.Count;
            }
            catch (Exception ex)
            {
                Add("绑定跟踪安装失败：" + ex.Message);
            }

            afterAttach?.Invoke();
        }

        public string Dump()
        {
            lock (_gate)
            {
                return _messages.Count == 0
                    ? "（一条绑定报错都没收到）"
                    : string.Join(Environment.NewLine, _messages);
            }
        }

        internal void Add(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            lock (_gate)
            {
                _messages.Add(message.Trim());
            }
        }

        internal void WriteChunk(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            lock (_gate)
            {
                _pending.Append(text);

                string buffered = _pending.ToString();
                int lastBreak = buffered.LastIndexOf('\n');

                if (lastBreak < 0)
                {
                    return;
                }

                string complete = buffered[..lastBreak];
                _pending.Clear();
                _pending.Append(buffered[(lastBreak + 1)..]);

                foreach (string line in complete.Split('\n'))
                {
                    string trimmed = line.Trim();

                    // 只留 WPF 的绑定报错，别把每一条正常的绑定事件都收进来（那会变成噪音）。
                    if (trimmed.Contains("System.Windows.Data Error", StringComparison.Ordinal) ||
                        trimmed.Contains("path error", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.Contains("property not found", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.Contains("Cannot find source for binding", StringComparison.Ordinal))
                    {
                        _messages.Add(trimmed);
                    }
                }
            }
        }
    }

    /// <summary>把 WPF 绑定跟踪的文本转给 <see cref="BindingErrorCollector"/> 的 TraceListener。</summary>
    internal sealed class CollectorTraceListener : TraceListener
    {
        private readonly BindingErrorCollector _collector;

        public CollectorTraceListener(BindingErrorCollector collector)
        {
            _collector = collector;
        }

        public override void Write(string? message) => _collector.WriteChunk(message);

        public override void WriteLine(string? message) => _collector.WriteChunk((message ?? string.Empty) + "\n");
    }
}
