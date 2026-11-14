using ArchiveFixer.Storage;
using System.Collections.Generic;

namespace ArchiveFixer.Extraction;

/// <summary>真实文件系统（产品代码用这一个）。</summary>
public sealed class FileSystemSourceDeleteFileSystem : ISourceDeleteFileSystem
{
    public static FileSystemSourceDeleteFileSystem Instance { get; } = new();

    public bool FileExists(string path) => SourceCleanupService.SafeFileExists(path);

    public void DeleteFile(string path) => FileOperations.Delete(path);
}

