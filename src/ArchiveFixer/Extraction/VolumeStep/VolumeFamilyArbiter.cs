using System;
using System.Collections.Generic;
using System.Linq;
using ArchiveFixer.Detection;

namespace ArchiveFixer.Extraction
{
    /// <summary>一个基名的族选择结论。</summary>
    public sealed class VolumeFamilySelection
    {
        public VolumeNamingFamily? Family { get; init; }

        /// <summary>同一基名下出现两种以上的强锚点 ⇒ 不混组。</summary>
        public bool Conflict { get; init; }

        public string Evidence { get; init; } = string.Empty;

        public bool IsKnown => Family != null && !Conflict;
    }

    /// <summary>
    /// **族仲裁**（共用骨架）：只回答"同基名的这些账本项属于哪一族"。
    ///
    /// <para>判据只用**强锚点**：7z 首片、RAR 成员、跨盘 ZIP 末片盘号、通用 ZIP 的 EOCD/本地头。
    /// 内容 Unknown 的中间片不投票；它们跟着锚点走。</para>
    ///
    /// <para>⛔ 一族只允许一个结论；同基名出现两种强锚点 ⇒ 冲突，交给上层落 SizeAnomaly/Unknown，
    /// 不许按"先看到谁"猜。</para>
    /// </summary>
    public static class VolumeFamilyArbiter
    {
        public static VolumeFamilySelection Select(FirstStepFileLedger ledger, string? baseName)
        {
            if (ledger == null || string.IsNullOrWhiteSpace(baseName))
            {
                return new VolumeFamilySelection { Evidence = "账本/基名为空 ⇒ 判不出" };
            }

            List<FirstStepFileLedgerEntry> entries = ledger.SameBase(baseName)
                .Where(entry => entry.IsGatePassed)
                .ToList();

            if (entries.Count == 0)
            {
                return new VolumeFamilySelection { Evidence = "同基名没有闸门通过的账本项" };
            }

            bool sevenZip = entries.Any(entry =>
                entry.Format == VolumeContentFormat.SevenZip
                && entry.BodyKind is ArchiveBodyKind.VolumeFirstPiece or ArchiveBodyKind.VolumePiece);

            bool rar = entries.Any(entry =>
                entry.Format == VolumeContentFormat.Rar
                && (entry.Membership == VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember
                    || entry.BodyKind is ArchiveBodyKind.VolumeFirstPiece or ArchiveBodyKind.VolumePiece
                    || entry.RarVariant != RarVariant.Unknown));

            /*
             * 跨盘族的**内容证据**：内容卷号已证（VolumeNumber≥2）或末片 EOCD 说"分盘"——
             * 两者都要求 Membership=VolumeMember（单凭 split 位不算：通用族的守门形状也带 split 位）。
             * 账本项可以没有 ZipVariant（不是所有账本项都走 ResolveZipVariant），所以这里不读它。
             */
            bool zipSpannedByContent = entries.Any(entry =>
                entry.Format == VolumeContentFormat.Zip
                && entry.Membership == VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember
                && (entry.ZipEndRecordMeansSplit
                    || (entry.VolumeNumber is int spannedNumber && spannedNumber >= 2)));

            /*
             * 强锚点投票：Spanned = 内容证据，或该片已被定成 Spanned，或内容卷号≥2；
             * Numbered = 该片不是 Spanned、不是"可证非成员"，且有本地头/EOCD/卷成员身份。
             * 内容 Unknown 的中间片跟着锚点走。
             */
            bool zipSpanned = zipSpannedByContent || entries.Any(entry =>
                entry.Format == VolumeContentFormat.Zip
                && (entry.ZipVariant == ZipVariant.Spanned
                    || (entry.Membership == VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember
                        && entry.VolumeNumber is int number
                        && number >= 2)));

            /*
             * 通用族的第一片同样"有本地头"（`HasZipLocalHeader`），单凭它投 Numbered 必然与跨盘末片
             * 撞成 `SizeAnomaly（不混组）`。真机 901000018 / 901000036（调试器读出的账本事实）：
             * 第一片被包装改名成 `bbb-L2.jpg` / `打包111-L2.png` —— `.z01` 名字没了、只剩本地头，
             * 而末片的 EOCD 盘号明确说"这一组是跨盘的"。⇒ 有跨盘内容证据时，本地头那一档不投票；
             * 没有该证据时行为与原来逐字相同。
             */
            bool zipNumbered = !zipSpannedByContent && entries.Any(entry =>
                entry.Format == VolumeContentFormat.Zip
                && entry.ZipVariant != ZipVariant.Spanned
                && entry.Membership != VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember
                && (entry.ZipEndRecordMeansSplit
                    || entry.HasZipLocalHeader
                    || entry.BodyKind is ArchiveBodyKind.VolumeFirstPiece or ArchiveBodyKind.VolumePiece));

            var families = new List<VolumeNamingFamily>();

            if (sevenZip)
            {
                families.Add(VolumeNamingFamily.SevenZipNumbered);
            }

            if (rar)
            {
                families.Add(VolumeNamingFamily.RarPart);
            }

            if (zipSpanned)
            {
                families.Add(VolumeNamingFamily.ZipSpanned);
            }

            if (zipNumbered)
            {
                families.Add(VolumeNamingFamily.ZipNumbered);
            }

            if (families.Count == 0)
            {
                return new VolumeFamilySelection
                {
                    Evidence = "同基名没有 7z/RAR/ZIP 的强锚点 ⇒ 判不出"
                };
            }

            if (families.Count > 1)
            {
                return new VolumeFamilySelection
                {
                    Conflict = true,
                    Evidence = $"同基名同时出现 {string.Join("、", families)} 的强锚点 ⇒ 不混组"
                };
            }

            return new VolumeFamilySelection
            {
                Family = families[0],
                Evidence = $"族锚点 = {families[0]}"
            };
        }
    }
}
