using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 密码列表的**本机加密记忆**（用户 2026-09-24 真机反馈后拍板）：
    /// 列表的内容 / 顺序 / 启用状态 / 手工条目要跨重启保留，并且支持多本密码本。
    ///
    /// <para>被测的三层：<see cref="PasswordListStore"/>（DPAPI 机器范围加密 + 原子写 + 容错读）、
    /// <see cref="PasswordService"/>（每次变更落盘、墓碑、多本合并）、
    /// <see cref="MainViewModel"/> 的启动接线（先记忆、再逐本合并、日志只写条数与文件名）。</para>
    ///
    /// <para><b>隐私（AGENTS.md §8）</b>：这里出现的密码全是合成占位符（<c>&lt;示例密码N&gt;</c> 风格），
    /// 路径全部落在系统临时目录下 —— 不含任何真实密码、真实站点名或用户路径。</para>
    /// </summary>
    // 碰进程级静态（构造 MainViewModel 会写工作区根、并读 WorkspaceRootIndex 账本）：
    // 与同类用例串行跑，不与别的集合并行 —— 见 InnerLayerContinuationTests 顶部的 CollectionDefinition。
    [Collection("ArchiveFixerGlobalState")]
    public sealed class PasswordListStoreTests
    {
        // ------------------------------------------------------------------ ① 往返一致

        [Fact]
        public void 记忆_保存后新实例加载_值顺序启用状态来源逐项一致()
        {
            using var dir = new TempDir();

            PasswordService first = CreateService(dir.Path);

            first.AddPassword("<示例密码1>");
            first.AddPassword("<示例密码2>");
            first.AddPassword("<示例密码3>");

            // 启用状态与来源都由界面直接改（复选框 / 备注列）—— 正是最容易漏掉落盘的那两种。
            first.Passwords[1].IsEnabled = false;
            first.Passwords[1].Remark = "备用（合成备注）";
            first.Passwords[2].Source = "ImportedList";

            PasswordService second = CreateService(dir.Path);

            Assert.Equal(PasswordListLoadStatus.Loaded, second.LoadRememberedList());
            Assert.Equal(3, second.Passwords.Count);

            Assert.Equal("<示例密码1>", second.Passwords[0].Value);
            Assert.Equal("<示例密码2>", second.Passwords[1].Value);
            Assert.Equal("<示例密码3>", second.Passwords[2].Value);

            Assert.True(second.Passwords[0].IsEnabled);
            Assert.False(second.Passwords[1].IsEnabled);
            Assert.True(second.Passwords[2].IsEnabled);

            Assert.Equal("ManualList", second.Passwords[0].Source);
            Assert.Equal("ImportedList", second.Passwords[2].Source);
            Assert.Equal("备用（合成备注）", second.Passwords[1].Remark);

            // 手工条目与顺序都要留住：这正是用户报的那件事。
            Assert.Equal(3, second.RememberedEntryCount);
        }

        // ------------------------------------------------------------------ ② 文件里没有明文

        [Fact]
        public void 记忆_文件里逐字节搜不到任何密码明文()
        {
            using var dir = new TempDir();

            string[] secrets = { "<示例密码1>", "<示例密码2>", " <示例密码带空格> " };

            PasswordService service = CreateService(dir.Path);

            foreach (string secret in secrets)
            {
                service.AddPassword(secret);
            }

            string dataFile = DataFile(dir.Path);

            Assert.True(File.Exists(dataFile), "开了「记住密码列表」就该真的落盘");

            byte[] bytes = File.ReadAllBytes(dataFile);

            foreach (string secret in secrets)
            {
                Assert.False(
                    ContainsBytes(bytes, Encoding.UTF8.GetBytes(secret)),
                    "UTF-8 形态的明文不该出现在记忆文件里");

                Assert.False(
                    ContainsBytes(bytes, Encoding.Unicode.GetBytes(secret)),
                    "UTF-16LE 形态的明文不该出现在记忆文件里");

                Assert.False(
                    ContainsBytes(bytes, Encoding.ASCII.GetBytes(secret)),
                    "ASCII 形态的明文不该出现在记忆文件里");
            }

            // 顺带钉住"真的是密文"：整份文件里也搜不到 JSON 的结构键。
            Assert.False(ContainsBytes(bytes, Encoding.ASCII.GetBytes("\"entries\"")));
        }

        // ------------------------------------------------------------------ ③ 顺序跨重启

        [Fact]
        public void 记忆_上移下移后的顺序跨重启保持()
        {
            using var dir = new TempDir();

            PasswordService first = CreateService(dir.Path);

            first.AddPassword("<示例密码1>");
            first.AddPassword("<示例密码2>");
            first.AddPassword("<示例密码3>");

            // 把第三条上移一位：1 → 3 → 2
            Assert.True(first.MoveUp(first.Passwords[2]));

            PasswordService second = CreateService(dir.Path);
            second.LoadRememberedList();

            Assert.Equal(
                new[] { "<示例密码1>", "<示例密码3>", "<示例密码2>" },
                Values(second));

            // 再下移一位：1 → 2 → 3，重启后同样是 1 → 2 → 3
            Assert.True(second.MoveDown(second.Passwords[1]));

            PasswordService third = CreateService(dir.Path);
            third.LoadRememberedList();

            Assert.Equal(
                new[] { "<示例密码1>", "<示例密码2>", "<示例密码3>" },
                Values(third));
        }

        // ------------------------------------------------------------------ ④ 删除不复活

        [Fact]
        public void 记忆_删除后重启不复活_书里还有该值时墓碑生效()
        {
            using var dir = new TempDir();

            string book = dir.WriteText("book.txt", "<示例密码1>\n<示例密码2>\n<示例密码3>\n", new UTF8Encoding(false));

            PasswordService first = CreateService(dir.Path);

            Assert.Equal(3, first.MergePasswordBook(book, out int parsed, out _, out _));
            Assert.Equal(3, parsed);

            // 删掉中间那一条（密码本 txt 里照样还留着它）
            Assert.True(first.RemovePassword(first.Passwords[1]));

            PasswordService second = CreateService(dir.Path);
            second.LoadRememberedList();

            Assert.Equal(
                new[] { "<示例密码1>", "<示例密码3>" },
                Values(second));

            // 启动时的合并：书里还有它，但墓碑说了算 —— 不许复活
            int added = second.MergePasswordBook(book, out parsed, out int skipped, out _);

            Assert.Equal(3, parsed);
            Assert.Equal(1, skipped);
            Assert.Equal(0, added);
            Assert.DoesNotContain("<示例密码2>", Values(second));
        }

        // ------------------------------------------------------------------ ⑤ 清空不复活

        [Fact]
        public void 记忆_清空后重启不复活()
        {
            using var dir = new TempDir();

            string book = dir.WriteText("book.txt", "<示例密码1>\n<示例密码2>\n", new UTF8Encoding(false));

            PasswordService first = CreateService(dir.Path);

            first.MergePasswordBook(book, out _, out _, out _);
            first.ClearPasswords();

            PasswordService second = CreateService(dir.Path);
            second.LoadRememberedList();

            Assert.Empty(second.Passwords);

            int added = second.MergePasswordBook(book, out int parsed, out int skipped, out _);

            Assert.Equal(2, parsed);
            Assert.Equal(2, skipped);
            Assert.Equal(0, added);
            Assert.Empty(second.Passwords);
        }

        // ------------------------------------------------------------------ ⑥ 多本合并

        [Fact]
        public void 记忆_多本合并_记忆里的顺序保留_各本书的新条目按书顺序追加()
        {
            using var dir = new TempDir();

            string book1 = dir.WriteText("book1.txt", "P1\nP3\n", new UTF8Encoding(false));
            string book2 = dir.WriteText("book2.txt", "P2\nP4\n", new UTF8Encoding(false));
            string book3 = dir.WriteText("book3.txt", "P5\nP1\n", new UTF8Encoding(false));

            PasswordService first = CreateService(dir.Path);

            first.SetRememberedBookPaths(new[] { book1, book2 });

            // 两个包都解不开密码的批次：一次也别试空候选之外的东西 —— 这里只关心合并顺序。
            Assert.Equal(2, first.MergePasswordBook(book1, out _, out _, out _));
            Assert.Equal(2, first.MergePasswordBook(book2, out _, out _, out _));

            // 第一轮：各本书的新条目按"书顺序"追加 → P1 P3 P2 P4
            Assert.Equal(new[] { "P1", "P3", "P2", "P4" }, Values(first));

            // 手工加一条并把它调到最前（用户在窗口里就是这么干的）
            first.AddPassword("M");

            PasswordItem manual = first.Passwords.First(item => item.Value == "M");

            for (int i = 0; i < 4; i++)
            {
                first.MoveUp(manual);
            }

            Assert.Equal(new[] { "M", "P1", "P3", "P2", "P4" }, Values(first));

            // 重启 + 又记住了一本：记忆里的顺序保留，新书里"还没有的"补到末尾
            PasswordService second = CreateService(dir.Path);
            second.LoadRememberedList();

            Assert.Equal(new[] { "M", "P1", "P3", "P2", "P4" }, Values(second));
            Assert.Equal(5, second.RememberedEntryCount);

            second.SetRememberedBookPaths(new[] { book1, book2, book3 });

            Assert.Equal(0, second.MergePasswordBook(book1, out _, out _, out _));
            Assert.Equal(0, second.MergePasswordBook(book2, out _, out _, out _));

            // book3 里 P1 已经有了（跳过）、P5 是新值（追加到末尾）
            Assert.Equal(1, second.MergePasswordBook(book3, out _, out _, out _));

            Assert.Equal(new[] { "M", "P1", "P3", "P2", "P4", "P5" }, Values(second));
        }

        // ------------------------------------------------------------------ ⑦ 导入记住路径

        [Fact]
        public void 记忆_导入新书后路径被记住且不重复_并且导入不记墓碑()
        {
            using var dir = new TempDir();

            string book = dir.WriteText("book.txt", "<示例密码1>\n", new UTF8Encoding(false));

            PasswordService first = CreateService(dir.Path);

            first.ImportPasswordList(book);
            first.ImportPasswordList(book);

            Assert.Single(first.RememberedBookPaths);
            Assert.Equal(book, first.RememberedBookPaths[0]);

            // 删掉之后再导入同一个文件 = "再加回来"：不许被自己刚记下的墓碑吃掉。
            Assert.True(first.RemovePassword(first.Passwords[0]));
            Assert.Empty(first.Passwords);

            first.ImportPasswordList(book);

            Assert.Single(first.Passwords);
            Assert.Equal("<示例密码1>", first.Passwords[0].Value);
            Assert.Empty(first.Tombstones);

            // 记住的路径跨重启仍在（用户 2026-09-21 的"导入一次就一直有效"）。
            PasswordService second = CreateService(dir.Path);
            second.LoadRememberedList();

            Assert.Single(second.RememberedBookPaths);
            Assert.Equal(book, second.RememberedBookPaths[0]);
        }

        // ------------------------------------------------------------------ ⑧ 关掉开关

        [Fact]
        public void 记忆_关闭开关_不写文件也不读_文件字节未变()
        {
            using var dir = new TempDir();

            // 先正常存一份
            PasswordService on = CreateService(dir.Path);
            on.AddPassword("<示例密码1>");

            string dataFile = DataFile(dir.Path);
            byte[] before = File.ReadAllBytes(dataFile);

            // 关掉开关之后：读也不读、写也不写
            PasswordService off = CreateService(dir.Path, remember: false);

            Assert.Equal(PasswordListLoadStatus.Disabled, off.LoadRememberedList());
            Assert.Empty(off.Passwords);

            off.AddPassword("<示例密码2>");
            off.AddPassword("<示例密码3>");
            off.MoveUp(off.Passwords[1]);
            off.RemovePassword(off.Passwords[0]);
            off.ClearPasswords();

            Assert.Equal(before, File.ReadAllBytes(dataFile));

            // 另一个目录：关着开关时连文件都不该创建
            using var other = new TempDir();

            PasswordService offFresh = CreateService(other.Path, remember: false);
            offFresh.AddPassword("<示例密码4>");

            Assert.False(File.Exists(DataFile(other.Path)));
        }

        // ------------------------------------------------------------------ ⑨ 篡改一字节

        [Fact]
        public void 记忆_篡改密文一个字节_忽略且原文件未被覆盖()
        {
            using var dir = new TempDir();

            PasswordService first = CreateService(dir.Path);
            first.AddPassword("<示例密码1>");
            first.AddPassword("<示例密码2>");

            string dataFile = DataFile(dir.Path);

            byte[] tampered = File.ReadAllBytes(dataFile);
            int index = tampered.Length / 2;
            tampered[index] ^= 0xFF;
            File.WriteAllBytes(dataFile, tampered);

            PasswordService second = CreateService(dir.Path);

            Assert.Equal(PasswordListLoadStatus.Undecryptable, second.LoadRememberedList());
            Assert.Empty(second.Passwords);
            Assert.NotEmpty(second.LastListWarning);

            // 读失败**绝不覆盖、绝不删除**：把改过的那份原样留在盘上（换回原机还能用）。
            Assert.Equal(tampered, File.ReadAllBytes(dataFile));
        }

        // ------------------------------------------------------------------ ⑨b 读失败先备份

        [Fact]
        public void 记忆_读不出来时先另存备份_之后落盘不再毁掉原件()
        {
            /*
             * 为什么单列一条（2026-09-29 复核逮到的数据风险）：
             * 老代码读失败时只写一句"已忽略、文件没被动"就返回，而**界面文案还写着**
             * "程序没有覆盖、也没有删除那份记忆" —— 可启动接线紧接着就会按密码本清单落盘一次，
             * Save 是替换式的：那份解不开的记忆（换机器 / 重装系统之后的正常现象）当场被覆盖。
             * 这一条钉住新口径：**读失败先把原件另存一份备份**，后面的写盘随便写。
             */
            using var dir = new TempDir();

            PasswordService first = CreateService(dir.Path);
            first.AddPassword("<示例密码1>");

            string dataFile = DataFile(dir.Path);

            byte[] tampered = File.ReadAllBytes(dataFile);
            tampered[tampered.Length / 2] ^= 0xFF;
            File.WriteAllBytes(dataFile, tampered);

            PasswordService second = CreateService(dir.Path);

            Assert.Equal(PasswordListLoadStatus.Undecryptable, second.LoadRememberedList());

            // ① 备份真的落了盘，而且与"读不出来时盘上那一份"逐字节相同
            string[] backups = Directory.GetFiles(dir.Path, "*.unreadable-*.bak");

            Assert.Single(backups);
            Assert.Equal(tampered, File.ReadAllBytes(backups[0]));

            // ② 文案如实点名备份文件（老文案说"没有覆盖"，而下一步就会覆盖）
            Assert.Contains(Path.GetFileName(backups[0]), second.LastListWarning, StringComparison.Ordinal);
            Assert.DoesNotContain(dir.Path, second.LastListWarning, StringComparison.OrdinalIgnoreCase);

            // ③ 之后正常落盘（启动接线就会做这件事）→ 正式文件被替换，备份一字不差
            second.AddPassword("<示例密码2>");

            Assert.NotEqual(tampered, File.ReadAllBytes(dataFile));
            Assert.Equal(tampered, File.ReadAllBytes(backups[0]));
        }

        // ------------------------------------------------------------------ ⑩ 半截 / 随机字节

        [Fact]
        public void 记忆_半截文件或随机字节_一律忽略且文件未变()
        {
            using var dir = new TempDir();

            string dataFile = DataFile(dir.Path);

            // ① 随机字节
            byte[] garbage = { 0x01, 0x02, 0x03, 0x04, 0x05 };
            File.WriteAllBytes(dataFile, garbage);

            PasswordService first = CreateService(dir.Path);
            PasswordListLoadStatus status = first.LoadRememberedList();

            Assert.NotEqual(PasswordListLoadStatus.Loaded, status);
            Assert.Empty(first.Passwords);
            Assert.Equal(garbage, File.ReadAllBytes(dataFile));

            // ② 真的密文被砍掉一半
            PasswordService writer = CreateService(dir.Path);
            writer.AddPassword("<示例密码1>");

            byte[] full = File.ReadAllBytes(dataFile);
            byte[] half = full.Take(full.Length / 2).ToArray();
            File.WriteAllBytes(dataFile, half);

            PasswordService second = CreateService(dir.Path);

            Assert.NotEqual(PasswordListLoadStatus.Loaded, second.LoadRememberedList());
            Assert.Empty(second.Passwords);
            Assert.Equal(half, File.ReadAllBytes(dataFile));

            // ③ 空文件（写到一半被打断的现场）
            File.WriteAllBytes(dataFile, Array.Empty<byte>());

            PasswordService third = CreateService(dir.Path);

            Assert.NotEqual(PasswordListLoadStatus.Loaded, third.LoadRememberedList());
            Assert.Empty(third.Passwords);
            Assert.Equal(0, new FileInfo(dataFile).Length);
        }

        // ------------------------------------------------------------------ ⑪ 写失败

        [Fact]
        public void 记忆_目标只读导致写失败_不崩_有明确原因_旧文件保住()
        {
            using var dir = new TempDir();

            PasswordService first = CreateService(dir.Path);
            first.AddPassword("<示例密码1>");

            string dataFile = DataFile(dir.Path);
            byte[] before = File.ReadAllBytes(dataFile);

            // 把记忆文件设成只读：两条替换路径（File.Replace / 覆盖式 Move）都会失败。
            File.SetAttributes(dataFile, FileAttributes.ReadOnly);

            try
            {
                first.AddPassword("<示例密码2>");

                Assert.NotEmpty(first.LastListWarning);
                Assert.DoesNotContain("<示例密码2>", first.LastListWarning, StringComparison.Ordinal);

                // 旧文件逐字节未变
                Assert.Equal(before, File.ReadAllBytes(dataFile));

                // 不留半个临时文件
                Assert.False(File.Exists(dataFile + ".tmp"));
            }
            finally
            {
                File.SetAttributes(dataFile, FileAttributes.Normal);
            }

            // 去掉只读之后，盘上那份仍然是**完整的旧记忆**（一条，不是两条）。
            PasswordService second = CreateService(dir.Path);

            Assert.Equal(PasswordListLoadStatus.Loaded, second.LoadRememberedList());
            Assert.Equal(new[] { "<示例密码1>" }, Values(second));
        }

        // ------------------------------------------------------------------ ⑫ 原子写

        [Fact]
        public void 记忆_原子写_写失败时旧文件仍是完整可读的()
        {
            using var dir = new TempDir();

            var store = new PasswordListStore { DataRootDirectory = dir.Path };

            var first = new PasswordListSnapshot();
            first.Entries.Add(new PasswordListEntry { Value = "<示例密码1>", Source = "ManualList" });
            first.BookPaths.Add(Path.Combine(dir.Path, "book.txt"));

            Assert.True(store.Save(first).Success);

            byte[] before = File.ReadAllBytes(DataFile(dir.Path));

            // 目标只读 → 替换失败
            File.SetAttributes(DataFile(dir.Path), FileAttributes.ReadOnly);

            PasswordListSaveResult failed;

            try
            {
                var second = new PasswordListSnapshot();
                second.Entries.Add(new PasswordListEntry { Value = "<示例密码2>", Source = "ManualList" });

                failed = store.Save(second);
            }
            finally
            {
                File.SetAttributes(DataFile(dir.Path), FileAttributes.Normal);
            }

            Assert.False(failed.Success);
            Assert.NotEmpty(failed.FailureReason);

            // 失败原因里只有文件名，**没有完整路径**（§8：个人路径不入日志）
            Assert.Contains(PasswordListStore.FileName, failed.FailureReason, StringComparison.Ordinal);
            Assert.DoesNotContain(dir.Path, failed.FailureReason, StringComparison.OrdinalIgnoreCase);

            // 旧文件逐字节完好，并且**仍然能被完整读回来**
            Assert.Equal(before, File.ReadAllBytes(DataFile(dir.Path)));

            PasswordListLoadResult reloaded = store.Load();

            Assert.True(reloaded.HasMemory);
            Assert.Single(reloaded.Snapshot.Entries);
            Assert.Equal("<示例密码1>", reloaded.Snapshot.Entries[0].Value);
            Assert.Single(reloaded.Snapshot.BookPaths);

            Assert.False(File.Exists(DataFile(dir.Path) + ".tmp"));
        }

        // ------------------------------------------------------------------ ⑬ 老配置迁移

        [Fact]
        public void 设置_老配置的PasswordBookPath迁移进PasswordBookPaths()
        {
            // ① 内存里迁移：老字段保留（回退到旧版本时那一边还认得它），清单里补上那一本，且不重复。
            var settings = new AppSettings { PasswordBookPath = @"X:\<示例目录>\<示例密码本>.txt" };

            settings.Normalize();

            Assert.Single(settings.PasswordBookPaths!);
            Assert.Equal(@"X:\<示例目录>\<示例密码本>.txt", settings.PasswordBookPaths![0]);
            Assert.Equal(@"X:\<示例目录>\<示例密码本>.txt", settings.PasswordBookPath);

            settings.Normalize();

            Assert.Single(settings.PasswordBookPaths!);

            // ② 真的从一份**旧格式的 appsettings.json** 读进来（这是用户机器上的实际形态）
            using var dir = new TempDir();

            File.WriteAllText(
                Path.Combine(dir.Path, "appsettings.json"),
                "{\"PasswordBookPath\":\"X:\\\\<示例目录>\\\\<示例密码本>.txt\",\"EnableLog\":true}",
                new UTF8Encoding(false));

            var settingsService = new SettingsService(new PathService { DataRootDirectory = dir.Path });

            AppSettings loaded = settingsService.Load();

            Assert.Single(loaded.PasswordBookPaths!);
            Assert.Equal(@"X:\<示例目录>\<示例密码本>.txt", loaded.PasswordBookPaths![0]);

            // ③ 新字段缺失时默认值是"记住密码列表 = 开"
            Assert.True(loaded.RememberPasswordList);
        }

        // ------------------------------------------------------------------ ⑭ 空列表边界

        [Fact]
        public void 记忆_空列表保存与加载的边界()
        {
            using var dir = new TempDir();

            // ① 空快照也能写、也能读回来（用户把列表清空之后就是这个状态）
            var store = new PasswordListStore { DataRootDirectory = dir.Path };

            Assert.True(store.Save(new PasswordListSnapshot()).Success);

            PasswordService service = CreateService(dir.Path);

            Assert.Equal(PasswordListLoadStatus.Loaded, service.LoadRememberedList());
            Assert.Empty(service.Passwords);
            Assert.Empty(service.RememberedBookPaths);
            Assert.Empty(service.LastListWarning);

            // ② 从来没有过这个文件：NoFile（不是错误，也不该吓唬用户）
            using var other = new TempDir();

            PasswordService fresh = CreateService(other.Path);

            Assert.Equal(PasswordListLoadStatus.NoFile, fresh.LoadRememberedList());
            Assert.Empty(fresh.Passwords);
            Assert.Empty(fresh.LastListWarning);

            // ③ 数据根目录没装配好：明确失败，不抛异常
            var unconfigured = new PasswordListStore { DataRootDirectory = string.Empty };

            Assert.Equal(PasswordListLoadStatus.Unreadable, unconfigured.Load().Status);
            Assert.False(unconfigured.Save(new PasswordListSnapshot()).Success);
        }

        // ------------------------------------------------------------------ ⑮ 日志里没有明文也没有路径

        [Fact]
        public void 记忆_日志与降级日志里搜不到明文且只有文件名没有完整路径()
        {
            using var dir = new TempDir();

            var pathService = new PathService { DataRootDirectory = dir.Path };
            var logService = new LogService(pathService);

            logService.Initialize(enableFileLog: true);

            var passwordService = new PasswordService
            {
                DataRootDirectory = dir.Path,
                RememberPasswordList = true
            };

            // ① 写失败（目标只读）→ 那条 WARN
            passwordService.AddPassword("<示例密码1>");

            string dataFile = DataFile(dir.Path);
            File.SetAttributes(dataFile, FileAttributes.ReadOnly);

            string saveWarning;

            try
            {
                passwordService.AddPassword("<示例密码2>");
                saveWarning = passwordService.LastListWarning;
            }
            finally
            {
                File.SetAttributes(dataFile, FileAttributes.Normal);
            }

            logService.WriteWarning(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.PasswordListMemorySaveFailedLogFormat,
                saveWarning));

            // ② 读失败（篡改一个字节）→ 降级提示 + 那条 WARN
            byte[] tampered = File.ReadAllBytes(dataFile);
            tampered[tampered.Length / 2] ^= 0xFF;
            File.WriteAllBytes(dataFile, tampered);

            PasswordService second = new PasswordService
            {
                DataRootDirectory = dir.Path,
                RememberPasswordList = true
            };

            Assert.Equal(PasswordListLoadStatus.Undecryptable, second.LoadRememberedList());
            Assert.NotEmpty(second.LastListWarning);

            logService.WriteWarning(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.PasswordListMemoryLoadFailedLogFormat,
                second.LastListWarning));

            string logText = File.ReadAllText(logService.CurrentLogFilePath);

            // 明文在任何一条日志里都搜不到（含首尾空格那条的两种形态）
            Assert.DoesNotContain("<示例密码1>", logText, StringComparison.Ordinal);
            Assert.DoesNotContain("<示例密码2>", logText, StringComparison.Ordinal);

            // 完整个人路径不入日志；文件名要在（否则排障时看不出是哪个文件）
            Assert.DoesNotContain(dir.Path, logText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(PasswordListStore.FileName, logText, StringComparison.Ordinal);

            // 给用户看的那两句本身也不含路径与明文
            Assert.DoesNotContain(dir.Path, second.LastListWarning, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<示例密码1>", second.LastListWarning, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ ⑯ 落点

        [Fact]
        public void 记忆_文件落在DataRootDirectory下而不是C盘或用户目录()
        {
            using var dir = new TempDir();

            var store = new PasswordListStore { DataRootDirectory = dir.Path };

            Assert.Equal(Path.Combine(dir.Path, PasswordListStore.FileName), store.FilePath);
            Assert.Equal("password-list.dat", PasswordListStore.FileName);

            Assert.True(store.Save(new PasswordListSnapshot()).Success);
            Assert.True(File.Exists(Path.Combine(dir.Path, "password-list.dat")));

            // 默认数据根目录 = **程序目录下的 data**（绿色软件跟着安装位置走，绝不写 C 盘用户目录）
            string defaultRoot = PathService.DefaultDataRootDirectory;

            Assert.StartsWith(AppContext.BaseDirectory, defaultRoot, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                defaultRoot,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                defaultRoot,
                StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------ ⑰ 启动接线（MainViewModel）

        [Fact]
        public void 启动_先加载记忆再逐本合并_日志只写条数与文件名()
        {
            // 这一条断言的是**日志全文**（不许出现完整路径），所以必须先把这个进程级的
            // "本次会话用过的工作区根"账本清掉：它是静态的，别的用例类记进去的根会留在表里，
            // MainViewModel 启动时照着它扫残留、一旦扫到就写一行"看了哪几个根"（`；` 连接），
            // 那一行里必然带本用例自己的完整路径 —— 于是这条用例在全量并发跑时假红（单跑是绿的）。
            WorkspaceRootIndex.ResetForTests();

            using var dir = new TempDir();

            string book1 = dir.WriteText("book1.txt", "P1\nP2\n", new UTF8Encoding(false));
            string book2 = dir.WriteText("book2.txt", "P3\n", new UTF8Encoding(false));

            // 上一轮运行留下的记忆：手工条目 M + 记住两本书 + 一条墓碑（P9 删过）
            var recorder = new PasswordService
            {
                DataRootDirectory = dir.Path,
                RememberPasswordList = true
            };

            recorder.SetRememberedBookPaths(new[] { book1, book2 });
            recorder.AddPassword("M");
            recorder.AddPassword("P9");
            recorder.RemovePassword(recorder.Passwords.First(p => p.Value == "P9"));

            // 这一轮启动：先记忆、再按顺序合并两本书。
            //
            // ⚠ 这里刻意走**真的启动路径**（构造 MainViewModel 时它自己会 Initialize → AutoLoadPasswordBook），
            // 而不是手工调某一步 —— 用户报的正是"重启之后列表不对"，这条链路必须整条被测到。
            var pathService = new PathService { DataRootDirectory = dir.Path };
            var logService = new LogService(pathService);
            var settingsService = new SettingsService(pathService);

            /*
             * 先把设置写好再建 ViewModel：MainViewModel 的构造里会 Load 一次设置，
             * 而**缓存根目录**决定数据根目录（留空会回落到"程序目录\data"）——
             * 不写这一笔，这个用例就会去动测试输出目录下的公共 data\，既不可重复也不干净。
             */
            AppSettings settings = AppSettings.CreateDefault();
            settings.PasswordBookPaths = new List<string> { book1, book2 };
            settings.PasswordBookPath = book2;

            settingsService.Save(settings);

            var passwordService = new PasswordService { DataRootDirectory = dir.Path };

            var vm = new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                new Engines.SevenZip.SevenZipEngine(),
                passwordService,
                logService,
                settingsService,
                pathService,
                new TaskSummaryService(),
                new ClipboardService(),
                new DialogService());

            // 记忆里的手工条目在第一，随后是按书顺序补进来的条目
            Assert.Equal(new[] { "M", "P1", "P2", "P3" }, Values(passwordService));

            Assert.Equal(dir.Path, passwordService.DataRootDirectory);

            // 一条 INFO 说清"记忆 N 条 + M 本密码本补了 K 条"
            string logText = File.ReadAllText(logService.CurrentLogFilePath);

            Assert.Contains("恢复 1 条", logText, StringComparison.Ordinal);
            Assert.Contains("2 本密码本", logText, StringComparison.Ordinal);
            Assert.Contains("补充 3 条", logText, StringComparison.Ordinal);

            // 日志里只有文件名，没有完整路径
            Assert.Contains("book1.txt", logText, StringComparison.Ordinal);
            Assert.DoesNotContain(dir.Path, logText, StringComparison.OrdinalIgnoreCase);

            // 主界面摘要如实反映"两本密码本"
            Assert.Contains("2 本密码本", vm.PasswordBookSummary, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ ⑱ 书顺序

        [Fact]
        public void 启动_书的合并顺序按记住的顺序走()
        {
            // 设置里是权威集合，记忆里是顺序：两者合并后顺序以记忆为准。
            var ordered = MainViewModel.OrderBookPaths(
                new[] { @"X:\<示例目录>\b.txt", @"X:\<示例目录>\a.txt" },
                new[] { @"X:\<示例目录>\a.txt", @"X:\<示例目录>\b.txt" });

            Assert.Equal(new[] { @"X:\<示例目录>\b.txt", @"X:\<示例目录>\a.txt" }, ordered);

            // 用户从设置里移除过一本：不许因为记忆里还有就加回来。
            var afterRemoval = MainViewModel.OrderBookPaths(
                new[] { @"X:\<示例目录>\b.txt", @"X:\<示例目录>\a.txt" },
                new[] { @"X:\<示例目录>\a.txt" });

            Assert.Equal(new[] { @"X:\<示例目录>\a.txt" }, afterRemoval);

            // 设置里一本都没有（配置被重置）而记忆里有 → 按记忆恢复，等于自愈一次。
            var recovered = MainViewModel.OrderBookPaths(
                new[] { @"X:\<示例目录>\b.txt" },
                Array.Empty<string>());

            Assert.Equal(new[] { @"X:\<示例目录>\b.txt" }, recovered);
        }

        // ================================================================ 装配

        private static string DataFile(string root) => Path.Combine(root, PasswordListStore.FileName);

        private static PasswordService CreateService(string dataRoot, bool remember = true)
        {
            return new PasswordService
            {
                DataRootDirectory = dataRoot,
                RememberPasswordList = remember
            };
        }

        private static string[] Values(PasswordService service) =>
            service.Passwords.Select(item => item.Value).ToArray();

        private static bool ContainsBytes(byte[] haystack, byte[] needle)
        {
            if (needle.Length == 0 || haystack.Length < needle.Length)
            {
                return false;
            }

            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                bool match = true;

                for (int j = 0; j < needle.Length && match; j++)
                {
                    match = haystack[i + j] == needle[j];
                }

                if (match)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>每个用例自建临时目录，Dispose 时整目录删掉（只读属性先摘掉，否则删不掉）。</summary>
        private sealed class TempDir : IDisposable
        {
            public TempDir()
            {
                Path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "ArchiveFixerPasswordListStore",
                    Guid.NewGuid().ToString("N"));

                Directory.CreateDirectory(Path);
            }

            public string Path { get; }

            public string WriteText(string relativePath, string content, Encoding encoding)
            {
                string full = System.IO.Path.Combine(Path, relativePath);

                File.WriteAllText(full, content, encoding);

                return full;
            }

            public void Dispose()
            {
                try
                {
                    if (!Directory.Exists(Path))
                    {
                        return;
                    }

                    foreach (string file in Directory.GetFiles(Path, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }

                    Directory.Delete(Path, recursive: true);
                }
                catch (IOException)
                {
                    // 临时目录在系统 temp 下，删不掉不影响测试结论。
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
