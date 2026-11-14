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

namespace ArchiveFixer.Tests;

/// <summary>
/// 密码与候选的回归用例：
/// 批末合并提示、密码已证实、两义档、尝试上限、先测试再解压、换密码无效。
/// </summary>
public partial class ExtractionPipelineFixTests
{


    // ================================================================ P0-2：密码错误只提示一次

    /// <summary>
    /// 旧实现：每个任务内部各弹一次模态框（50 个错包 = 50 次阻塞点击），而且用的是同步 Dispatcher.Invoke。
    /// 新实现：循环里只登记，批次结束后合并成**一次**提示；日志与弹窗同源。
    ///
    /// 说明：单元测试里没有 WPF Application（下面显式断言这一点），
    /// 所以"弹窗"这一步退化成只写日志 —— 而日志正是可断言的证据：
    /// 只允许出现**一条**"本批 N 个包没能解开密码"，不允许出现按任务重复的提示。
    /// </summary>
    [Fact]
    public async Task 一批密码错误_只合并提示一次()
    {
        Assert.Null(System.Windows.Application.Current);   // 前提：测试进程里没有 WPF 应用

        Harness harness = CreateHarness(passwords: new[] { "候选密码1", "候选密码2" });

        for (int i = 1; i <= 3; i++)
        {
            AddTask(harness, CreateSourceFile($"wrong-{i}.7z"));
        }

        harness.Engine.OnExtractAsync = _ => Task.FromResult(WrongPassword());
        harness.Engine.OnListAsync = _ => Task.FromResult(ArchiveListResult.Failure("WrongPassword", "密码错误", "fake", "1.0"));

        await harness.Coordinator.StartExtractAsync();

        Assert.All(harness.Vm.Tasks, t => Assert.Equal(StatusText.WrongPassword, t.Status));

        string[] aggregated = harness.Log.Logs
            .Where(x => x.Message.Contains("个包没能解开", StringComparison.Ordinal))
            .Select(x => x.Message)
            .ToArray();

        Assert.Single(aggregated);
        Assert.Contains("本批 3 个包没能解开", aggregated[0], StringComparison.Ordinal);
        Assert.Contains("原因：密码错误或缺少正确密码", aggregated[0], StringComparison.Ordinal);

        // 脱敏兜底不许把提示吃掉（"密码"后面直接跟冒号会被 PasswordMasker 整行打码）。
        Assert.DoesNotContain("******", aggregated[0], StringComparison.Ordinal);

        foreach (ArchiveTask task in harness.Vm.Tasks)
        {
            Assert.Contains(task.FileName, aggregated[0], StringComparison.Ordinal);
        }
    }

    // ================================================================ 密码已证实的失败不再试候选（2026-10-01 真机 giu910）

    /// <summary>
    /// **密码已经证实之后，失败不再算"密码候选不对"、也不再往下试候选**（用户 2026-10-01 真机 `giu910`）。
    ///
    /// <para>现场：一组 17.7 GiB 的 7z `-mhe`，候选 3 把整包解到 98%（13 分钟），
    /// 最后只有一个 mp4 报 `CRC Failed in encrypted file. Wrong password?` ——
    /// 老口径把这一句当成"这个候选不对"，于是**解压完又回去试了 7 个候选**、13 分钟的产物全扔，
    /// 最后报「密码错误」。用户原话：「**为什么试了密码之后再去试一次，我说过要试密码的话要在最开始的时候，
    /// 而不是解压完，这才 20G，大一点你这就是在浪费用户时间**」。</para>
    ///
    /// <para>判据**只读事实**：这一趟在暂存目录里真解出了 ≥2 个非空文件 ⇒ 密码不是问题
    /// （错密码连第一份数据都过不去、只留 0 字节桩）。⛔ 不比任何文案。</para>
    /// </summary>
    [Fact]
    public async Task 解出了大半个包之后失败_不再当密码候选不对_也不再往下试候选()
    {
        Assert.Null(System.Windows.Application.Current);

        Harness harness = CreateHarness(passwords: new[] { "候选密码1", "候选密码2", "候选密码3" });
        ArchiveTask task = AddTask(harness, CreateSourceFile("giu.7z.001"));

        // 假引擎：这一趟真的解出了两个非空文件（= 加密头已经打开、数据在往外流），
        // 然后像真机那样报"这个候选不对"（CRC 那句在解析层就落成 WrongPassword）。
        harness.Engine.OnExtractAsync = request =>
        {
            harness.Engine.LastExtractOutputPath = request.OutputPath ?? string.Empty;
            WriteFiles(request.OutputPath!, 2);
            return Task.FromResult(WrongPassword());
        };

        await harness.Coordinator.StartExtractAsync();

        // ① 只试了**一个**候选（修前：三个候选各解一遍）。
        Assert.Single(harness.Engine.ExtractCalls);

        // ② 结论是「文件损坏」，⛔ 不是「密码错误」（用户会拿着密码本白核对半天）。
        Assert.Equal(StatusText.Corrupted, task.Status);
        Assert.Contains("密码已经证实", task.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(StatusText.WrongPassword, task.ErrorMessage, StringComparison.Ordinal);

        // ③ 终态：产物目录真解出了内容（>=2 个文件）⇒ 按 2026-10-16 口径记部分完成。
        Assert.Equal(TaskOutcome.PartiallyCompleted, task.Outcome);
    }

    /// <summary>
    /// **对照组：真的只解出一个文件**（错密码的典型签名 = 第一份数据就过不去）⇒ 照老口径**继续试下一个候选**。
    /// ⛔ 这条挡的是"把错密码误判成文件损坏"——真机那 11 个候选只试第 1 个的坑不许重演。
    /// </summary>
    [Fact]
    public async Task 只解出一个文件时_仍按老口径继续试下一个候选()
    {
        Assert.Null(System.Windows.Application.Current);

        Harness harness = CreateHarness(passwords: new[] { "候选密码1", "候选密码2", "候选密码3" });
        ArchiveTask task = AddTask(harness, CreateSourceFile("one-file.7z"));

        harness.Engine.OnExtractAsync = request =>
        {
            harness.Engine.LastExtractOutputPath = request.OutputPath ?? string.Empty;
            WriteFiles(request.OutputPath!, 1);
            return Task.FromResult(WrongPassword());
        };

        await harness.Coordinator.StartExtractAsync();

        // ⛔ 不许把它当成"密码已证实"：候选得继续往下试（试几个由候选表决定，只钉"不止一个"）。
        Assert.True(
            harness.Engine.ExtractCalls.Count > 1,
            $"只解出一个文件 ⇒ 必须继续试下一个候选，实际只试了 {harness.Engine.ExtractCalls.Count} 次");
        Assert.Equal(StatusText.WrongPassword, task.Status);
    }

    // ================================================================ 两义档也算密码类失败（2026-10-01）

    /// <summary>
    /// **RAR 的"文件已损坏或密码错误"（两义档）也算"密码类失败"**。
    ///
    /// <para>原来 `RecordPasswordFailure` 只认 `密码错误` 与 `达到密码尝试上限` 两档，
    /// 而真机那批 RAR `-p` 包走的**全是**两义档 ⇒ 批末那条"下一步去密码页导入 / 在确认框里填"
    /// 的指路**一次都不出现**，用户只看到一行红字，不知道下一步该干什么。
    /// 它恰恰是最可能缺密码的一档。</para>
    ///
    /// <para>⛔ 但**不许**因此把它说成确认的密码问题（引擎在那条路上分不出"密码错"与"数据坏"），
    /// 也**不许**把它误算成"达到密码尝试上限"—— 那是另一个原因，会把用户指错方向。</para>
    /// </summary>
    [Fact]
    public async Task 两义档也算密码类失败_批末提示给指路且不许断言就是密码问题()
    {
        Assert.Null(System.Windows.Application.Current);   // 前提：测试进程里没有 WPF 应用

        Harness harness = CreateHarness(passwords: new[] { "候选密码1" });
        AddTask(harness, CreateSourceFile("two-way.rar"));

        harness.Engine.OnExtractAsync = _ => Task.FromResult(PasswordOrCorrupted());
        harness.Engine.OnListAsync = _ =>
            Task.FromResult(ArchiveListResult.Failure("WrongPassword", "密码错误", "fake", "1.0"));

        await harness.Coordinator.StartExtractAsync();

        ArchiveTask task = Assert.Single(harness.Vm.Tasks);
        Assert.Equal(StatusText.PasswordOrCorrupted, task.Status);

        string[] aggregated = harness.Log.Logs
            .Where(x => x.Message.Contains("个包没能解开", StringComparison.Ordinal))
            .Select(x => x.Message)
            .ToArray();

        // ① 它确实被算进"密码类失败"了（修前这里一条都没有）。
        Assert.Single(aggregated);
        Assert.Contains(task.FileName, aggregated[0], StringComparison.Ordinal);

        // ② ⛔ 不许说成确认的密码问题：文案里必须带"可能"。
        Assert.Contains("可能", aggregated[0], StringComparison.Ordinal);

        // ③ ⛔ 也不许被误算成"达到密码尝试上限"（那是另一个原因）。
        Assert.DoesNotContain(StatusText.PasswordAttemptLimitReached, aggregated[0], StringComparison.Ordinal);

        // ④ 手动密码的出口指引照旧在 —— 这正是这一档最需要的那一句。
        Assert.Contains("手动", aggregated[0], StringComparison.Ordinal);

        // ⑤ 脱敏兜底不许把提示吃掉（"密码"后面直接跟冒号会被 PasswordMasker 整行打码）。
        Assert.DoesNotContain("******", aggregated[0], StringComparison.Ordinal);
    }

    // ================================================================ 撞上密码尝试上限时的指路（2026-10-02 真机）

    /// <summary>
    /// **撞上「每层密码尝试上限」要给一条指路**（用户 2026-10-02 要求）：没试出来**不等于**
    /// 这些包一定需要密码 —— 可能它们本来就没有密码，也可能对的密码排在候选更靠后（被上限截断了），
    /// 所以要让用户去④页调顺序 / 把上限调大。
    ///
    /// <para>⛔ 三条边界：①仍不许说成确认的密码问题（必须带「可能」）；②上限那两句**原话一个字不改**
    /// （有用例逐字钉着），这一句只是**补在后面**；③不许被 PasswordMasker 吃掉。</para>
    ///
    /// <para>红检：把 <c>ExtractionCoordinator</c> 里那段 <c>if (attemptLimitCount &gt; 0)</c>
    /// 撤掉 ⇒ 本条变红（汇总里再没有"按上限停下"那句指路）。</para>
    /// </summary>
    [Fact]
    public async Task 撞上密码尝试上限_批末要给可能是没有密码或排在更靠后的指路()
    {
        Assert.Null(System.Windows.Application.Current);   // 前提：测试进程里没有 WPF 应用

        // 4 个候选（空密码 + 3 条）而单层上限设成 2 ⇒ 一定被上限截断。
        Harness harness = CreateHarness(
            passwords: new[] { "候选密码1", "候选密码2", "候选密码3" },
            configure: settings => settings.MaxPasswordAttemptsPerLayer = 2);

        AddTask(harness, CreateSourceFile("locked.7z"));

        harness.Engine.OnExtractAsync = _ => Task.FromResult(WrongPassword());
        harness.Engine.OnListAsync = _ =>
            Task.FromResult(ArchiveListResult.Failure("WrongPassword", "密码错误", "fake", "1.0"));

        await harness.Coordinator.StartExtractAsync();

        ArchiveTask task = Assert.Single(harness.Vm.Tasks);
        Assert.Equal(StatusText.PasswordAttemptLimitReached, task.Status);

        string[] aggregated = harness.Log.Logs
            .Where(x => x.Message.Contains("个包没能解开", StringComparison.Ordinal))
            .Select(x => x.Message)
            .ToArray();

        string text = Assert.Single(aggregated);

        // ① 老口径那两句一个字没改（也不许被算成"密码错误"）。
        Assert.Contains("候选还没试完就按上限停了，不等于密码错误", text, StringComparison.Ordinal);

        // ② 新增的指路：可能是没有密码 / 可能排在更靠后 + 去④页调顺序（数字取设置里那个上限）。
        Assert.Contains("可能它们本来就没有密码", text, StringComparison.Ordinal);
        Assert.Contains("也可能正确的密码排在候选里更靠后的位置", text, StringComparison.Ordinal);
        Assert.Contains("每层最多只试前 2 条", text, StringComparison.Ordinal);
        Assert.Contains("④页", text, StringComparison.Ordinal);

        // ③ 不许被脱敏兜底吃掉。
        Assert.DoesNotContain("******", text, StringComparison.Ordinal);
    }

    // ================================================================ 先测试再解压（2026-10-01）

    /// <summary>
    /// **「先测试再解压」这条路的两义档**：引擎说"文件已损坏或密码错误"时要**继续试下一个候选**
    /// （⛔ 不是 break 当成最终结论）。
    ///
    /// <para>为什么专门补这一条：这条路（<c>settings.TestBeforeExtract = true</c>）在整个测试套件里
    /// 原来**一条用例都没有** —— 而它是用户能开的设置项、真机第 37 条要求的正是"先试密码再解压"，
    /// 谁改坏了它都不会有人知道。</para>
    ///
    /// <para>判据全用机器事实：<c>TestAsync</c> 被调用**不止一次**（= 真的换了下个候选）、
    /// 终态是两义档、且**不是**"密码错误"（⛔ 引擎只说了「可能」，不许替它下结论）。</para>
    /// </summary>
    [Fact]
    public async Task 先测试再解压_两义档要继续试下一个候选_不许当成最终结论()
    {
        Harness harness = CreateHarness(
            passwords: new[] { "候选密码1", "候选密码2" },
            configure: settings => settings.TestBeforeExtract = true);

        ArchiveTask task = AddTask(harness, CreateSourceFile("test-first-two-way.rar"));

        int testCalls = 0;

        harness.Engine.OnTestAsync = _ =>
        {
            testCalls++;
            return Task.FromResult(PasswordOrCorrupted());
        };

        await harness.Coordinator.StartExtractAsync();

        // ① 结论是两义档 —— ⛔ 既不是"密码错误"（会把真损坏说成密码问题），也不是"文件损坏"。
        //    失败时把判据输入一起带出来（试了几次 / 解压被调了几次 / 原因 / 日志尾部）。
        Assert.True(
            task.Status == StatusText.PasswordOrCorrupted,
            $"两义档的结论必须是「{StatusText.PasswordOrCorrupted}」，实际「{task.Status}」"
            + $"；原因：{task.ErrorMessage}"
            + $"；测试调用 {testCalls} 次 / 解压调用 {harness.Engine.ExtractCalls.Count} 次"
            + "\n日志尾部：" + string.Join(
                " | ",
                harness.Log.Logs.TakeLast(10).Select(item => item.Level + ":" + item.Message)));
        Assert.NotEqual(StatusText.WrongPassword, task.Status);
        Assert.NotEqual(StatusText.Corrupted, task.Status);

        // ② 真的换了下个候选：测试被调用不止一次（旧口径"第 1 个候选就 break"在这里会红）。
        Assert.True(
            testCalls >= 2,
            $"两义档必须继续试下一个候选（⛔ 不许 break 当结论），实际只试了 {testCalls} 次");

        // ③ 一次解压都不该发生：测试没过就不进解压那一步。
        Assert.Empty(harness.Engine.ExtractCalls);

        // ④ 两义那一档必须写清"引擎只说了可能"（比泛泛的一句更能回答用户"那我该干什么"）。
        Assert.Contains(
            harness.Log.Logs,
            x => x.Message.Contains("可能", StringComparison.Ordinal));
    }

    // ================================================================ P1-4：密码尝试上限

    /// <summary>
    /// 密码候选没有上限时，几百条密码本的包会被逐个候选整包重解一遍（每个候选一次完整解压）。
    /// 现在每层最多试 <c>Settings.MaxPasswordAttemptsPerLayer</c> 个（缺省 10）。
    /// </summary>
    [Fact]
    public async Task 密码候选超过上限时_只试到上限且状态是达到上限而不是密码错误()
    {
        var passwords = Enumerable.Range(1, 40).Select(i => $"候选密码{i}").ToArray();

        Harness harness = CreateHarness(passwords: passwords);
        ArchiveTask task = AddTask(harness, CreateSourceFile("many-passwords.7z"));

        harness.Engine.OnExtractAsync = _ => Task.FromResult(WrongPassword());
        harness.Engine.OnListAsync = _ => Task.FromResult(ArchiveListResult.Failure("WrongPassword", "密码错误", "fake", "1.0"));

        await harness.Coordinator.StartExtractAsync();

        // 空密码 + 40 条候选 = 41 个，必须被截到上限（缺省 10）。
        Assert.Equal(harness.Vm.Settings.MaxPasswordAttemptsPerLayer, harness.Engine.ExtractCalls.Count);
        Assert.True(harness.Engine.ExtractCalls.Count < passwords.Length + 1, "候选没有被截断");

        Assert.Equal(StatusText.PasswordAttemptLimitReached, task.Status);
        Assert.NotEqual(StatusText.WrongPassword, task.Status);
        Assert.Contains("上限", task.ErrorMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// 上限是**设置项**，不是写死的常量：用户在设置界面改成 3，就必须只试 3 个候选。
    /// （旧实现里它是 <c>ExtractionCoordinator</c> 的常量，界面改了不生效 —— 等于设置项是摆设。）
    /// </summary>
    [Fact]
    public async Task 密码尝试上限改成设置值后_真的只试那么多个()
    {
        var passwords = Enumerable.Range(1, 40).Select(i => $"候选密码{i}").ToArray();

        Harness harness = CreateHarness(
            passwords: passwords,
            configure: settings => settings.MaxPasswordAttemptsPerLayer = 3);

        ArchiveTask task = AddTask(harness, CreateSourceFile("limit-3.7z"));

        harness.Engine.OnExtractAsync = _ => Task.FromResult(WrongPassword());
        harness.Engine.OnListAsync = _ => Task.FromResult(ArchiveListResult.Failure("WrongPassword", "密码错误", "fake", "1.0"));

        await harness.Coordinator.StartExtractAsync();

        Assert.Equal(3, harness.Engine.ExtractCalls.Count);

        // 到上限 ≠ 密码错误（AGENTS.md §9.2）：候选还剩 38 个，不能说"密码本里没有正确密码"。
        Assert.Equal(StatusText.PasswordAttemptLimitReached, task.Status);
        Assert.NotEqual(StatusText.WrongPassword, task.Status);
        Assert.Contains("3 个候选", task.ErrorMessage, StringComparison.Ordinal);
    }

    /// <summary>候选本来就不超过上限时，试完全部仍失败 —— 这时才该报"密码错误"。</summary>
    [Fact]
    public async Task 候选没到上限且全部错误_仍然报密码错误()
    {
        Harness harness = CreateHarness(passwords: new[] { "候选密码1", "候选密码2" });
        ArchiveTask task = AddTask(harness, CreateSourceFile("few-passwords.7z"));

        harness.Engine.OnExtractAsync = _ => Task.FromResult(WrongPassword());
        harness.Engine.OnListAsync = _ => Task.FromResult(ArchiveListResult.Failure("WrongPassword", "密码错误", "fake", "1.0"));

        await harness.Coordinator.StartExtractAsync();

        Assert.Equal(3, harness.Engine.ExtractCalls.Count);   // 空密码 + 2 条候选，全试过
        Assert.Equal(StatusText.WrongPassword, task.Status);
    }

    /// <summary>
    /// 上限这个设置项也要管到**递归内层**，而且必须是同一个值。
    ///
    /// 真实的坑：候选表按设置截断，而递归核心（<c>RecursionLimits</c>）另有自己的默认值 8。
    /// 用户把上限改成 20 时，如果只有候选表跟着改，递归里仍然在第 8 个候选上停手 ——
    /// 设置项看着生效、实际半生效（而且状态还报"达到密码尝试上限"，让人以为已经试到底了）。
    /// 这里用 20 做钉子：旧接线只会有 8 次解压调用。
    /// </summary>
    [Fact]
    public async Task 递归内层也按设置里的密码尝试上限停手()
    {
        var passwords = Enumerable.Range(1, 40).Select(i => $"候选密码{i}").ToArray();

        Harness harness = CreateHarness(
            passwords: passwords,
            configure: settings =>
            {
                settings.MaxPasswordAttemptsPerLayer = 20;
            });

        ArchiveTask task = AddTask(harness, CreateSourceFile("recursive-limit.7z"));

        harness.Engine.OnExtractAsync = _ => Task.FromResult(WrongPassword());
        harness.Engine.OnListAsync = _ => Task.FromResult(ArchiveListResult.Failure("WrongPassword", "密码错误", "fake", "1.0"));

        // 递归的工作区根目录是进程级静态（见 CreateHarness 的说明），用完必须还原。
        string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;

        try
        {
            RecursiveExtractor.ConfiguredWorkspaceRoot = Path.Combine(_root, "work");

            await harness.Coordinator.StartExtractAsync();
        }
        finally
        {
            RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
        }

        // 空密码 + 40 条候选，上限 20 → 恰好 20 次解压尝试（旧接线是 8 次）。
        Assert.Equal(20, harness.Engine.ExtractCalls.Count);

        // 一个候选都没成功：绝不能显示成功（不变量 6），最多"部分完成"。
        Assert.NotEqual(StatusText.ExtractSuccess, task.Status);
    }

    /// <summary>
    /// **先试密码，再解整包**（用户 2026-09-25 第 37 条，他原话："你应该先试密码再进行解压"）。
    ///
    /// <para>真机代价：那个 12.22 GiB 的包用**空密码**跑了 10 分 18 秒才报密码错误（还把 12 GiB 垃圾写进工作区）。
    /// 这条钉子把一个"名字可见 + 条目加密"的大包喂给管线，断言：</para>
    /// <list type="bullet">
    /// <item><description>错密码**只解探针那一个条目**（`IncludeEntries` 非空），**一次整包都不解**；</description></item>
    /// <item><description>对密码才走到整包解压；</description></item>
    /// <item><description>「空密码」这一档**根本不试**（加密包不可能用空密码加密）。</description></item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task 加密大包_错密码只解一个小条目_绝不去解整包()
    {
        Harness harness = CreateHarness(passwords: new[] { "错误密码", "正确密码" });

        // 源文件要有一点体积：预检里的"展开比"是 解压后 ÷ 压缩包体积，
        // 55 字节的假源文件配 100 MiB 的声明显然会被判成压缩炸弹（那是另一条闸门，与本用例无关）。
        string sourcePath = Path.Combine(_root, "src", "big-encrypted.7z");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        File.WriteAllBytes(sourcePath, new byte[1024 * 1024]);

        ArchiveTask task = AddTask(harness, sourcePath);

        harness.Engine.OnListAsync = _ => Task.FromResult(new ArchiveListResult
        {
            Success = true,
            FileCount = 2,
            TotalUncompressedSize = 100L * 1024 * 1024,
            Entries = new List<ArchiveEntry>
            {
                new() { Path = "big.bin", Size = 100L * 1024 * 1024 },
                new() { Path = "readme.txt", Size = 1024 }
            },
            IsEncrypted = true,
            EngineId = "fake",
            EngineVersion = "1.0"
        });

        int probeCalls = 0;
        int fullExtractCalls = 0;
        var triedCandidates = new List<string>();

        harness.Engine.OnExtractWithOptionsAsync = (request, options) =>
        {
            triedCandidates.Add(request.Password ?? string.Empty);

            if (options.IncludeEntries.Count > 0)
            {
                probeCalls++;

                Assert.Equal("readme.txt", options.IncludeEntries[0]);

                return Task.FromResult(
                    request.Password == "正确密码"
                        ? Succeeded()
                        : new ArchiveOperationResult
                        {
                            Success = false,
                            Status = StatusText.WrongPassword,
                            Message = "密码错误",
                            DetectedErrorType = "WrongPassword"
                        });
            }

            fullExtractCalls++;
            WriteFiles(request.OutputPath!, 2);
            harness.Engine.Extracted = true;
            return Task.FromResult(Succeeded());
        };

        await harness.Coordinator.StartExtractAsync();

        _output.WriteLine($"诊断：状态={task.Status} 原因={task.ErrorMessage} 候选={string.Join("|", triedCandidates)} ExtractCalls={harness.Engine.ExtractCalls.Count}");

        // 空密码那一档根本没被试过（加密包）。
        Assert.DoesNotContain(string.Empty, triedCandidates);

        // 两个非空候选各探一次；整包只解了一次（对的那个）。
        Assert.Equal(2, probeCalls);
        Assert.Equal(1, fullExtractCalls);
    }

    /// <summary>
    /// 多分支询问里用户选"只保留当前这一层"（无界面宿主下 ShowConfirm 返回 false，正好走这一支）。
    ///
    /// 这条路的结论是在协调器里被**改写成 Completed** 的：递归核心当时以 NeedsDecision 收尾、
    /// 按规则没清工作区；产物被取回暂存目录之后那份工作区就是纯垃圾，
    /// 不清的话每次"只保留当前一层"都会在 <c>data\work\recursive</c> 留一份。
    /// </summary>
    [Fact]
    public async Task 没加密的包_产物校验不过时_不再换密码也不写密码错误()
    {
        Harness harness = CreateHarness(passwords: new[] { "候选密码1", "候选密码2", "候选密码3" });

        string sourcePath = CreateSourceFile("apk-like.apk");
        ArchiveTask task = AddTask(harness, sourcePath);

        harness.Engine.OnListAsync = _ => Task.FromResult(new ArchiveListResult
        {
            Success = true,
            FileCount = 2,
            TotalUncompressedSize = 200,
            Entries = new List<ArchiveEntry>
            {
                new() { Path = "classes.dex", Size = 100 },
                new() { Path = "AndroidManifest.xml", Size = 100 }
            },
            IsEncrypted = false,
            EngineId = "fake",
            EngineVersion = "1.0"
        });

        // 引擎"成功"，但只写出一个文件 → 校验必然判否（真机少 33 个文件就是这种形状）
        harness.Engine.OnExtractAsync = request =>
        {
            harness.Engine.LastExtractOutputPath = request.OutputPath ?? string.Empty;
            Directory.CreateDirectory(request.OutputPath!);
            File.WriteAllBytes(Path.Combine(request.OutputPath!, "classes.dex"), new byte[100]);
            return Task.FromResult(Succeeded());
        };

        await harness.Coordinator.StartExtractAsync();

        Assert.Single(harness.Engine.ExtractCalls);
        Assert.NotEqual(StatusText.WrongPassword, task.Status);
        Assert.Equal(StatusText.PasswordNotNeeded, task.PasswordStatus);
        Assert.Contains("没有加密", task.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("产物校验未通过", task.ErrorMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// **连续两个候选的结果一模一样 → 立刻停**（换密码不可能改变结果；真机白试了 9 次）。
    ///
    /// <para>判据只用事实：两次校验的文件数与字节数相同（⛔ 不比文案、也不看引擎说了什么）。</para>
    /// </summary>
    [Fact]
    public async Task 连续两个候选结果完全相同_不再试第三个()
    {
        Harness harness = CreateHarness(passwords: new[] { "候选密码1", "候选密码2", "候选密码3", "候选密码4" });

        string sourcePath = CreateSourceFile("same-result.7z");
        ArchiveTask task = AddTask(harness, sourcePath);

        harness.Engine.OnListAsync = _ => Task.FromResult(new ArchiveListResult
        {
            Success = true,
            FileCount = 2,
            TotalUncompressedSize = 200,
            Entries = new List<ArchiveEntry>
            {
                new() { Path = "one.bin", Size = 100 },
                new() { Path = "two.bin", Size = 100 }
            },

            // 加密包：这一条走的是"结果相同"那一路（不是"没加密"那一路）
            IsEncrypted = true,
            EngineId = "fake",
            EngineVersion = "1.0"
        });

        harness.Engine.OnExtractAsync = request =>
        {
            harness.Engine.LastExtractOutputPath = request.OutputPath ?? string.Empty;
            Directory.CreateDirectory(request.OutputPath!);
            File.WriteAllBytes(Path.Combine(request.OutputPath!, "one.bin"), new byte[100]);
            return Task.FromResult(Succeeded());
        };

        await harness.Coordinator.StartExtractAsync();

        Assert.Equal(2, harness.Engine.ExtractCalls.Count);
        Assert.Contains("完全相同", task.ErrorMessage, StringComparison.Ordinal);
    }
}
