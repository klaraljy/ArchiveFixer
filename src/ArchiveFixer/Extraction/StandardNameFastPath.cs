using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// **第二大步标准名前置快通道的候选识别**（只读，不解压、不写盘）。
    ///
    /// <para>用户 2026-10-27 口径：同目录、同基名、全部标准名的组，可以跳过内容卡，
    /// 直接拿入口包试密码、只解一层；非均匀/加密头都按标准情况对待。</para>
    ///
    /// <para>本类只回答“哪些组满足标准名形状、入口是谁”；**不回答完整性**。
    /// 001–004 在本目录、005 在别处时，这里仍然是标准名组；是否真的能解开由调用方
    /// 的试解结果决定，失败必须交回正常第二大步，⛔ 不写内容卡 Unknown/Missing。</para>
    /// </summary>
    internal static class StandardNameFastPath
    {
        internal enum Family
        {
            RarPart,
            RarOld,
            SevenZip,
            ZipNumbered,
            ZipSpanned
        }

        internal sealed class Group
        {
            public string Directory { get; init; } = string.Empty;

            public string BaseName { get; init; } = string.Empty;

            public Family Family { get; init; }

            public string EntryPath { get; init; } = string.Empty;

            public IReadOnlyList<string> Pieces { get; init; } = Array.Empty<string>();
        }

        private static readonly Regex RarPartPattern = new(
            @"^(?<base>.+)\.part(?<num>[1-9]\d*)\.rar$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex RarOldBodyPattern = new(
            @"^(?<base>.+)\.rar$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex RarOldPartPattern = new(
            @"^(?<base>.+)\.r(?<num>\d{2})$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex SevenZipPattern = new(
            @"^(?<base>.+)\.7z\.(?<num>\d{3})$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex ZipNumberedPattern = new(
            @"^(?<base>.+)\.zip\.(?<num>\d{3})$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex ZipSpannedPartPattern = new(
            @"^(?<base>.+)\.z(?<num>\d{2})$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex ZipSpannedTailPattern = new(
            @"^(?<base>.+)\.zip$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>按目录 + 基名 + 族归组，只返回“每一片都是标准名”的组。</summary>
        internal static IReadOnlyList<Group> Find(IEnumerable<string>? paths)
        {
            var pieces = new List<(string Path, string Directory, string Base, Family Family, int Index)>();

            foreach (string path in paths ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                string name = Path.GetFileName(path);
                string directory = Path.GetDirectoryName(path) ?? string.Empty;

                if (TryParse(name, out string baseName, out Family family, out int index))
                {
                    pieces.Add((path, directory, baseName, family, index));
                }
            }

            var result = new List<Group>();

            foreach (IGrouping<(string Directory, string Base, Family Family), (string Path, string Directory, string Base, Family Family, int Index)> group
                     in pieces.GroupBy(
                         item => (item.Directory, item.Base, item.Family),
                         new GroupKeyComparer()))
            {
                List<(string Path, string Directory, string Base, Family Family, int Index)> members = group
                    .OrderBy(item => item.Index)
                    .ToList();

                if (!TryResolveEntry(members, group.Key.Family, out string entry))
                {
                    continue;
                }

                result.Add(new Group
                {
                    Directory = group.Key.Directory,
                    BaseName = group.Key.Base,
                    Family = group.Key.Family,
                    EntryPath = entry,
                    Pieces = members.Select(item => item.Path).ToList()
                });
            }

            return result;
        }

        private static bool TryResolveEntry(
            IReadOnlyList<(string Path, string Directory, string Base, Family Family, int Index)> members,
            Family family,
            out string entry)
        {
            entry = string.Empty;

            switch (family)
            {
                case Family.RarPart:
                    entry = members.FirstOrDefault(item => item.Index == 1).Path ?? string.Empty;
                    break;

                case Family.RarOld:
                    entry = members.FirstOrDefault(item => item.Index == 1).Path ?? string.Empty;
                    break;

                case Family.SevenZip:
                case Family.ZipNumbered:
                    entry = members.FirstOrDefault(item => item.Index == 1).Path ?? string.Empty;
                    break;

                case Family.ZipSpanned:
                    entry = members.FirstOrDefault(item => item.Index == 0).Path ?? string.Empty;
                    break;
            }

            return entry.Length > 0;
        }

        private static bool TryParse(
            string fileName,
            out string baseName,
            out Family family,
            out int index)
        {
            baseName = string.Empty;
            family = default;
            index = 0;

            Match match = RarPartPattern.Match(fileName);

            if (match.Success)
            {
                baseName = match.Groups["base"].Value;
                family = Family.RarPart;
                index = int.Parse(match.Groups["num"].Value, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }

            match = SevenZipPattern.Match(fileName);

            if (match.Success)
            {
                baseName = match.Groups["base"].Value;
                family = Family.SevenZip;
                index = int.Parse(match.Groups["num"].Value, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }

            match = ZipNumberedPattern.Match(fileName);

            if (match.Success)
            {
                baseName = match.Groups["base"].Value;
                family = Family.ZipNumbered;
                index = int.Parse(match.Groups["num"].Value, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }

            match = ZipSpannedTailPattern.Match(fileName);

            if (match.Success)
            {
                baseName = match.Groups["base"].Value;
                family = Family.ZipSpanned;
                index = 0;
                return true;
            }

            match = ZipSpannedPartPattern.Match(fileName);

            if (match.Success)
            {
                baseName = match.Groups["base"].Value;
                family = Family.ZipSpanned;
                index = int.Parse(match.Groups["num"].Value, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }

            match = RarOldBodyPattern.Match(fileName);

            if (match.Success)
            {
                baseName = match.Groups["base"].Value;
                family = Family.RarOld;
                index = 1;
                return true;
            }

            match = RarOldPartPattern.Match(fileName);

            if (match.Success)
            {
                baseName = match.Groups["base"].Value;
                family = Family.RarOld;
                index = int.Parse(match.Groups["num"].Value, System.Globalization.CultureInfo.InvariantCulture) + 2;
                return true;
            }

            return false;
        }

        private sealed class GroupKeyComparer : IEqualityComparer<(string Directory, string Base, Family Family)>
        {
            public bool Equals(
                (string Directory, string Base, Family Family) x,
                (string Directory, string Base, Family Family) y) =>
                string.Equals(x.Directory, y.Directory, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Base, y.Base, StringComparison.OrdinalIgnoreCase)
                && x.Family == y.Family;

            public int GetHashCode((string Directory, string Base, Family Family) obj) =>
                HashCode.Combine(
                    obj.Directory.ToUpperInvariant(),
                    obj.Base.ToUpperInvariant(),
                    obj.Family);
        }
    }
}
