using System;
using System.IO;
using System.IO.Compression;

namespace ArchiveFixer.Detection
{
    /// <summary>
    /// 「**这是不是 Android 应用安装包**」的**唯一出口**（用户 2026-10-10 拍板：
    /// 「现在我觉得禁止解压真正的 apk 文件，因为有些是用户真的要转移到手机上进行安装的」）。
    ///
    /// <para><b>为什么需要它</b>（问题单 `2026-10-06-程序不该碰真正的APK（被解开并递归进内部）`）：
    /// APK 的内容本来就是 ZIP ⇒ 按魔数一定认得出是 ZIP ⇒ 老版本把它当普通包解开，
    /// 还在 `…\魔方.apk\lib\arm64-v8a\` **里面**建工作区、落产物、继续递归探内层包
    /// ⇒ 把"用户要拿去装到手机上的安装包"变成一堆目录 + 一个注定失败的 `.so` 任务 + 一句错方向的"补密码"。</para>
    ///
    /// <para><b>判据 = 内容证据，⛔ 不看后缀</b>（项目老规矩「不信后缀」）：ZIP 里**同时**存在
    /// <c>AndroidManifest.xml</c> 与 <c>classes.dex</c> ⇒ 真 APK。
    /// ⛔ 只认这两个：别的容器后缀（`.jar` / `.docx` / `.epub` / `.xlsx` … 共 18 个）**照旧当正常归档处理**
    /// —— 它们是用户真的要解的东西（用户 2026-10-10 定边界：**只挡 `.apk`**）。</para>
    ///
    /// <para>⛔ **判不出 ⇒ false（不拦）**：读不动 / 不是 ZIP / 结构坏了 ⇒ 照旧走原来的路
    /// （这一档是"补充闸门"，不许因为判据本身出错而把正常包挡下来）。</para>
    ///
    /// <para>⛔ **不许拿 <see cref="IArchiveProber.IsArchiveAsync"/> 当它用**：那条路同时喂着
    /// 「无用物」判定（<c>SourceJunkScanner</c>）—— 把 APK 说成"不是归档"会让它被判成垃圾，
    /// 那是比解开它更坏的事（用户要的是**一个字节都不碰**）。</para>
    /// </summary>
    public static class AndroidPackageDetector
    {
        /// <summary>APK 必备条目之一：清单文件。</summary>
        public const string ManifestEntryName = "AndroidManifest.xml";

        /// <summary>APK 必备条目之二：Dalvik 字节码。</summary>
        public const string DexEntryName = "classes.dex";

        /// <summary>
        /// 这一份是不是 Android 应用安装包（判据见类注释；判不出 / 读不动 ⇒ <c>false</c>）。
        /// </summary>
        public static bool IsAndroidPackage(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return false;
            }

            try
            {
                using var stream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                /*
                 * 先看 4 个字节的 ZIP 本地文件头（`PK\x03\x04`）：⛔ 别直接 new ZipArchive ——
                 * 这条判据会被"每一个新出现的产物"问到（一键处理的产物扫描），
                 * 对大文件先做一次 O(1) 的魔数筛，省掉无谓的解析。
                 */
                Span<byte> signature = stackalloc byte[4];

                if (stream.Read(signature) != signature.Length
                    || signature[0] != 0x50
                    || signature[1] != 0x4B
                    || signature[2] != 0x03
                    || signature[3] != 0x04)
                {
                    return false;
                }

                stream.Position = 0;

                using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

                bool hasManifest = false;
                bool hasDex = false;

                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    // APK 的这两个条目都在**根**上；⛔ 不许按"结尾像"匹配（嵌套包里同名条目不算）。
                    string name = entry.FullName;

                    if (string.Equals(name, ManifestEntryName, StringComparison.OrdinalIgnoreCase))
                    {
                        hasManifest = true;
                    }
                    else if (string.Equals(name, DexEntryName, StringComparison.OrdinalIgnoreCase))
                    {
                        hasDex = true;
                    }

                    if (hasManifest && hasDex)
                    {
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                // 读不动 / 不是 ZIP / 结构坏了 ⇒ 判不出 ⇒ 不拦（照旧走原来的路）。
                return false;
            }
        }
    }
}
