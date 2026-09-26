using System;
using System.IO;

namespace ArchiveFixer.Helpers
{
    /// <summary>
    /// 长**文件名**的中间省略（用户 2026-09-26 审计提出的那一条）。
    ///
    /// <para><b>用户原话</b>：「还有文件名过长的问题，我们改后缀看的主要是文件后缀的变化，
    /// 如果名字太长导致后缀都显示不了，这就非常的奇怪了，如果名字过长我们就可以自动缩短，
    /// 比如在中间显示........，最重要的是能清楚的看到后缀的变化」。</para>
    ///
    /// <para><b>为什么不能用 <c>TextTrimming="CharacterEllipsis"</c></b>：WPF 那个是**尾省略**——
    /// 从右边开始截，第一个没的就是后缀，正好把用户唯一要看的东西截掉
    /// （改名预览窗里一列 150px 的「原文件名」就是这么干的）。</para>
    ///
    /// <para><b>规则</b>：保留**头部**与**尾部**，中间用 <c>…</c>；尾部优先保留后缀，
    /// 后缀若由两段组成（<c>.7z.001</c> / <c>.rar.jpg</c> / <c>.tar.gz</c>）且不太长，
    /// 就把两段都留下 —— 这三类正是用户真正会盯着看的形状；没有后缀的文件保留末尾一小段
    /// （至少能看出这是哪一个）。宽度按**显示宽度**算：ASCII 记 1、其余（中日韩等全角字符）记 2，
    /// 于是"40 个汉字"不会因为按字符数算而溢出半格。</para>
    ///
    /// <para><b>边界</b>：空 / 空白 / 短名 / 省略之后不比原来短 —— 一律**原样返回**；
    /// 不碰磁盘、不抛异常（省略规则错了只会让人看不清名字，不会报错 —— 所以它是纯函数、单独测）。</para>
    /// </summary>
    public static class FileNameMiddleEllipsis
    {
        /// <summary>中间那一截的写法（单个字符，与界面其它地方的"中间省略"手感一致）。</summary>
        public const char EllipsisChar = '…';

        /// <summary>两段后缀合起来不超过它才一起保留（<c>.7z.001</c>=7、<c>.rar.jpg</c>=8、<c>.tar.gz</c>=7）。</summary>
        private const int MaxTailExtensionUnits = 12;

        /// <summary>最少要留几个单位的头部 —— 低于它就宁可不省略（省略反而看不懂）。</summary>
        private const int MinimumHeadUnits = 4;

        /// <summary>没有后缀的文件，尾部保留多少单位。</summary>
        private const int NoExtensionTailUnits = 12;

        /// <summary>省略号自己占几个单位（<c>…</c> 是非 ASCII，按 <see cref="Measure"/> 的规则记 2）。</summary>
        private const int EllipsisUnits = 2;

        /// <summary>
        /// 把文件名中间省略到大约 <paramref name="maxUnits"/> 个"半角字符宽"。
        /// </summary>
        /// <param name="fileName">文件名（不含路径；传路径进来只会把路径当名字一起省）。</param>
        /// <param name="maxUnits">宽度上限（半角字符数；全角字符算 2）。默认 36 ≈ 260px 宽的一列。</param>
        public static string Elide(string? fileName, int maxUnits = 36)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return string.Empty;
            }

            string original = fileName.Trim();

            if (maxUnits < MinimumHeadUnits + 2)
            {
                maxUnits = MinimumHeadUnits + 2;
            }

            if (Measure(original) <= maxUnits)
            {
                return original;
            }

            string tail = ResolveTail(original, maxUnits);
            int tailUnits = Measure(tail);
            int headBudget = maxUnits - tailUnits - EllipsisUnits;

            if (headBudget < MinimumHeadUnits)
            {
                // 尾巴太长（例如一长串没有后缀的名字）：宁可不省略，也不给出一个只剩后缀的假名字。
                return original;
            }

            string elided = TakeHead(original, headBudget) + EllipsisChar + tail;

            return Measure(elided) < Measure(original) ? elided : original;
        }

        /// <summary>显示宽度：ASCII 记 1，其余记 2（粗略但足够决定"截到哪"）。</summary>
        internal static int Measure(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            int units = 0;

            foreach (char ch in text)
            {
                units += ch < 128 ? 1 : 2;
            }

            return units;
        }

        /// <summary>从左边取够 <paramref name="budget"/> 个单位（不会把一个全角字符劈成半个）。</summary>
        private static string TakeHead(string text, int budget)
        {
            int units = 0;
            int index = 0;

            while (index < text.Length)
            {
                int width = text[index] < 128 ? 1 : 2;

                if (units + width > budget)
                {
                    break;
                }

                units += width;
                index++;
            }

            return text[..index];
        }

        /// <summary>
        /// 尾部要保留的那一段：优先"最后两段后缀"，其次"最后一段后缀"，没有后缀就保留末尾一小段。
        /// </summary>
        private static string ResolveTail(string fileName, int maxUnits)
        {
            int tailBudget = Math.Max(MinimumHeadUnits, maxUnits / 2);

            string lastExtension = SafeExtension(fileName);
            string withoutLast = string.IsNullOrEmpty(lastExtension)
                ? fileName
                : fileName[..^lastExtension.Length];

            string tail = lastExtension;

            if (!string.IsNullOrEmpty(lastExtension) && withoutLast.Length > 0)
            {
                string previousExtension = SafeExtension(withoutLast);

                if (!string.IsNullOrEmpty(previousExtension) &&
                    Measure(previousExtension) + Measure(lastExtension) <= MaxTailExtensionUnits)
                {
                    tail = previousExtension + lastExtension;
                }
            }

            if (string.IsNullOrEmpty(tail))
            {
                // 没有后缀（或整名就是一个 ".gitignore" 这种）：保留末尾一小段。
                tail = TakeTail(fileName, Math.Min(NoExtensionTailUnits, tailBudget));
            }

            if (Measure(tail) > tailBudget)
            {
                tail = string.IsNullOrEmpty(lastExtension)
                    ? TakeTail(fileName, tailBudget)
                    : lastExtension;
            }

            return tail;
        }

        /// <summary>从右边取够 <paramref name="budget"/> 个单位的尾巴。</summary>
        private static string TakeTail(string text, int budget)
        {
            int units = 0;
            int index = text.Length;

            while (index > 0)
            {
                int width = text[index - 1] < 128 ? 1 : 2;

                if (units + width > budget)
                {
                    break;
                }

                units += width;
                index--;
            }

            return text[index..];
        }

        /// <summary>
        /// <see cref="Path.GetExtension(string)"/> 的安全版：整名就是后缀（<c>.gitignore</c>）时返回空 ——
        /// 那种名字没有"主体"，把它当后缀保留下来只会让人以为这是别的文件。
        /// </summary>
        private static string SafeExtension(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return string.Empty;
            }

            string extension;

            try
            {
                extension = Path.GetExtension(fileName);
            }
            catch
            {
                return string.Empty;
            }

            return extension.Length >= fileName.Length ? string.Empty : extension;
        }
    }
}
