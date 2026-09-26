using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ArchiveFixer.Helpers;
using ArchiveFixer.Password;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 密码本解析（列表式 + 映射式）与旁路说明文件抽取的测试。
    ///
    /// 隐私约定（AGENTS.md §8）：这里出现的所有密码都是合成密码，不含任何真实密码或真实站点名。
    /// </summary>
    public sealed class PasswordBookTests
    {
        /// <summary>
        /// 代码页编码的注册必须是**幂等的一次性初始化**，而且解析器自己会确保它。
        ///
        /// <para>钉住这件事的原因是一次真的偶发失败：注册原来散在打包的进程读取器里，
        /// 于是"哪个测试先跑"决定了 GB18030 能不能用，密码本那条回退路径时好时坏。
        /// 现在注册收敛到 <see cref="CodePageEncodingBootstrap"/>，解析前自己确保一次 ——
        /// 不依赖"启动路径恰好注册过"，单测与未来的 CLI 直接调也照样对。</para>
        /// </summary>
        [Fact]
        public void 代码页编码注册是幂等的_而且解析器自己会确保它()
        {
            // 重复调用不许抛、状态不许来回翻。
            CodePageEncodingBootstrap.EnsureRegistered();
            bool first = CodePageEncodingBootstrap.IsRegistered;

            CodePageEncodingBootstrap.EnsureRegistered();

            Assert.Equal(first, CodePageEncodingBootstrap.IsRegistered);

            if (!CodePageEncodingBootstrap.IsRegistered)
            {
                // 这台机器缺代码页支持：如实跳过（解析器那边有回退 + 警告，另有测试覆盖）。
                return;
            }

            // 注册好之后，GBK 存的密码本必须**真的读得对**（不是靠警告糊过去）。
            using var dir = new TempDir();
            string path = Path.Combine(dir.Path, "book.txt");

            File.WriteAllText(path, "包A：TestPass123!\n", Encoding.GetEncoding("GB18030"));

            var result = PasswordBookParser.ParseFile(path);

            var entry = Assert.Single(result.Entries);
            Assert.Equal("包A", entry.Name);
            Assert.Equal("TestPass123!", entry.Password);
            Assert.Contains(result.Warnings, w => w.Contains("GB18030", StringComparison.Ordinal));
        }

        // ---------------- 列表式 ----------------

        [Fact]
        public void Parse_列表式_跳过注释与空行并按出现顺序保留()
        {
            var result = PasswordBookParser.Parse("# 注释\n\n  \nTestPass123!\n\t\n second");

            Assert.Equal(2, result.Entries.Count);
            Assert.Equal("TestPass123!", result.Entries[0].Password);
            Assert.Equal(PasswordEntryKind.List, result.Entries[0].Kind);
            Assert.Null(result.Entries[0].Name);
            Assert.Equal(4, result.Entries[0].LineNumber);

            // 列表式整行原样就是密码：前导空格必须保留。
            Assert.Equal(" second", result.Entries[1].Password);
            Assert.Equal(6, result.Entries[1].LineNumber);
            Assert.Equal(0, result.SkippedLineCount);
            Assert.Empty(result.Warnings);
        }

        [Fact]
        public void Parse_密码首尾空格绝对不被Trim()
        {
            var result = PasswordBookParser.Parse("  abc  \n");

            var entry = Assert.Single(result.Entries);
            Assert.Equal("  abc  ", entry.Password);
            Assert.Equal("来自第 1 行", entry.Remark);
        }

        [Fact]
        public void Parse_纯空格行当作空行跳过而不是空格密码()
        {
            // 有意的取舍：密码绝不 Trim，代价是纯空格行无法与排版空行区分，
            // 因此一律按空行跳过；要表达空密码必须用"空"/"空密码"这类显式标记。
            var result = PasswordBookParser.Parse("   \nTestPass123!\n");

            var entry = Assert.Single(result.Entries);
            Assert.Equal("TestPass123!", entry.Password);
        }

        [Fact]
        public void Parse_注释行前允许空白()
        {
            var result = PasswordBookParser.Parse("   # 缩进注释\nTestPass123!\n");

            var entry = Assert.Single(result.Entries);
            Assert.Equal("TestPass123!", entry.Password);
            Assert.Equal(2, entry.LineNumber);
        }

        [Fact]
        public void Parse_三种换行都能识别且行号连续()
        {
            var result = PasswordBookParser.Parse("aaa\r\nbbb\nccc\rddd\n");

            Assert.Equal(new[] { "aaa", "bbb", "ccc", "ddd" }, result.Entries.Select(e => e.Password));
            Assert.Equal(new[] { 1, 2, 3, 4 }, result.Entries.Select(e => e.LineNumber));
        }

        [Fact]
        public void Parse_去重按原文精确匹配并保留第一次出现且行号保留()
        {
            var result = PasswordBookParser.Parse("dup\ndup\nother\n dup\n");

            Assert.Equal(new[] { "dup", "other", " dup" }, result.Entries.Select(e => e.Password));
            Assert.Equal(new[] { 1, 3, 4 }, result.Entries.Select(e => e.LineNumber));

            Assert.Equal(1, result.SkippedLineCount);
            Assert.Contains("第 2 行", Assert.Single(result.Warnings));
        }

        [Fact]
        public void Parse_名称不同但密码相同仍算重复()
        {
            // 不同 Name 指向同一个密码时，候选只需要一份——重复尝试是浪费尝试次数。
            var result = PasswordBookParser.Parse("包A:dup\n包B:dup\n");

            var entry = Assert.Single(result.Entries);
            Assert.Equal("包A", entry.Name);
            Assert.Equal(1, result.SkippedLineCount);
        }

        [Fact]
        public void Parse_null或空内容返回空结果()
        {
            Assert.Empty(PasswordBookParser.Parse(null).Entries);
            Assert.Empty(PasswordBookParser.Parse(string.Empty).Entries);
            Assert.Empty(PasswordBookParser.Parse("# 只有注释").Entries);
        }

        // ---------------- 映射式 ----------------

        [Fact]
        public void Parse_映射式_中文冒号()
        {
            var result = PasswordBookParser.Parse("包A：TestPass123!\n");

            var entry = Assert.Single(result.Entries);
            Assert.Equal(PasswordEntryKind.Mapped, entry.Kind);
            Assert.Equal("包A", entry.Name);
            Assert.Equal("TestPass123!", entry.Password);
        }

        [Fact]
        public void Parse_映射式_名称Trim而密码不Trim()
        {
            var result = PasswordBookParser.Parse("  包A  :  TestPass123!  \n");

            var entry = Assert.Single(result.Entries);
            Assert.Equal("包A", entry.Name);
            Assert.Equal("  TestPass123!  ", entry.Password);
        }

        [Fact]
        public void Parse_映射式_按第一个冒号切分以保住密码里的冒号()
        {
            // 密码本身可能带冒号，按第一个冒号切分才拿得到完整密码。
            var result = PasswordBookParser.Parse("账号:口令:123\n");

            var entry = Assert.Single(result.Entries);
            Assert.Equal(PasswordEntryKind.Mapped, entry.Kind);
            Assert.Equal("账号", entry.Name);
            Assert.Equal("口令:123", entry.Password);

            /*
             * 整行原文也要留着（2026-09-25 第 30 条）：
             * 如果这本书其实是**列表式**（密码就是 `账号:口令:123` 整行），切出来的右侧根本不对，
             * 候选链要靠 RawLine 把整行也试一遍。
             */
            Assert.Equal("账号:口令:123", entry.RawLine);
        }

        [Fact]
        public void Parse_列表式的整行_不再留一份原文()
        {
            var result = PasswordBookParser.Parse("plain-pass\n");

            var entry = Assert.Single(result.Entries);
            Assert.Equal(PasswordEntryKind.List, entry.Kind);
            Assert.Null(entry.RawLine);
        }

        [Fact]
        public void Parse_映射式_名称含中文冒号也按第一个冒号切分()
        {
            var result = PasswordBookParser.Parse("包:子目录:TestPass123!\n");

            var entry = Assert.Single(result.Entries);
            Assert.Equal("包", entry.Name);
            Assert.Equal("子目录:TestPass123!", entry.Password);
        }

        [Fact]
        public void Parse_冒号在第1个字符时不算映射式()
        {
            // ":abc" 的冒号左侧没有内容，只能按列表式理解——整行原样当密码。
            var result = PasswordBookParser.Parse(":abc\n");

            var entry = Assert.Single(result.Entries);
            Assert.Equal(PasswordEntryKind.List, entry.Kind);
            Assert.Null(entry.Name);
            Assert.Equal(":abc", entry.Password);
        }

        [Fact]
        public void Parse_冒号左侧只有空白时不算映射式()
        {
            var result = PasswordBookParser.Parse("   :abc\n");

            var entry = Assert.Single(result.Entries);
            Assert.Equal(PasswordEntryKind.List, entry.Kind);
            Assert.Equal("   :abc", entry.Password);
        }

        [Fact]
        public void Parse_冒号右侧为空时按列表式处理()
        {
            var result = PasswordBookParser.Parse("abc:\n");

            var entry = Assert.Single(result.Entries);
            Assert.Equal(PasswordEntryKind.List, entry.Kind);
            Assert.Equal("abc:", entry.Password);
        }

        [Fact]
        public void Parse_映射式密码为单个空格时保留空格()
        {
            var result = PasswordBookParser.Parse("包A: \n");

            var entry = Assert.Single(result.Entries);
            Assert.Equal(PasswordEntryKind.Mapped, entry.Kind);
            Assert.Equal(" ", entry.Password);
        }

        // ---------------- 空密码 ----------------

        [Theory]
        [InlineData("空")]
        [InlineData("空密码")]
        [InlineData("<空>")]
        [InlineData("<EMPTY>")]
        [InlineData("  空  ")]
        public void Parse_空密码标记产出空字符串密码(string line)
        {
            var result = PasswordBookParser.Parse(line + "\n");

            var entry = Assert.Single(result.Entries);
            Assert.Equal(PasswordEntryKind.Empty, entry.Kind);
            Assert.Equal(string.Empty, entry.Password);
            Assert.Equal("空密码（按文件内容）", entry.Remark);
        }

        [Fact]
        public void Parse_空密码标记与同名映射式不同名时都保留()
        {
            var result = PasswordBookParser.Parse("空\n包A：TestPass123!\n");

            Assert.Equal(2, result.Entries.Count);
            Assert.Equal(string.Empty, result.Entries[0].Password);
            Assert.Equal(PasswordEntryKind.Empty, result.Entries[0].Kind);
            Assert.Equal("TestPass123!", result.Entries[1].Password);
        }

        [Fact]
        public void Parse_行尾带空字不算空密码标记()
        {
            // 只认整行标记，避免把"abc空"这类真实密码误判成空密码。
            var result = PasswordBookParser.Parse("abc空\n");

            var entry = Assert.Single(result.Entries);
            Assert.Equal(PasswordEntryKind.List, entry.Kind);
            Assert.Equal("abc空", entry.Password);
        }

        // ---------------- MatchByName ----------------

        [Fact]
        public void MatchByName_完全相等优先于包含()
        {
            var entries = new List<PasswordEntry>
            {
                new PasswordEntry { Password = "p1", Name = "包A-完整版", Kind = PasswordEntryKind.Mapped },
                new PasswordEntry { Password = "p2", Name = "包A", Kind = PasswordEntryKind.Mapped },
            };

            var matched = PasswordBookParser.MatchByName(entries, "包A.7z");

            var entry = Assert.Single(matched);
            Assert.Equal("p2", entry.Password);
        }

        [Fact]
        public void MatchByName_tar_gz归档基名能匹配名称()
        {
            var entries = new List<PasswordEntry>
            {
                new PasswordEntry { Password = "p1", Name = "x", Kind = PasswordEntryKind.Mapped },
            };

            var matched = PasswordBookParser.MatchByName(entries, "x.tar.gz");

            var entry = Assert.Single(matched);
            Assert.Equal("p1", entry.Password);
        }

        [Fact]
        public void MatchByName_包含命中按输入顺序返回()
        {
            // 两个名称都 ≥2 个字，都会参与包含匹配；顺序必须按密码本里的出现顺序，不能被打乱。
            // （单字名称不参与包含匹配，见下一条测试。）
            var entries = new List<PasswordEntry>
            {
                new PasswordEntry { Password = "p1", Name = "包A", Kind = PasswordEntryKind.Mapped },
                new PasswordEntry { Password = "p2", Name = "资源", Kind = PasswordEntryKind.Mapped },
            };

            var matched = PasswordBookParser.MatchByName(entries, "包A资源.7z");

            Assert.Equal(new[] { "p1", "p2" }, matched.Select(e => e.Password));
        }

        [Fact]
        public void MatchByName_单字名称不参与包含匹配()
        {
            // "包"只有一个字，参与包含匹配会命中一大片无关归档，因此直接不参与。
            var entries = new List<PasswordEntry>
            {
                new PasswordEntry { Password = "p1", Name = "包", Kind = PasswordEntryKind.Mapped },
            };

            Assert.Empty(PasswordBookParser.MatchByName(entries, "包A.7z"));
        }

        [Fact]
        public void MatchByName_无命中返回空列表()
        {
            var entries = new List<PasswordEntry>
            {
                new PasswordEntry { Password = "p1", Name = "包A", Kind = PasswordEntryKind.Mapped },
            };

            Assert.Empty(PasswordBookParser.MatchByName(entries, "别的包.7z"));
        }

        [Fact]
        public void MatchByName_忽略列表式与空密码条目()
        {
            var entries = new List<PasswordEntry>
            {
                new PasswordEntry { Password = "包A", Name = null, Kind = PasswordEntryKind.List },
                new PasswordEntry { Password = "p1", Name = "包A", Kind = PasswordEntryKind.Empty },
            };

            Assert.Empty(PasswordBookParser.MatchByName(entries, "包A.7z"));
        }

        [Fact]
        public void MatchByName_名称大小写与扩展名差异都能命中()
        {
            var entries = new List<PasswordEntry>
            {
                new PasswordEntry { Password = "p1", Name = "PackA", Kind = PasswordEntryKind.Mapped },
                new PasswordEntry { Password = "p2", Name = "PackB.zip", Kind = PasswordEntryKind.Mapped },
            };

            Assert.Equal("p1", Assert.Single(PasswordBookParser.MatchByName(entries, "packa.7z")).Password);
            Assert.Equal("p2", Assert.Single(PasswordBookParser.MatchByName(entries, "PACKB.tar.gz")).Password);
        }

        [Fact]
        public void MatchByName_空归档名返回空列表()
        {
            var entries = new List<PasswordEntry>
            {
                new PasswordEntry { Password = "p1", Name = "包A", Kind = PasswordEntryKind.Mapped },
            };

            Assert.Empty(PasswordBookParser.MatchByName(entries, string.Empty));
            Assert.Empty(PasswordBookParser.MatchByName(entries, "   "));
        }

        [Fact]
        public void NormalizeName_去扩展名去空白统一小写()
        {
            Assert.Equal("pack", PasswordBookParser.NormalizeName("  Pack.ZIP  "));
            Assert.Equal("x", PasswordBookParser.NormalizeName("X.tar.gz"));
            Assert.Equal(string.Empty, PasswordBookParser.NormalizeName(null));
            Assert.Equal(string.Empty, PasswordBookParser.NormalizeName("   "));
        }

        [Fact]
        public void PasswordEntry_ToString不泄露明文()
        {
            // 防止本对象被手滑丢进日志或异常信息时泄露密码。
            var entry = new PasswordEntry
            {
                Password = "TestPass123!",
                Name = "包A",
                Kind = PasswordEntryKind.Mapped,
                LineNumber = 2,
            };

            Assert.DoesNotContain("TestPass123!", entry.ToString(), StringComparison.Ordinal);
        }

        // ---------------- ParseFile ----------------

        [Fact]
        public void ParseFile_不存在的文件返回空结果加警告且不抛异常()
        {
            string missing = Path.Combine(Path.GetTempPath(), "af_pw_missing_" + Guid.NewGuid().ToString("N") + ".txt");

            var result = PasswordBookParser.ParseFile(missing);

            Assert.Empty(result.Entries);
            Assert.Contains("不存在", Assert.Single(result.Warnings));
            Assert.Equal(0, result.SkippedLineCount);

            var empty = PasswordBookParser.ParseFile(string.Empty);
            Assert.Empty(empty.Entries);
            Assert.NotEmpty(empty.Warnings);
        }

        [Fact]
        public void ParseFile_读取UTF8带BOM文件()
        {
            using var dir = new TempDir();

            // 密码本里有中文名称，因此这里特意写成"UTF-8 带 BOM"这一中文 Windows 上最常见的形态。
            string path = dir.WriteText("book.txt", "# 密码本\n包A：TestPass123!\n", new UTF8Encoding(true));

            var result = PasswordBookParser.ParseFile(path);

            var entry = Assert.Single(result.Entries);
            Assert.Equal("包A", entry.Name);
            Assert.Equal("TestPass123!", entry.Password);
            Assert.Empty(result.Warnings);
        }

        [Fact]
        public void ParseFile_读取UTF8无BOM文件()
        {
            using var dir = new TempDir();
            string path = dir.WriteText("book.txt", "TestPass123!\n", new UTF8Encoding(false));

            var result = PasswordBookParser.ParseFile(path);

            Assert.Equal("TestPass123!", Assert.Single(result.Entries).Password);
            Assert.Empty(result.Warnings);
        }

        [Fact]
        public void ParseFile_非UTF8内容触发编码回退且不静默丢行()
        {
            using var dir = new TempDir();
            string path = Path.Combine(dir.Path, "book.txt");

            /*
             * ⚠ 这一条曾经是**顺序相关**的（会偶发红），根因有两处，现在都钉住了：
             *
             * ① GB18030 需要 CodePagesEncodingProvider，而它是**进程级、一次性**注册的：
             *    生产代码在打包那条路径上（PackProcessRunner 读进程输出编码）会注册它。
             *    于是"这个测试跑在打包测试之前还是之后"决定了下面这个 GetEncoding 成功还是抛异常 ——
             *    成功时走进真正的断言，失败时整条直接 return（等于没测）。
             *    这里显式注册一次（拿不到就跳过），让**分支固定**：要么每次都真测，要么每次都明说跳过。
             * ② 回退警告的原文是"密码本文件不是有效的 UTF-8，已按 GB18030（GBK 超集）重新读取。"
             *    —— 里面**没有"编码"两个字**。原来断言 `w.Contains("编码")` 就是把"我看错了文案"
             *    写进了测试：真走到这条分支时它必然红。现在按**这条警告要表达的事实**断言
             *    （说了"不是 UTF-8"或说了回退到什么编码），措辞改了也不会假红。
             */
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            }
            catch
            {
                // 拿不到就跳过（与下面 GetEncoding 失败同一处理）。
            }

            Encoding encoding;

            try
            {
                encoding = Encoding.GetEncoding("GB18030");
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return;
            }

            File.WriteAllText(path, "包A：TestPass123!\n", encoding);

            var result = PasswordBookParser.ParseFile(path);

            // 关键：要么成功按 GB18030 读出来（带一条"已回退"的警告），要么至少不静默丢行——
            // 绝不允许"解析出 0 条、也没有任何警告"。
            if (result.Entries.Count == 1)
            {
                Assert.Equal("TestPass123!", result.Entries[0].Password);
                Assert.Equal("包A", result.Entries[0].Name);

                Assert.Contains(
                    result.Warnings,
                    w => w.Contains("UTF-8", StringComparison.Ordinal) ||
                         w.Contains("GB18030", StringComparison.Ordinal) ||
                         w.Contains("编码", StringComparison.Ordinal));
            }
            else
            {
                Assert.NotEmpty(result.Warnings);
            }
        }

        // ---------------- SidecarPasswordReader ----------------

        [Fact]
        public void Sidecar_抽取set式与中文标签式并标明来源()
        {
            using var dir = new TempDir();

            string text = string.Join(
                "\n",
                "解压说明",
                "注意：仅供测试使用",
                "set \"password=TestPass123!\"",
                "解压密码：中文口令A1",
                "http://example.invalid/very/long/description/page.html");

            string sidecar = dir.WriteText("说明.txt", text, new UTF8Encoding(false));
            string archive = dir.WriteText("archive.7z", "占位内容", new UTF8Encoding(false));

            var candidates = SidecarPasswordReader.ReadCandidates(archive);

            Assert.Equal(new[] { "TestPass123!", "中文口令A1" }, candidates.Select(c => c.Password));
            Assert.All(candidates, c => Assert.Equal(sidecar, c.SourceFile));
            Assert.All(candidates, c => Assert.Contains("说明.txt", c.Remark ?? string.Empty));
            Assert.Equal(3, candidates[0].LineNumber);
            Assert.Equal(4, candidates[1].LineNumber);
        }

        [Fact]
        public void Sidecar_抽取7z参数式()
        {
            using var dir = new TempDir();

            dir.WriteText("aa.bat", "7z x \"archive.7z\" -pTestPass123! -y\n", new UTF8Encoding(false));
            string archive = dir.WriteText("archive.7z", "占位内容", new UTF8Encoding(false));

            var candidates = SidecarPasswordReader.ReadCandidates(archive);

            var candidate = Assert.Single(candidates);
            Assert.Equal("TestPass123!", candidate.Password);
            Assert.Equal(1, candidate.LineNumber);
            Assert.EndsWith("aa.bat", candidate.SourceFile, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Sidecar_抽取set无引号式与keyvalue式()
        {
            using var dir = new TempDir();

            dir.WriteText("aa.txt", "set password=TestPass123!\npassword:abc 123\n", new UTF8Encoding(false));
            string archive = dir.WriteText("archive.7z", "占位内容", new UTF8Encoding(false));

            var candidates = SidecarPasswordReader.ReadCandidates(archive);

            Assert.Equal(new[] { "TestPass123!", "abc 123" }, candidates.Select(c => c.Password));
        }

        [Fact]
        public void Sidecar_抽取解压密码是式()
        {
            using var dir = new TempDir();

            dir.WriteText("aa.txt", "解压密码是 TestPass123!\n", new UTF8Encoding(false));
            string archive = dir.WriteText("archive.7z", "占位内容", new UTF8Encoding(false));

            var candidates = SidecarPasswordReader.ReadCandidates(archive);

            Assert.Equal("TestPass123!", Assert.Single(candidates).Password);
        }

        [Fact]
        public void Sidecar_整行就是密码时才收()
        {
            using var dir = new TempDir();

            dir.WriteText("aa.txt", "TestPass123!\n\"quotedPass9\"\n", new UTF8Encoding(false));
            string archive = dir.WriteText("archive.7z", "占位内容", new UTF8Encoding(false));

            var candidates = SidecarPasswordReader.ReadCandidates(archive);

            Assert.Equal(new[] { "TestPass123!", "quotedPass9" }, candidates.Select(c => c.Password));
        }

        [Fact]
        public void Sidecar_说明文字与超长行不会被当成密码()
        {
            using var dir = new TempDir();
            string longLine = new string('x', 200);

            string text = string.Join(
                "\n",
                "这是一份很长的说明文字，介绍资源内容与使用方法",
                "http://example.invalid/path/to/readme",
                "C:\\Users\\Public\\Downloads\\readme.txt",
                longLine);

            dir.WriteText("aa.txt", text, new UTF8Encoding(false));
            string archive = dir.WriteText("archive.7z", "占位内容", new UTF8Encoding(false));

            Assert.Empty(SidecarPasswordReader.ReadCandidates(archive));
        }

        [Fact]
        public void Sidecar_按密码原文或Ordinal去重()
        {
            using var dir = new TempDir();

            dir.WriteText("aa.txt", "set \"password=TestPass123!\"\n密码：TestPass123!\n", new UTF8Encoding(false));
            string archive = dir.WriteText("archive.7z", "占位内容", new UTF8Encoding(false));

            var candidate = Assert.Single(SidecarPasswordReader.ReadCandidates(archive));
            Assert.Equal("TestPass123!", candidate.Password);
            Assert.Equal(1, candidate.LineNumber);
        }

        [Fact]
        public void Sidecar_maxCandidates生效()
        {
            using var dir = new TempDir();

            var lines = new List<string>();

            for (int i = 1; i <= 5; i++)
            {
                lines.Add("pass" + i);
            }

            dir.WriteText("aa.txt", string.Join("\n", lines) + "\n", new UTF8Encoding(false));
            string archive = dir.WriteText("archive.7z", "占位内容", new UTF8Encoding(false));

            var candidates = SidecarPasswordReader.ReadCandidates(archive, maxCandidates: 2);

            Assert.Equal(new[] { "pass1", "pass2" }, candidates.Select(c => c.Password));
        }

        [Fact]
        public void Sidecar_超过大小上限的文件被跳过()
        {
            using var dir = new TempDir();

            dir.WriteText("aa.txt", "TestPass123!\n", new UTF8Encoding(false));
            string archive = dir.WriteText("archive.7z", "占位内容", new UTF8Encoding(false));

            Assert.Empty(SidecarPasswordReader.ReadCandidates(archive, maxFileBytes: 4));
            Assert.NotEmpty(SidecarPasswordReader.ReadCandidates(archive, maxFileBytes: 4096));
        }

        [Fact]
        public void Sidecar_只读同目录不递归子目录()
        {
            using var dir = new TempDir();

            Directory.CreateDirectory(Path.Combine(dir.Path, "sub"));
            dir.WriteText(Path.Combine("sub", "aa.txt"), "TestPass123!\n", new UTF8Encoding(false));
            string archive = dir.WriteText("archive.7z", "占位内容", new UTF8Encoding(false));

            Assert.Empty(SidecarPasswordReader.ReadCandidates(archive));
        }

        [Fact]
        public void Sidecar_只认指定后缀的说明文件()
        {
            using var dir = new TempDir();

            dir.WriteText("aa.log", "TestPass123!\n", new UTF8Encoding(false));
            dir.WriteText("bb.md", "TestPass123!\n", new UTF8Encoding(false));
            string archive = dir.WriteText("archive.7z", "占位内容", new UTF8Encoding(false));

            var candidate = Assert.Single(SidecarPasswordReader.ReadCandidates(archive));
            Assert.EndsWith("bb.md", candidate.SourceFile, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Sidecar_路径为空或目录不存在时返回空列表()
        {
            Assert.Empty(SidecarPasswordReader.ReadCandidates(string.Empty));
            Assert.Empty(SidecarPasswordReader.ReadCandidates("   "));
            Assert.Empty(SidecarPasswordReader.ReadCandidates(
                Path.Combine(Path.GetTempPath(), "af_sidecar_missing_" + Guid.NewGuid().ToString("N"), "archive.7z")));
        }

        [Fact]
        public void SidecarCandidate_ToString不泄露明文()
        {
            var candidate = new SidecarCandidate
            {
                Password = "TestPass123!",
                SourceFile = @"C:\temp\说明.txt",
                LineNumber = 3,
            };

            Assert.DoesNotContain("TestPass123!", candidate.ToString(), StringComparison.Ordinal);
        }

        /// <summary>
        /// 每个测试自建临时目录，Dispose 时整目录删掉，保证不在仓库（或系统里）留下残留文件。
        /// </summary>
        private sealed class TempDir : IDisposable
        {
            public TempDir()
            {
                Path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "ArchiveFixerPwBook",
                    Guid.NewGuid().ToString("N"));

                Directory.CreateDirectory(Path);
            }

            public string Path { get; }

            /// <summary>写一个文本文件（自动建子目录），返回完整路径。</summary>
            public string WriteText(string relativePath, string content, Encoding encoding)
            {
                string full = System.IO.Path.Combine(Path, relativePath);
                string? directory = System.IO.Path.GetDirectoryName(full);

                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(full, content, encoding);

                return full;
            }

            public void Dispose()
            {
                try
                {
                    if (Directory.Exists(Path))
                    {
                        Directory.Delete(Path, recursive: true);
                    }
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
