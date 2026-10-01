using System.IO;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;

namespace ArchiveFixer.Tests;

public class RenameServiceTests
{
    private static string CreateTempDir()
    {
        return Path.Combine(Path.GetTempPath(), "af_rename_" + Guid.NewGuid().ToString("N"));
    }

    private static ArchiveTask CreateTask(string filePath, string format, string suggestedExtension)
    {
        return new ArchiveTask(filePath)
        {
            DetectedFormat = format,
            SuggestedExtension = suggestedExtension,
            IsArchive = true,
            ExtensionStatus = "后缀不匹配"
        };
    }

    private static RenameOptions CreateFixOptions()
    {
        var options = new RenameOptions
        {
            OperationType = "FixByDetectedFormat",
            TargetExtension = ".zip",
            ConflictAction = "AutoRename",
            UnknownFormatAction = "MarkUnknown"
        };
        options.Normalize();
        return options;
    }

    [Fact]
    public void BuildPreview_FixByDetectedFormat_ReplacesWrongExtension()
    {
        string dir = CreateTempDir();
        try
        {
            string file = Path.Combine(dir, "a.jpg");
            Directory.CreateDirectory(dir);
            File.WriteAllText(file, "x");

            var service = new RenameService();
            var preview = service.BuildPreview(
                new[] { CreateTask(file, "ZIP", ".zip") },
                CreateFixOptions());

            Assert.Single(preview);
            Assert.Equal(Path.Combine(dir, "a.zip"), preview[0].NewPath);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void BuildPreview_ExtensionAlreadyCorrect_MarksSkip()
    {
        string dir = CreateTempDir();
        try
        {
            string file = Path.Combine(dir, "a.zip");
            Directory.CreateDirectory(dir);
            File.WriteAllText(file, "x");

            var task = CreateTask(file, "ZIP", ".zip");
            task.ExtensionStatus = "后缀正常";

            var service = new RenameService();
            var preview = service.BuildPreview(new[] { task }, CreateFixOptions());

            Assert.Single(preview);
            Assert.Equal("将跳过", preview[0].Status);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void BuildPreview_MultiExtension_RemovesTrailingFakeExtension()
    {
        string dir = CreateTempDir();
        try
        {
            string file = Path.Combine(dir, "a.7z.jpg");
            Directory.CreateDirectory(dir);
            File.WriteAllText(file, "x");

            var service = new RenameService();
            var preview = service.BuildPreview(
                new[] { CreateTask(file, "7Z", ".7z") },
                new RenameOptions
                {
                    OperationType = "FixByDetectedFormat",
                    TargetExtension = ".7z",
                    ConflictAction = "AutoRename",
                    UnknownFormatAction = "MarkUnknown"
                });

            Assert.Single(preview);
            Assert.Equal(Path.Combine(dir, "a.7z"), preview[0].NewPath);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // 「删除多个后缀」整块退役（2026-09-26 审计：界面上的那颗按钮 + 命令 + 服务分支一起删）——
    // 用例跟着删掉，⛔ 不留"测试还在钉一个已经不存在的操作"。

    [Fact]
    public async Task ExecuteRenameAsync_RenamesFilesOnDiskAndUpdatesTask()
    {
        string dir = CreateTempDir();
        try
        {
            string file = Path.Combine(dir, "a.jpg");
            Directory.CreateDirectory(dir);
            File.WriteAllText(file, "x");

            var task = CreateTask(file, "ZIP", ".zip");
            var service = new RenameService();
            var preview = service.BuildPreview(new[] { task }, CreateFixOptions());

            await service.ExecuteRenameAsync(preview, new[] { task });

            Assert.False(File.Exists(file));
            Assert.True(File.Exists(Path.Combine(dir, "a.zip")));
            Assert.Equal("改名成功", preview[0].Status);
            Assert.Equal(Path.Combine(dir, "a.zip"), task.CurrentPath);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ExecuteRenameAsync_ConflictAutoRename_UsesNumberedName()
    {
        string dir = CreateTempDir();
        try
        {
            string file = Path.Combine(dir, "a.jpg");
            Directory.CreateDirectory(dir);
            File.WriteAllText(file, "x");
            File.WriteAllText(Path.Combine(dir, "a.zip"), "existing");

            var task = CreateTask(file, "ZIP", ".zip");
            var service = new RenameService();
            var preview = service.BuildPreview(new[] { task }, CreateFixOptions());

            await service.ExecuteRenameAsync(preview, new[] { task });

            Assert.True(File.Exists(Path.Combine(dir, "a(1).zip")));
            Assert.Equal(Path.Combine(dir, "a(1).zip"), task.CurrentPath);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ─────────────── 覆盖不丢文件 / 名称交换两阶段（不变量 3） ───────────────

    [Fact]
    public async Task ExecuteRenameAsync_覆盖模式_目标文件被新内容取代且源文件不再存在()
    {
        string dir = CreateTempDir();
        try
        {
            Directory.CreateDirectory(dir);

            string source = Path.Combine(dir, "a.jpg");
            string target = Path.Combine(dir, "a.zip");
            File.WriteAllText(source, "新内容");
            File.WriteAllText(target, "旧内容");

            var task = CreateTask(source, "ZIP", ".zip");
            var service = new RenameService();
            var preview = service.BuildPreview(new[] { task }, CreateOverwriteOptions());

            Assert.Equal("目标已存在", preview[0].Status);

            await service.ExecuteRenameAsync(preview, new[] { task });

            Assert.Equal("改名成功", preview[0].Status);
            Assert.False(File.Exists(source), "源文件应当已经改名，不再存在于旧路径");
            Assert.Equal("新内容", File.ReadAllText(target));
            Assert.Empty(Directory.GetFiles(dir, "*.af-vacating*"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ExecuteRenameAsync_名称交换_两个文件互换且内容都不丢()
    {
        string dir = CreateTempDir();
        try
        {
            Directory.CreateDirectory(dir);

            string pathA = Path.Combine(dir, "A.zip");
            string pathB = Path.Combine(dir, "B.zip");
            File.WriteAllText(pathA, "内容A");
            File.WriteAllText(pathB, "内容B");

            // 直接构造"改名后互为对方名字"的预览项（BuildPreview 不会生成这种落点）。
            var itemA = new RenamePreviewItem(pathA, "ZIP", "按真实格式修正", pathB, "Overwrite")
            {
                Status = "目标已存在"
            };
            var itemB = new RenamePreviewItem(pathB, "ZIP", "按真实格式修正", pathA, "Overwrite")
            {
                Status = "目标已存在"
            };

            var taskA = CreateTask(pathA, "ZIP", ".zip");
            var taskB = CreateTask(pathB, "ZIP", ".zip");

            var service = new RenameService();

            await service.ExecuteRenameAsync(new[] { itemA, itemB }, new[] { taskA, taskB });

            Assert.Equal("改名成功", itemA.Status);
            Assert.Equal("改名成功", itemB.Status);

            // 交换成功：两个文件都还在，只是内容换了位置 —— 一个都不能丢。
            Assert.True(File.Exists(pathA), "交换后 A.zip 必须存在");
            Assert.True(File.Exists(pathB), "交换后 B.zip 必须存在");
            Assert.Equal("内容B", File.ReadAllText(pathA));
            Assert.Equal("内容A", File.ReadAllText(pathB));
            Assert.Empty(Directory.GetFiles(dir, "*.af-vacating*"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ExecuteRenameAsync_覆盖模式_落位失败时源文件与目标文件都必须还在()
    {
        string dir = CreateTempDir();
        try
        {
            Directory.CreateDirectory(dir);

            string source = Path.Combine(dir, "a.jpg");
            string target = Path.Combine(dir, "a.zip");
            File.WriteAllText(source, "新内容");
            File.WriteAllText(target, "旧内容");

            var task = CreateTask(source, "ZIP", ".zip");
            var service = new RenameService();
            var preview = service.BuildPreview(new[] { task }, CreateOverwriteOptions());

            // 让本次改名必然失败：目标目录在执行前被删掉。
            // 旧写法此时已经 File.Delete(a.zip)，两个文件都会消失。
            Directory.Delete(dir, recursive: true);

            await service.ExecuteRenameAsync(preview, new[] { task });

            Assert.Equal("无法改名", preview[0].Status);
            Assert.False(string.IsNullOrWhiteSpace(preview[0].ErrorMessage), "失败必须给出明确原因");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    /// <summary>
    /// **用户 2026-10-01 真机第五轮留下的那 400 MB**（`AAAA\222\222.z01`）：
    /// 跨盘 zip 的**本体**被「按真实格式修正」改名之后，<see cref="ArchiveTask.VolumePaths"/>
    /// 里那份旧名字必须一起改掉。
    ///
    /// <para><b>现场</b>：`222.zscip`（本体，名字被改坏的那一份）+ `222.z删除01`（续卷）→ 批首改名成
    /// `222.zip` + `222.z01`。可本体那次改名只写了 <c>CurrentPath</c>，清单里还留着 `222.zscip`
    /// ⇒ 账上成了 <c>[222.zscip（盘上已经没有了）, 222.z01]</c>；
    /// `SourcePackageMover.ResolveSourceGroup` 于是判"清单非空、却不含任务自己"⇒ 按**单文件**办
    /// ⇒ 只搬 247 MB 的本体，400 MB 的 `222.z01` 留在源目录（日志里那句"已把 **1** 个源包移入其余物"
    /// 就是它）。而"清单能变多才补"那道闸门按**条数**比（2 条 vs 2 条 = 没变多）也补不上。</para>
    ///
    /// <para><b>这条钉的验收</b>：改名之后，这一组**两卷都要能被搬走** —— 少一卷就是用户要自己动手清。</para>
    /// </summary>
    [Fact]
    public async Task ExecuteRenameAsync_分卷组本体改名_清单里的旧名字也要跟着改()
    {
        string dir = CreateTempDir();
        try
        {
            Directory.CreateDirectory(dir);

            string body = Path.Combine(dir, "222.zscip");
            string volume = Path.Combine(dir, "222.z删除01");
            File.WriteAllText(body, "body");
            File.WriteAllText(volume, "vol");

            var task = CreateTask(body, "ZIP_SPANNED", ".zip");
            task.IsVolumeGroup = true;
            task.VolumePaths.Add(body);
            task.VolumePaths.Add(volume);

            var service = new RenameService();
            var preview = service.BuildPreview(new[] { task }, CreateFixOptions());

            await service.ExecuteRenameAsync(preview, new[] { task });

            string renamed = Path.Combine(dir, "222.zip");

            Assert.Equal(renamed, task.CurrentPath);

            // 清单里那份旧名字必须换成新名字（否则搬运退化成"只搬本体"）。
            Assert.Contains(renamed, task.VolumePaths);
            Assert.DoesNotContain(body, task.VolumePaths);
            Assert.Contains(volume, task.VolumePaths);

            // 用户真正在意的验收：整组两卷都得在搬运范围内。
            IReadOnlyList<string> group = SourcePackageMover.ResolveSourceGroup(task);

            Assert.Equal(2, group.Count);
            Assert.Contains(renamed, group);
            Assert.Contains(volume, group);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static RenameOptions CreateOverwriteOptions()
    {
        return new RenameOptions
        {
            OperationType = "FixByDetectedFormat",
            TargetExtension = ".zip",
            ConflictAction = "Overwrite",
            UnknownFormatAction = "MarkUnknown"
        };
    }
}
