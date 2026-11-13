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
{    /// <summary>
     /// 源包搬运要碰的那几件文件系统操作。
     ///
     /// <para>
     /// 抽出来只为一件事：**跨盘那条路必须能被验证**。真机上很难临时造出第二个卷，
     /// 而"跨盘 = 复制成功后再删原件、复制失败源包一个字节都不动"是决策 D-12 的红线，
     /// 不能只靠肉眼读代码（单测注入假实现就能把这条路走一遍，见 SourcePackageMoverTests）。
     /// </para>
     /// </summary>
    public interface ISourceMoveFileSystem
    {
        bool FileExists(string path);

        bool DirectoryExists(string path);

        /// <summary>建目录；建不出来返回 false（调用方据此报失败，绝不当成"搬成功了"）。</summary>
        bool CreateDirectory(string path);

        long GetFileSize(string path);

        /// <summary>两个路径是不是同一个卷（同卷才能用原子改名）。</summary>
        bool IsSameVolume(string sourcePath, string targetPath);

        /// <summary>同卷：原子改名。<b>目标已存在必须抛异常，绝不覆盖</b>。</summary>
        void MoveFile(string source, string target);

        /// <summary>跨卷：复制。<b>目标已存在必须抛异常，绝不覆盖</b>。</summary>
        void CopyFile(string source, string target);

        void DeleteFile(string path);
    }

}
