using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ArchiveFixer.Storage
{
    /// <summary>
    /// 程序自带的内置密码清单（纯文本，一行一条，行顺序 = 尝试顺序）。
    ///
    /// <para>用户 2026-10-25：不再把外部密码本当活链接；外部导入只复制/合并进这里。
    /// 文件名固定英文 <c>passwords.txt</c>；默认在数据根目录下，随设置的数据根一起走。</para>
    ///
    /// <para>⚠ 这是**明文**，任何能打开数据目录的人都能看到密码；这是用户明确选择的可搬运方案。
    /// 旧的 DPAPI <c>password-list.dat</c> 只作为迁移来源保留备份，不再是主存储。</para>
    /// </summary>
    public sealed class PasswordTextStore
    {
        public const string FileName = "passwords.txt";

        public string DataRootDirectory { get; set; } = string.Empty;

        public string FilePath => string.IsNullOrWhiteSpace(DataRootDirectory)
            ? string.Empty
            : Path.Combine(DataRootDirectory, FileName);

        public bool Exists => FilePath.Length > 0 && File.Exists(FilePath);

        /// <summary>读取顺序清单。返回 false 表示没有文件；文件存在但读不了时抛异常给调用方处理。</summary>
        public bool TryLoad(out List<string> values, out string failureReason)
        {
            values = new List<string>();
            failureReason = string.Empty;

            string path = FilePath;

            if (path.Length == 0 || !File.Exists(path))
            {
                return false;
            }

            try
            {
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string value = line.TrimEnd('\r');

                    // 空行只是排版，不是密码；用户不会想试一个空串。
                    if (value.Length == 0)
                    {
                        continue;
                    }

                    values.Add(value);
                }

                return true;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                throw;
            }
        }

        /// <summary>原子写入；顺序就是传进来的顺序。旧文件先留一份 .bak。</summary>
        public bool TrySave(IEnumerable<string> values, out string failureReason)
        {
            failureReason = string.Empty;

            string path = FilePath;

            if (path.Length == 0)
            {
                failureReason = "数据根目录为空";
                return false;
            }

            try
            {
                Directory.CreateDirectory(DataRootDirectory);

                if (File.Exists(path))
                {
                    try
                    {
                        File.Copy(path, path + ".bak", overwrite: true);
                    }
                    catch
                    {
                        // 备份失败不阻断主保存。
                    }
                }

                string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                var builder = new StringBuilder();

                foreach (string value in values)
                {
                    if (value is null)
                    {
                        continue;
                    }

                    // 一行一条；密码里的换行无法在 txt 里表达，直接压成空格（密码本身极少有换行）。
                    builder.Append(value.Replace("\r", " ").Replace("\n", " "));
                    builder.Append("\r\n");
                }

                File.WriteAllText(temp, builder.ToString(), new UTF8Encoding(true));
                File.Move(temp, path, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                return false;
            }
        }
    }
}
