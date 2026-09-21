using System.IO;
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

    [Fact]
    public void BuildNewPath_DeleteMultipleExtensions_RemovesGivenCount()
    {
        string dir = CreateTempDir();
        try
        {
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "a.rar.pdf.jpg");
            var task = CreateTask(file, "RAR", ".rar");

            var options = new RenameOptions
            {
                OperationType = "DeleteMultipleExtensions",
                DeleteExtensionCount = 2,
                ConflictAction = "AutoRename"
            };
            options.Normalize();

            var service = new RenameService();
            string newPath = service.BuildNewPath(task, options);

            Assert.Equal(Path.Combine(dir, "a.rar"), newPath);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

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
}
