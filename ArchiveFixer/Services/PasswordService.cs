using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;

namespace ArchiveFixer.Services
{
    public class PasswordService
    {
        public ObservableCollection<PasswordItem> Passwords { get; } = new();

        public List<PasswordItem> GetPasswordCandidates(
            ArchiveTask task,
            string globalPassword,
            IEnumerable<PasswordItem> passwordList,
            bool tryEmptyFirst)
        {
            var result = new List<PasswordItem>();
            var used = new HashSet<string>(StringComparer.Ordinal);

            void AddCandidate(string value, string source, string remark)
            {
                value ??= string.Empty;

                if (!used.Add(value))
                {
                    return;
                }

                result.Add(new PasswordItem
                {
                    Value = value,
                    Source = source,
                    IsEnabled = true,
                    Remark = remark
                });
            }

            if (tryEmptyFirst)
            {
                AddCandidate(string.Empty, "Empty", "空密码");
            }

            if (task != null && !string.IsNullOrEmpty(task.Password))
            {
                AddCandidate(task.Password, "TaskPassword", "单任务密码");
            }

            if (!string.IsNullOrEmpty(globalPassword))
            {
                AddCandidate(globalPassword, "GlobalPassword", "统一密码");
            }

            if (passwordList != null)
            {
                int index = 0;

                foreach (PasswordItem item in passwordList)
                {
                    index++;

                    if (item == null)
                    {
                        continue;
                    }

                    if (!item.IsEnabled)
                    {
                        continue;
                    }

                    /*
                     * 注意：
                     * 密码允许是空格开头、空格结尾。
                     * 不要 Trim。
                     */
                    string value = item.Value ?? string.Empty;

                    AddCandidate(value, "ImportedList", $"密码列表第 {index} 项");
                }
            }

            /*
             * 如果用户关闭了空密码优先，并且没有任何密码，
             * 仍然补一个空密码，保证普通无密码压缩包能解压。
             */
            if (result.Count == 0)
            {
                AddCandidate(string.Empty, "Empty", "空密码");
            }

            return result;
        }

        public List<PasswordItem> ImportPasswordList(string txtPath)
        {
            var imported = new List<PasswordItem>();

            if (string.IsNullOrWhiteSpace(txtPath))
            {
                return imported;
            }

            if (!File.Exists(txtPath))
            {
                return imported;
            }

            /*
             * 用 UTF8 自动识别 BOM。
             * 如果你的密码 txt 是 ANSI，.NET 通常也能读取部分中文；
             * 后续如果需要可以增加编码选择。
             */
            string[] lines = File.ReadAllLines(txtPath, Encoding.UTF8);

            foreach (string rawLine in lines)
            {
                /*
                 * File.ReadAllLines 已经去掉换行符。
                 * 这里不要 Trim。
                 * 因为密码可能本来就带前后空格。
                 */
                string password = rawLine ?? string.Empty;

                if (password.Length == 0)
                {
                    continue;
                }

                var item = new PasswordItem
                {
                    Value = password,
                    Source = "ImportedList",
                    IsEnabled = true,
                    Remark = "导入"
                };

                Passwords.Add(item);
                imported.Add(item);
            }

            RemoveDuplicatePasswordsKeepOrder();

            return imported;
        }

        public void AddPassword(string password)
        {
            password ??= string.Empty;

            Passwords.Add(new PasswordItem
            {
                Value = password,
                Source = "ManualList",
                IsEnabled = true,
                Remark = "手动添加"
            });

            RemoveDuplicatePasswordsKeepOrder();
        }

        public void RemovePassword(PasswordItem item)
        {
            if (item == null)
            {
                return;
            }

            Passwords.Remove(item);
        }

        public void ClearPasswords()
        {
            Passwords.Clear();
        }

        public void MoveUp(PasswordItem item)
        {
            if (item == null)
            {
                return;
            }

            int index = Passwords.IndexOf(item);

            if (index <= 0)
            {
                return;
            }

            Passwords.Move(index, index - 1);
        }

        public void MoveDown(PasswordItem item)
        {
            if (item == null)
            {
                return;
            }

            int index = Passwords.IndexOf(item);

            if (index < 0 || index >= Passwords.Count - 1)
            {
                return;
            }

            Passwords.Move(index, index + 1);
        }

        public string MaskPassword(string password)
        {
            if (string.IsNullOrEmpty(password))
            {
                return "空密码";
            }

            return "******";
        }

        public string BuildTryPasswordLogText(PasswordItem candidate, int index)
        {
            if (candidate == null)
            {
                return $"尝试密码候选第 {index} 项：******";
            }

            string source = candidate.Source ?? string.Empty;

            return source switch
            {
                "Empty" => "尝试空密码",
                "TaskPassword" => "尝试单任务密码：******",
                "GlobalPassword" => "尝试统一密码：******",
                "ImportedList" => $"尝试密码列表第 {index} 项：******",
                "ManualList" => $"尝试手动密码第 {index} 项：******",
                _ => $"尝试密码候选第 {index} 项：******"
            };
        }

        private void RemoveDuplicatePasswordsKeepOrder()
        {
            var used = new HashSet<string>(StringComparer.Ordinal);

            for (int i = Passwords.Count - 1; i >= 0; i--)
            {
                string value = Passwords[i].Value ?? string.Empty;

                if (!used.Add(value))
                {
                    Passwords.RemoveAt(i);
                }
            }

            /*
             * 上面从后往前会保留最后一个。
             * 如果你想保留第一个，可以用下面重建方式。
             */
            var ordered = Passwords.ToList();
            Passwords.Clear();

            used.Clear();

            foreach (PasswordItem item in ordered)
            {
                string value = item.Value ?? string.Empty;

                if (used.Add(value))
                {
                    Passwords.Add(item);
                }
            }
        }
    }
}
