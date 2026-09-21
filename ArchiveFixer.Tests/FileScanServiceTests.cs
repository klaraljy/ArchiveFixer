using System.IO;
using ArchiveFixer.Models;
using ArchiveFixer.Services;

namespace ArchiveFixer.Tests;

public class FileScanServiceTests
{
    private static string CreateTempDir()
    {
        return Path.Combine(Path.GetTempPath(), "af_scan_" + Guid.NewGuid().ToString("N"));
    }

    private static ScanOptions CreateOptions(string scanMode = "ScanAllFiles")
    {
        return new ScanOptions
        {
            RecursiveScan = true,
            ScanMode = scanMode,
            IncludeHiddenFiles = false,
            IncludeSystemFiles = false,
            MaxFileSizeLimit = 0
        };
    }

    [Fact]
    public void ShouldIncludeFile_ScanAllFiles_ReturnsTrue()
    {
        string dir = CreateTempDir();
        try
        {
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "anything.bin");
            File.WriteAllText(file, "x");

            var service = new FileScanService();

            Assert.True(service.ShouldIncludeFile(file, CreateOptions()));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ShouldIncludeFile_ExcludesHiddenFileByDefault()
    {
        string dir = CreateTempDir();
        try
        {
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "hidden.dat");
            File.WriteAllText(file, "x");
            File.SetAttributes(file, FileAttributes.Hidden);

            var service = new FileScanService();

            Assert.False(service.ShouldIncludeFile(file, CreateOptions()));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ShouldIncludeFile_IncludesHiddenWhenEnabled()
    {
        string dir = CreateTempDir();
        try
        {
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "hidden.dat");
            File.WriteAllText(file, "x");
            File.SetAttributes(file, FileAttributes.Hidden);

            var options = CreateOptions();
            options.IncludeHiddenFiles = true;

            var service = new FileScanService();

            Assert.True(service.ShouldIncludeFile(file, options));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ShouldIncludeFile_ExceedsSizeLimit_ReturnsFalse()
    {
        string dir = CreateTempDir();
        try
        {
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "big.bin");
            using (var stream = new FileStream(file, FileMode.Create))
            {
                stream.SetLength(1024L * 1024L + 1);
            }

            var options = CreateOptions();
            options.MaxFileSizeLimit = 1;

            var service = new FileScanService();

            Assert.False(service.ShouldIncludeFile(file, options));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ScanFolder_Recursive_IncludesSubfolderFiles()
    {
        string dir = CreateTempDir();
        try
        {
            string sub = Path.Combine(dir, "sub");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(dir, "root.txt"), "x");
            File.WriteAllText(Path.Combine(sub, "nested.txt"), "x");

            var service = new FileScanService();
            var tasks = service.ScanFolder(dir, CreateOptions());

            Assert.Equal(2, tasks.Count);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ScanFolder_TopDirectoryOnly_ExcludesSubfolderFiles()
    {
        string dir = CreateTempDir();
        try
        {
            string sub = Path.Combine(dir, "sub");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(dir, "root.txt"), "x");
            File.WriteAllText(Path.Combine(sub, "nested.txt"), "x");

            var options = CreateOptions();
            options.RecursiveScan = false;

            var service = new FileScanService();
            var tasks = service.ScanFolder(dir, options);

            Assert.Single(tasks);
            Assert.Equal("root.txt", tasks[0].FileName);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ScanFile_CreatesTaskWithExpectedFields()
    {
        string dir = CreateTempDir();
        try
        {
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "a.zip");
            File.WriteAllText(file, "x");

            var service = new FileScanService();
            var task = service.ScanFile(file, CreateOptions());

            Assert.NotNull(task);
            Assert.True(task!.IsSelected);
            Assert.Equal(Path.GetFullPath(file), task.CurrentPath);
            Assert.Equal("a.zip", task.FileName);
            Assert.Equal(".zip", task.CurrentExtension);
            Assert.Equal("等待扫描", task.Status);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
