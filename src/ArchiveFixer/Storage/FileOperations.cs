using System.IO;

namespace ArchiveFixer.Storage
{
    /// <summary>
    /// 文件删除/移动的统一入口。ViewModels 等业务代码不许直接调用 <see cref="File"/>；
    /// 删除闸门、日志与回滚策略以后都挂在这里。
    /// </summary>
    public static class FileOperations
    {
        public static void Delete(string path) => File.Delete(path);

        public static void Move(string source, string destination) => File.Move(source, destination);

        public static void Move(string source, string destination, bool overwrite) =>
            File.Move(source, destination, overwrite);
    }
}
