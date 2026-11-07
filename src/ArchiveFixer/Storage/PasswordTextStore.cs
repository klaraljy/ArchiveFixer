using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ArchiveFixer.Storage
{
    /// <summary>
    /// 程序自带的内置密码清单（文件名仍为 <c>passwords.txt</c>，一行一条，行顺序 = 尝试顺序）。
    ///
    /// <para>用户 2026-10-25：不再把外部密码本当活链接；外部导入只复制/合并进这里。
    /// 文件名固定英文 <c>passwords.txt</c>；默认在数据根目录下，随设置的数据根一起走。</para>
    ///
    /// <para>2026-11-07：生产使用 DPAPI（当前 Windows 用户）加密写入；旧明文/旧加密文件
    /// 仍能读，读入后下一次保存自动升级。测试夹具可通过 <see cref="EncryptAtRest"/> 明确
    /// 选择明文，以便继续验证旧格式；生产入口由 MainViewModel 打开加密。</para>
    /// </summary>
    public sealed class PasswordTextStore
    {
        public const string FileName = "passwords.txt";

        internal const string EncryptedHeader = "ARCHIVEFIXER-PASSWORDS-DPAPI-V1\n";

        private static readonly byte[] EncryptedHeaderBytes = Encoding.ASCII.GetBytes(EncryptedHeader);

        /// <summary>
        /// 是否加密写入。生产入口显式置 true；测试夹具默认 false，保留旧明文测试语义。
        /// 读取永远同时兼容明文与加密，不依赖这个开关。
        /// </summary>
        public bool EncryptAtRest { get; set; }

        /// <summary>最近一次读取失败是否因为加密文件解不开（用于阻止旧记忆覆盖）。</summary>
        public bool LastLoadWasEncryptedFailure { get; private set; }

        /// <summary>最近一次成功读取的文件是否已经是 DPAPI 加密格式。</summary>
        public bool LastLoadWasEncrypted { get; private set; }

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
            LastLoadWasEncryptedFailure = false;
            LastLoadWasEncrypted = false;

            string path = FilePath;

            if (path.Length == 0 || !File.Exists(path))
            {
                return false;
            }

            try
            {
                byte[] raw = File.ReadAllBytes(path);

                if (raw.AsSpan().StartsWith(EncryptedHeaderBytes))
                {
                    LastLoadWasEncrypted = true;

                    try
                    {
                        string encoded = Encoding.UTF8.GetString(
                            raw,
                            EncryptedHeaderBytes.Length,
                            raw.Length - EncryptedHeaderBytes.Length).Trim();

                        byte[] cipher = Convert.FromBase64String(encoded);
                        byte[] plain = PasswordTextDpapi.Unprotect(cipher);
                        string text = Encoding.UTF8.GetString(plain);

                        ParseLines(text, values);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        LastLoadWasEncryptedFailure = true;
                        failureReason = "密码本解密失败（可能换了 Windows 用户/机器，或文件被改动）：" + ex.Message;
                        throw new InvalidDataException(failureReason, ex);
                    }
                }

                ParseLines(Encoding.UTF8.GetString(raw), values);
                return true;
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                throw;
            }
        }

        private static void ParseLines(string text, List<string> values)
        {
            // 旧明文文件用 File.WriteAllText(..., UTF8Encoding(true)) 写时会带 BOM；
            // 第一行不能被读成 "\uFEFF密码"。
            if (text.Length > 0 && text[0] == '\uFEFF')
            {
                text = text[1..];
            }

            foreach (string line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string value = line.TrimEnd('\r');

                // 空行只是排版，不是密码；用户不会想试一个空串。
                if (value.Length == 0)
                {
                    continue;
                }

                values.Add(value);
            }
        }

        /// <summary>
        /// 主清单 <c>passwords.txt</c> 不见了、但同目录还留着上一次保存自动生成的
        /// <c>passwords.txt.bak</c> 时，用备份把它恢复回来。
        ///
        /// <para>为什么必须是"产品自己的备份"而不是加密记忆：2026-11-01 真机，主清单丢失后
        /// 程序回落到很旧的 DPAPI 记忆（只剩 1 条测试密码），把它写回 txt，用户的 13 条全没了。
        /// 备份是最后一次保存前的真实内容，拿它恢复是事实，不是猜。</para>
        ///
        /// <para>⛔ 只在主文件**不存在**时用；主文件在就一个字节都不动。</para>
        /// </summary>
        public bool TryRestoreFromBackup(out string failureReason)
        {
            failureReason = string.Empty;

            string path = FilePath;

            if (path.Length == 0)
            {
                failureReason = "数据根目录为空";
                return false;
            }

            if (File.Exists(path))
            {
                return false;
            }

            string backup = path + ".bak";

            if (!File.Exists(backup))
            {
                failureReason = "没有 passwords.txt.bak";
                return false;
            }

            try
            {
                File.Copy(backup, path, overwrite: false);
                return true;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 把当前值写出一份**明文副本**（显式导出用）。只写目标文件，绝不移动/删除内置密码本。
        /// </summary>
        public static bool TryExportPlaintext(
            string destinationPath,
            IEnumerable<string> values,
            out string failureReason)
        {
            failureReason = string.Empty;

            if (string.IsNullOrWhiteSpace(destinationPath))
            {
                failureReason = "导出路径为空";
                return false;
            }

            try
            {
                var builder = new StringBuilder();

                foreach (string value in values)
                {
                    if (value is null)
                    {
                        continue;
                    }

                    builder.Append(value.Replace("\r", " ").Replace("\n", " "));
                    builder.Append("\r\n");
                }

                string? directory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(destinationPath, builder.ToString(), new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 旧文件 → .bak。加密模式下，若旧文件还是明文，备份也先加密，
        /// 避免“主文件已加密、.bak 仍是明文”的漏口。
        /// </summary>
        private void TryWriteBackup(string path)
        {
            try
            {
                string backup = path + ".bak";
                byte[] old = File.ReadAllBytes(path);

                if (!EncryptAtRest || old.AsSpan().StartsWith(EncryptedHeaderBytes))
                {
                    File.WriteAllBytes(backup, old);
                    return;
                }

                byte[] cipher = PasswordTextDpapi.Protect(old);
                File.WriteAllText(
                    backup,
                    EncryptedHeader + Convert.ToBase64String(cipher),
                    new UTF8Encoding(false));
            }
            catch
            {
                // 备份失败不阻断主保存；原有的恢复逻辑仍会尝试主文件/旧 .bak。
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

            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                Directory.CreateDirectory(DataRootDirectory);

                if (File.Exists(path))
                {
                    TryWriteBackup(path);
                }

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

                string plaintext = builder.ToString();

                if (EncryptAtRest)
                {
                    byte[] cipher = PasswordTextDpapi.Protect(Encoding.UTF8.GetBytes(plaintext));
                    string encoded = Convert.ToBase64String(cipher);
                    File.WriteAllText(temp, EncryptedHeader + encoded, new UTF8Encoding(false));
                }
                else
                {
                    File.WriteAllText(temp, plaintext, new UTF8Encoding(true));
                }

                File.Move(temp, path, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                return false;
            }
            finally
            {
                try
                {
                    if (File.Exists(temp))
                    {
                        File.Delete(temp);
                    }
                }
                catch
                {
                }
            }
        }
    }
}
