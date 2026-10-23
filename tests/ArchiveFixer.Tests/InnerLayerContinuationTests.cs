using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 这一组测试要构造真的 <see cref="MainViewModel"/>，而它的构造过程会写两个**进程级静态**：
    /// 7z.exe 路径（<c>ToolLocator.Default</c>）与递归工作区根目录（<c>RecursiveExtractor.ConfiguredWorkspaceRoot</c>）。
    /// 和别的测试集并行跑时，别的测试会拿着"我这边马上要被删掉的临时目录"当工作区去解压
    /// （实测：RecursiveExtractorTests 因此在解压到一半时目录被删，报"引擎操作失败"）。
    ///
    /// 所以这一组声明成"不与其他集合并行"，并且每次装配后**立刻把两个静态还原**（见 CreateHarness）。
    /// </summary>
    [CollectionDefinition("ArchiveFixerGlobalState", DisableParallelization = true)]
    public class ArchiveFixerGlobalStateCollection
    {
    }

    /// <summary>
    /// 一键处理**自动续解内层包**（第 2 层起）的行为测试。
    ///
    /// 真实现场：用户的文件是"双面文件"（<c>xxx.mp4</c> = 真 MP4 头 + 尾部一个完整 ZIP），
    /// 解出来的第一层不是最终数据，而是 <c>&lt;id&gt;.7z.001</c> + <c>.002</c>（加密分卷），
    /// 要再解一层才是结果。以前一键处理只做第一层就停手，用户看到的现象就是"只有第一层能解出来"。
    ///
    /// 这里跑的是**真链路**：真 7z.exe、真 MainViewModel + 各 Coordinator、真的单层解压管线
    /// （刻意不碰 <c>RecursiveExtractor</c> —— 它在用户的真实文件上会卡死，续解不走它）。
    /// 引擎被包一层只是为了**数调用次数**：要证明"第 2 轮没有再解一遍已经处理过的包"，
    /// 只能看引擎实际被调了几次、拿的是哪个文件。
    ///
    /// 样本全部自己造（临时目录 + 项目内置 7z.exe），不读用户目录里的任何文件（AGENTS.md §8）。
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class InnerLayerContinuationTests : IDisposable
    {
        private const string OuterPassword = "OuterPass1";
        private const string InnerPassword = "InnerPass2";

        /// <summary>续解链测试用的统一密码（列表式密码本，一行一个）。</summary>
        private const string ChainPassword = "ChainPass9";

        /// <summary>
        /// 故意**不写进密码本**的密码：给某一层加它 ⇒ 那一层必然解不开
        /// （用来复现"从未解开过的内层包"那一格，缺陷用例①）。
        /// </summary>
        private const string UnknownInnerPassword = "测试用未知密码";

        private const string InnerPayloadText = "第二层才有的最终数据\n";

        /// <summary>假视频头长度：与 EmbeddedArchiveTests 一致（34 KB 的文件头读不到尾部的 ZIP）。</summary>
        private const int FakeVideoPrefixLength = 32768;

        private readonly string _root;
        private readonly string _sevenZip;

        public InnerLayerContinuationTests()
        {
            _sevenZip = LocateSevenZip();
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerInnerLayer", Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响结论（句柄可能还被 7z 释放中）。
            }
        }

        // ---------------------------------------------------------------- 第一步：产物里有 .7z.001 → 续解



        /// <summary>失败时把目录树与日志一起带出来（本类别每个用例都用它，省得靠猜）。</summary>
        private static string DiagnoseOutput(Harness harness)
        {
            string tree;

            try
            {
                tree = string.Join(
                    " | ",
                    Directory.GetFileSystemEntries(harness.OutputRoot, "*", SearchOption.AllDirectories));
            }
            catch (Exception ex)
            {
                tree = $"（目录树读不出来：{ex.Message}）";
            }

            return $"\n目录树：{tree}\n日志：\n{string.Join("\n", harness.LogTexts)}";
        }

        // ---------------------------------------------------------------- 形状 A：第一层直接出内容物


        // ---------------------------------------------------------------- 第二步：不重复解压已处理的任务



        // ---------------------------------------------------------------- 第三步：轮数硬上限





        // ---------------------------------------------------------------- 用户的验收判据：一个源包 = 一个目录


        // ---------------------------------------------------------------- 第 33 条：伪装过的下一层



        // ---------------------------------------------------------------- 第 35 条：落点与内层包归属





        /// <summary>造"外层包 → 名字被塞字的内层包 → 内容物"这条链（第 33/35 条共用样本）。</summary>
        private string BuildMangledInnerPackage(string mangledName)
        {
            string innerStage = Path.Combine(_root, "mangle-inner-" + mangledName);
            Directory.CreateDirectory(innerStage);
            WriteText(Path.Combine(innerStage, "payload.txt"), InnerPayloadText);
            Run7z(innerStage, "a", "-t7z", mangledName, "-p" + InnerPassword, "-mhe=on", "payload.txt");

            string outerStage = Path.Combine(_root, "mangle-outer-" + mangledName);
            Directory.CreateDirectory(outerStage);
            File.Copy(Path.Combine(innerStage, mangledName), Path.Combine(outerStage, mangledName), overwrite: true);

            return BuildPackage("mangle-" + Guid.NewGuid().ToString("N") + ".7z", outerStage, mangledName);
        }

        // ---------------------------------------------------------------- 第四步：没有内层包 = 回归


        // ---------------------------------------------------------------- 第四步之二：输出目录被自动改名


        // ---------------------------------------------------------------- 第四步之三：达到密码尝试上限


        // ---------------------------------------------------------------- 第五步：停止后续


        // ---------------------------------------------------------------- 第五步之二：收尾守卫不许误判"没轮到"



        // ---------------------------------------------------------------- 第六步：用户的真实现场


        // ---------------------------------------------------------------- 落点模型 v2：手动档「解压到当前文件夹」


        // ---------------------------------------------------------------- 落点模型 v2：续解层的两种档位










        /// <summary>
        /// 造一个**平铺的两分支**包（不加密）：<c>outer.7z</c> = <c>innerA.7z</c>（payloadA.txt）
        /// + <c>innerB.7z</c>（payloadB.txt）。
        /// </summary>
        private string BuildTwoPlainInnerPackages()
        {
            string build = Path.Combine(_root, "two-plain");
            Directory.CreateDirectory(build);

            WriteText(Path.Combine(build, "payloadA.txt"), "分支 A 的内容\n");
            WriteText(Path.Combine(build, "payloadB.txt"), "分支 B 的内容\n");

            Run7z(build, "a", "-t7z", "innerA.7z", "payloadA.txt");
            Run7z(build, "a", "-t7z", "innerB.7z", "payloadB.txt");

            string outerDirectory = Path.Combine(_root, "two-plain-outer");
            Directory.CreateDirectory(outerDirectory);

            File.Copy(Path.Combine(build, "innerA.7z"), Path.Combine(outerDirectory, "innerA.7z"), overwrite: true);
            File.Copy(Path.Combine(build, "innerB.7z"), Path.Combine(outerDirectory, "innerB.7z"), overwrite: true);

            string outer = Path.Combine(_root, "packages", "two-plain-outer.7z");
            Directory.CreateDirectory(Path.GetDirectoryName(outer)!);

            Run7z(outerDirectory, "a", "-t7z", outer, "innerA.7z", "innerB.7z");

            return outer;
        }

        /// <summary>
        /// 造一个"**一半解不开**"的两分支包：<c>outer.7z</c> = <c>innerA.7z</c>（明文）
        /// + <c>innerB.7z</c>（**设了密码、密码本里没有它** ⇒ 这一单必然解不开）。
        ///
        /// <para>用它来复现用户 2026-10-04 的真机现象：链尾把**从未解开过**的内层包也收进了其余物，
        /// 而其余物在「彻底删除」档下是永久删除。</para>
        /// </summary>
        private string BuildHalfBrokenInnerPackage()
        {
            string build = Path.Combine(_root, "half-broken");
            Directory.CreateDirectory(build);

            WriteText(Path.Combine(build, "payloadA.txt"), "A 的内容\n");
            WriteText(Path.Combine(build, "payloadB.txt"), "B 的内容\n");

            Run7z(build, "a", "-t7z", "innerA.7z", "payloadA.txt");

            // ⛔ 密码故意**不写进密码本**：这一单必然落在"密码错误"上，内容永远解不出来。
            Run7z(build, "a", "-t7z", "innerB.7z", "-p" + UnknownInnerPassword, "-mhe=on", "payloadB.txt");

            string outerDirectory = Path.Combine(_root, "half-broken-outer");
            Directory.CreateDirectory(outerDirectory);

            File.Copy(Path.Combine(build, "innerA.7z"), Path.Combine(outerDirectory, "innerA.7z"), overwrite: true);
            File.Copy(Path.Combine(build, "innerB.7z"), Path.Combine(outerDirectory, "innerB.7z"), overwrite: true);

            string outer = Path.Combine(_root, "packages", "half-broken-outer.7z");
            Directory.CreateDirectory(Path.GetDirectoryName(outer)!);

            Run7z(outerDirectory, "a", "-t7z", outer, "innerA.7z", "innerB.7z");

            return outer;
        }

        /// <summary>这一份文件是不是躺在「其余物」/「过程物」目录里（只看路径层级，不看名字像不像）。</summary>
        private static bool IsInRestDirectory(string path)
        {
            string? directory = Path.GetDirectoryName(path);

            while (!string.IsNullOrWhiteSpace(directory))
            {
                string name = Path.GetFileName(directory);

                if (ProcessArtifactLayout.IsArtifactDirectoryName(name))
                {
                    return true;
                }

                directory = Path.GetDirectoryName(directory);
            }

            return false;
        }

        /// <summary>
        /// 造一条**单链**、最深一层是"一个文件夹 + 里面一个文件"的样本：
        /// <c>outer.7z → level2.7z → … → levelN.7z → 内容物\payload.bin</c>。
        ///
        /// <para>与 <see cref="BuildChain"/> 的差别只有最后一层的内容形状（那个是单文件，
        /// 判定表 1 不套层）—— 落点模型 v2 的"末层那一层"必须用一个文件夹才测得出来。</para>
        /// </summary>
        private string BuildFolderChain(int levelCount)
        {
            Assert.True(levelCount >= 2, "至少要有 outer + 一层内层包");

            string build = Path.Combine(_root, "folder-chain-" + levelCount);
            Directory.CreateDirectory(Path.Combine(build, "内容物"));

            WriteText(Path.Combine(build, "内容物", "payload.bin"), InnerPayloadText);

            Run7z(build, "a", "-t7z", $"level{levelCount}.7z", "-p" + ChainPassword, "-mhe=on", @"内容物\payload.bin");

            for (int level = levelCount - 1; level >= 2; level--)
            {
                Run7z(
                    build,
                    "a",
                    "-t7z",
                    $"level{level}.7z",
                    "-p" + ChainPassword,
                    "-mhe=on",
                    $"level{level + 1}.7z");
            }

            string packageDirectory = Path.Combine(_root, "packages");
            Directory.CreateDirectory(packageDirectory);

            string outer = Path.Combine(packageDirectory, "outer.7z");
            Run7z(build, "a", "-t7z", outer, "-p" + ChainPassword, "-mhe=on", "level2.7z");

            return outer;
        }

        // ---------------------------------------------------------------- 归档起点判定（纯函数）


        // ---------------------------------------------------------------- 样本构造

        private static string LocateSevenZip()
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ArchiveFixer.slnx")))
                {
                    string candidate = Path.Combine(dir.FullName, "src", "ArchiveFixer", "tools", "7zip", "7z.exe");

                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                dir = dir.Parent;
            }

            string local = Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7z.exe");

            if (File.Exists(local))
            {
                return local;
            }

            throw new InvalidOperationException("找不到内置 7z.exe。");
        }

        private void Run7z(string workingDirectory, params object[] args)
        {
            var psi = new ProcessStartInfo(_sevenZip)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory
            };

            foreach (object a in args)
            {
                if (a is IEnumerable<string> many)
                {
                    foreach (string s in many)
                    {
                        psi.ArgumentList.Add(s);
                    }
                }
                else
                {
                    psi.ArgumentList.Add(a.ToString() ?? string.Empty);
                }
            }

            using Process p = Process.Start(psi)!;
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(120_000);

            if (p.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"7z 失败（exit {p.ExitCode}）：{string.Join(' ', psi.ArgumentList)}\n{stdout}\n{stderr}");
            }
        }

        private void WriteText(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        /// <summary>
        /// 造一组加密分卷 <c>inner.7z.001/.002…</c>（40 KB 一卷，保证切得开），落在
        /// <c>&lt;root&gt;/inner-stage</c>，并返回这些分卷的路径。
        /// </summary>
        private List<string> BuildInnerVolumeGroup()
        {
            string stage = InnerStageDirectory;
            Directory.CreateDirectory(stage);

            WriteText(Path.Combine(stage, "payload.txt"), InnerPayloadText);

            byte[] filler = new byte[150 * 1024];
            new Random(20260921).NextBytes(filler);
            File.WriteAllBytes(Path.Combine(stage, "big.bin"), filler);

            // 文件名用相对形式传给 7z（工作目录就是 stage），归档里的条目名才稳定。
            Run7z(stage, "a", "-t7z", "inner.7z", "-v40k", "-p" + InnerPassword, "-mhe=on", "payload.txt", "big.bin");

            List<string> volumes = Directory.GetFiles(stage, "inner.7z.*")
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Assert.True(volumes.Count >= 2, $"应该切出多个分卷，实际 {volumes.Count} 个");

            return volumes;
        }

        /// <summary>把内层分卷打成一个加密包，返回包的路径。</summary>
        private string BuildPackageFromInnerStage(string packageFileName)
        {
            List<string> volumes = Directory.GetFiles(InnerStageDirectory, "inner.7z.*")
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p => Path.GetFileName(p) ?? string.Empty)
                .ToList();

            Assert.NotEmpty(volumes);

            return BuildPackage(packageFileName, InnerStageDirectory, volumes.Cast<object>().ToArray());
        }

        /// <summary>在 <c>&lt;root&gt;/packages</c> 下造一个加密 7z（条目名相对 <paramref name="workingDirectory"/>）。</summary>
        private string BuildPackage(string packageFileName, string workingDirectory, params object[] entryNames)
        {
            string packageDirectory = Path.Combine(_root, "packages");
            Directory.CreateDirectory(packageDirectory);

            string path = Path.Combine(packageDirectory, packageFileName);

            var args = new List<object> { "a", "-t7z", path, "-p" + OuterPassword, "-mhe=on" };
            args.AddRange(entryNames);

            Run7z(workingDirectory, args.ToArray());

            return path;
        }

        /// <summary>
        /// 造一条嵌套链：<c>outer.7z → level2.7z → … → levelN.7z → final.txt</c>。
        /// 每一层的产物里都有一个**新包**，所以它会一直想往下解 ——
        /// 正好用来验证轮数硬上限（不变量 8）与「继续解」（用户 2026-09-24 第 16 条追加）。
        ///
        /// <para>层数由调用方给，而且**必须跟着生效的
        /// <see cref="OneClickCoordinator.RoundLimit"/> 走**：上限从 10 收敛成"设置里的最大嵌套层数
        /// （默认 7）"之后，写死"四层链"的那种样本会**跑到底**、根本碰不到上限，
        /// 测试就从"验证上限"悄悄变成"验证能跑完"（假绿）。</para>
        /// </summary>
        private string BuildChain(int levelCount)
        {
            Assert.True(levelCount >= 2, "至少要有 outer + 一层内层包");

            string build = Path.Combine(_root, "chain-" + levelCount);
            Directory.CreateDirectory(build);

            WriteText(Path.Combine(build, "final.txt"), "最深一层的内容\n");

            // 从最深一层往外套：levelN 里是内容物，levelN-1 里是 levelN，……，outer 里是 level2。
            Run7z(build, "a", "-t7z", $"level{levelCount}.7z", "-p" + ChainPassword, "-mhe=on", "final.txt");

            for (int level = levelCount - 1; level >= 2; level--)
            {
                Run7z(
                    build,
                    "a",
                    "-t7z",
                    $"level{level}.7z",
                    "-p" + ChainPassword,
                    "-mhe=on",
                    $"level{level + 1}.7z");
            }

            string packageDirectory = Path.Combine(_root, "packages");
            Directory.CreateDirectory(packageDirectory);

            string outer = Path.Combine(packageDirectory, "outer.7z");
            Run7z(build, "a", "-t7z", outer, "-p" + ChainPassword, "-mhe=on", "level2.7z");

            return outer;
        }

        /// <summary>
        /// 造用户的真实现场：<c>user.mp4</c> = 假 MP4 头 + 尾部一个完整 ZIP，ZIP 里是加密的 7z 分卷。
        ///
        /// 造法必须是"先把 ZIP 整个建好、再在前面拼数据"：这样 ZIP 的内部偏移才保持相对它自己，
        /// 才是那个"7z 打不开、必须按偏移抠出来"的形态（见 EmbeddedArchiveTests 的说明）。
        /// </summary>
        private string BuildPolyglotUserFile()
        {
            List<string> volumes = BuildInnerVolumeGroup();

            string stage = Path.Combine(_root, "zip-stage");
            Directory.CreateDirectory(stage);

            foreach (string volume in volumes)
            {
                File.Copy(volume, Path.Combine(stage, Path.GetFileName(volume)), overwrite: true);
            }

            string plainZip = Path.Combine(_root, "tail.zip");
            ZipFile.CreateFromDirectory(stage, plainZip);

            string packageDirectory = Path.Combine(_root, "packages");
            Directory.CreateDirectory(packageDirectory);

            string userFile = Path.Combine(packageDirectory, "user.mp4");

            using (var output = new FileStream(userFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                output.Write(BuildFakeVideoPrefix());
                output.Write(File.ReadAllBytes(plainZip));
            }

            return userFile;
        }

        /// <summary>
        /// 造一组**第一层直接出内容物**的加密 7z 分卷（形状 A 的样本，对应 flowe2e 里的
        /// <c>444.7z.001/.002/.003</c>）：<c>&lt;root&gt;/packages/&lt;baseName&gt;.7z.001/.002…</c>。
        ///
        /// 40 KB 一卷，保证真的切得开；内容物是一个 <c>content.txt</c> 加一个随机大文件。
        /// </summary>
        private string BuildVolumeGroupWithContent(string baseName)
        {
            string stage = Path.Combine(_root, baseName + "-stage");
            Directory.CreateDirectory(stage);

            WriteText(Path.Combine(stage, "content.txt"), InnerPayloadText);

            byte[] filler = new byte[150 * 1024];
            new Random(20260922).NextBytes(filler);
            File.WriteAllBytes(Path.Combine(stage, "big.bin"), filler);

            string packageDirectory = Path.Combine(_root, "packages");
            Directory.CreateDirectory(packageDirectory);

            // 文件名用相对形式传给 7z（工作目录就是 stage），归档里的条目名才稳定。
            Run7z(
                stage,
                "a",
                "-t7z",
                Path.Combine(packageDirectory, baseName + ".7z"),
                "-v40k",
                "-p" + OuterPassword,
                "-mhe=on",
                "content.txt",
                "big.bin");

            Assert.True(
                Directory.GetFiles(packageDirectory, baseName + ".7z.*").Length >= 2,
                "样本没有切成多个分卷");

            return packageDirectory;
        }

        /// <summary>假 MP4 头：前 12 字节是真格式的 ftyp box，后面填随机字节（固定种子，样本可复现）。</summary>
        private static byte[] BuildFakeVideoPrefix()
        {
            byte[] prefix = new byte[FakeVideoPrefixLength];
            new Random(20260921).NextBytes(prefix);

            byte[] boxSize = { 0x00, 0x00, 0x00, 0x20 };
            byte[] ftyp = Encoding.ASCII.GetBytes("ftypisom");

            Array.Copy(boxSize, 0, prefix, 0, boxSize.Length);
            Array.Copy(ftyp, 0, prefix, 4, ftyp.Length);

            return prefix;
        }

        private string InnerStageDirectory => Path.Combine(_root, "inner-stage");

        // ---------------------------------------------------------------- 装配

        /// <summary>
        /// 一套真实装配：真 MainViewModel + 真各 Coordinator + 真 7z 引擎（外面包一层计数）。
        /// 缓存根 / 输出目录 / 密码本全部落在临时目录里，绝不碰用户的目录（AGENTS.md §8）。
        /// </summary>
        private Harness CreateHarness(string passwordBookText, Action<AppSettings>? configure = null)
        {
            string dataRoot = Path.Combine(_root, "data");
            string outputRoot = Path.Combine(_root, "out");

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);

            string bookPath = Path.Combine(_root, "密码本.txt");
            WriteText(bookPath, passwordBookText);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CustomOutputDirectory = outputRoot;
            settings.ExtractToOriginalDirectory = false;
            settings.KeepArchiveNameFolder = true;
            settings.PasswordBookPath = bookPath;

            /*
             * 第 32 条之后设置里的默认档是「源包留在原地」；本类全部用例钉的都是"源包进其余物"那条链
             * （含链尾补搬），所以在这里显式选上「放入其余物」，免得用例变成在测默认档。
             */
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);

            /*
             * ⚠ 2026-10-04（用户当场推翻出厂默认档）：默认从「只解当前这一层」改成「展开所有分支」。
             * ⛔⛔ 2026-10-15（本类**正在从旧模型迁到轮次模型**，这段注释是迁移的总说明）：
             *
             * 这里原来钉的是 <c>settings.RecursionMode = "SingleLayer"</c>（"②页「只解当前这一层」那一档"），
             * 意图是让整类用例走**"一轮 = 一层"的续解路**（`Rounds` / `ContinuationLayers` / 目录树都按那条路算）。
             *
             * ⚠ 但它**早就失效了**，而且是静默失效：`settingsService.Save()` 内部会调
             * `AppSettings.Normalize()`，而那里的钉子表把 `RecursionMode` **无条件覆盖成 `AllBranches`**
             * （用户口径「单层路已经删了，现在就是全面解压所有分路」）⇒ 整类其实一直在**测递归内核**，
             * 与类注释担心的"悄悄改成在测递归内核"正好撞上。实测：撤掉那条 pin，本类 23 红 → 2 红。
             *
             * ⇒ 迁移方向（⛔ 不是把 pin 加回去 —— 撤 pin 就违反用户口径）：
             *   ① 与轮次模型一致的事实是 **`Rounds == 1`**（第一大步 = 单一压缩包的**全部**解压，
             *      **保留递归**；内层包由递归内核当场解开，不再靠第二轮去续解）；
             *   ② 所以 `ContinuationLayers == 0`、目录树更深（一次到底）都是**对的**；
             *   ③ 单链档/单层档相关的场景（"按档位停在该层并点名"）随档位一起删除。
             */
            // ⛔ 2026-10-15：这里原来钉 `settings.RecursionMode = "SingleLayer"` —— 那条 pin 是**死的**
            // （`Save()` → `Normalize()` 会把它覆盖成 `AllBranches`，见上面那段迁移说明）⇒ 删掉它，
            // 让夹具的意图与实际跑的路一致（现在跑的就是递归内核 = 唯一的行为）。

            // 7z 路径留空 = 用 ToolLocator 解析出的内置路径。测试目录里也有一份 tools\7zip
            // （由 ArchiveFixer.csproj 的 CopyToOutputDirectory 带过来），所以真引擎在测试里是可用的。
            settings.CustomSevenZipExePath = string.Empty;

            configure?.Invoke(settings);
            settingsService.Save(settings);

            var engine = new CountingEngine(new SevenZipEngine());
            var passwordService = new PasswordService();
            var logService = new LogService(pathService);

            /*
             * MainViewModel 的构造会顺手写两个进程级静态（7z 路径、递归工作区根目录）。
             * 这里先存下来、构造完立刻还原：不然别的测试会拿着"我这边马上要删掉的临时目录"当工作区去解压。
             * （本测试类同时声明成不与其他集合并行，见文件顶部的 CollectionDefinition。）
             */
            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;
            string previousSevenZipPath = ToolLocator.Default.CustomSevenZipExePath;

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
            ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;

            var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), new DialogService());
            var rename = new RenameCoordinator(vm, scan, new RenameService(), new DialogService());
            var extraction = new ExtractionCoordinator(vm, engine, passwordService, pathService, new DialogService());
            var oneClick = new OneClickCoordinator(vm, scan, rename, extraction, new DialogService());

            return new Harness(vm, engine, oneClick, extraction, scan, outputRoot, logService);
        }

        /// <summary>把汇总里的分项数字抠出来求和，用来验证"分项之和 = 本次任务数"这条硬要求。</summary>
        private static (int Sum, int Scope) ParseSummaryCounts(string summary)
        {
            int sum = 0;

            foreach (Match match in Regex.Matches(summary, @"(成功|失败|跳过|部分完成|取消|未处理) (\d+)"))
            {
                sum += int.Parse(match.Groups[2].Value);
            }

            /*
             * 两种 scope 写法都要认：全部处理过时是"本次 N 个任务"，
             * 列表里还有没轮到的时候是"本次 N 个 / 列表共 M 个"（第 16 条追加之后，
             * 撞上限那一批的列表里会多出"已加进列表、等继续解"的内层包，正好走后一种）。
             */
            Match scope = Regex.Match(summary, @"本次 (\d+) 个");

            Assert.True(scope.Success, $"汇总里没有本次 N 个任务：{summary}");

            return (sum, int.Parse(scope.Groups[1].Value));
        }

        private sealed class Harness
        {
            private readonly OneClickCoordinator _oneClick;

            public Harness(
                MainViewModel vm,
                CountingEngine engine,
                OneClickCoordinator oneClick,
                ExtractionCoordinator extraction,
                ScanCoordinator scan,
                string outputRoot,
                LogService log)
            {
                Vm = vm;
                Engine = engine;
                _oneClick = oneClick;
                Extraction = extraction;
                Scan = scan;
                OutputRoot = outputRoot;
                Log = log;
            }

            public MainViewModel Vm { get; }

            public CountingEngine Engine { get; }

            /// <summary>手动那条解压入口（「只解压」/「解压到当前文件夹」走它）—— 落点模型 v2 的用例要直接调它。</summary>
            public ExtractionCoordinator Extraction { get; }

            /// <summary>
            /// 扫描协调器。用例要直接调它做的只有一件事：复刻"一键处理开工前把无用物移出列表"
            /// 那一步（<see cref="ScanCoordinator.RemoveJunkTasksFromListAsync"/>）。
            /// </summary>
            public ScanCoordinator Scan { get; }

            /// <summary>一键处理协调器：用例要读**生效的轮数上限**（<c>RoundLimit</c>），⛔ 不许写死常量。</summary>
            public OneClickCoordinator OneClick => _oneClick;

            public string OutputRoot { get; }

            /// <summary>
            /// 真 LogService：屏幕日志集合（<c>Logs</c>）在无 WPF 应用的测试进程里照样会被填充
            /// （MainViewModel.AppendLog 走的就是它），所以"有没有写这条日志"可以直接断言。
            /// </summary>
            public LogService Log { get; }

            /// <summary>屏幕日志的文本（断言"写没写这条日志"用；与既有同类测试同一写法）。</summary>
            public IEnumerable<string> LogTexts => Log.Logs.Select(item => item.DisplayText);

            public Task AddPathsAsync(params string[] paths) => Vm.AddPathsAsync(paths);

            /// <summary>
            /// 跑一键处理的**流程部分**（不弹任何对话框 —— 测试里没人在那儿点确定）。
            /// 走的是 RunAsync 内部同一个 RunPipelineAsync。
            /// </summary>
            public Task<OneClickOutcome> RunOneClickAsync() => _oneClick.RunPipelineAsync(Vm.Tasks.ToList());
        }

        /// <summary>
        /// 真引擎外面包一层，只为**数调用**：测试要证明"第 2 轮没有再解一遍已经处理过的包"，
        /// 只能看引擎实际被调了几次、拿的是哪个文件。
        /// </summary>
        private sealed class CountingEngine : IArchiveEngine
        {
            private readonly IArchiveEngine _inner;

            public CountingEngine(IArchiveEngine inner)
            {
                _inner = inner;
            }

            public List<string> ExtractCalls { get; } = new();

            /// <summary>每次解压前回调（用来模拟"用户在批次中途点了停止后续"）。</summary>
            public Action<ArchiveRequest>? OnExtract { get; set; }

            public string Id => _inner.Id;

            public string DisplayName => _inner.DisplayName;

            public string Version => _inner.Version;

            public bool IsAvailable => _inner.IsAvailable;

            public EngineCapabilities Capabilities => _inner.Capabilities;

            public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                return _inner.ProbeAsync(request, cancellationToken);
            }

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                return _inner.ListAsync(request, cancellationToken);
            }

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                return _inner.TestAsync(request, cancellationToken);
            }

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                ExtractCalls.Add(request.ArchivePath);
                OnExtract?.Invoke(request);

                return _inner.ExtractAsync(request, options, cancellationToken);
            }
        }
    }
}
