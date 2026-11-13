using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;

namespace ArchiveFixer.Extraction
{    /// <summary>真实文件系统的占用探测。</summary>
    public sealed class FileSystemArtifactTargetProbe : IArtifactTargetProbe
    {
        public static FileSystemArtifactTargetProbe Instance { get; } = new();

        public bool Exists(string path)
        {
            return SafePathHelper.FileExists(path) || SafePathHelper.DirectoryExists(path);
        }

        public bool? IsDirectory(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return null;
                }

                return (File.GetAttributes(path) & FileAttributes.Directory) != 0;
            }
            catch
            {
                return null;
            }
        }
    }

}
