using System;
using System.IO;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 账本事实必须单调：后到的低信息登记（兄弟片补录）不能把已确认的名字槽/租约/角色抹掉。
    /// 2026-11-06 真机 `_2.7z` 七片只剩 1/3/7 有槽，根因就在这里。
    /// </summary>
    public sealed class FirstStepLedgerMonotonicFactsTests
    {
        [Fact]
        public void 同路径后到的低信息项_不得抹掉名字槽和候选租约()
        {
            string path = Path.Combine(Path.GetTempPath(), "af-ledger-monotonic", "打包_2.7z.002");

            var ledger = new FirstStepFileLedger();

            ledger.AddOrReplace(new FirstStepFileLedgerEntry
            {
                Path = path,
                BaseName = "打包_2",
                Size = 1_048_576_000,
                Format = VolumeContentFormat.SevenZip,
                BodyKind = ArchiveBodyKind.Unknown,
                NameSlot = 2,
                NameEvidence = "名字槽",
                Role = ArtifactRole.CandidateVolumePiece,
                CandidateLeaseId = "lease-1",
                SourceLayer = Path.GetDirectoryName(path)!
            });

            FirstStepFileLedgerEntry merged = ledger.AddOrReplace(new FirstStepFileLedgerEntry
            {
                Path = path,
                BaseName = "打包_2",
                Size = 1_048_576_000,
                Format = VolumeContentFormat.Unknown,
                SourceLayer = Path.GetDirectoryName(path)!
            });

            Assert.Equal(2, merged.NameSlot);
            Assert.Equal("名字槽", merged.NameEvidence);
            Assert.Equal(ArtifactRole.CandidateVolumePiece, merged.Role);
            Assert.Equal("lease-1", merged.CandidateLeaseId);
            Assert.Equal(VolumeContentFormat.SevenZip, merged.Format);
            Assert.True(merged.HasSecondStepEvidence);
        }

        [Fact]
        public void 内容物没有分卷事实_不得进入第二大步候选()
        {
            var content = new FirstStepFileLedgerEntry
            {
                Path = Path.Combine(Path.GetTempPath(), "af-ledger-content", "SSNI-727-UC.mp4"),
                BaseName = "SSNI-727-UC",
                Size = 6_919_473_224,
                Format = VolumeContentFormat.Unknown,
                Membership = VolumeNumberFromContent.VolumeMembershipVerdict.Unknown
            };

            Assert.False(content.HasSecondStepEvidence);

            var piece = new FirstStepFileLedgerEntry
            {
                Path = Path.Combine(Path.GetTempPath(), "af-ledger-content", "打包_2.7z.002"),
                BaseName = "打包_2",
                NameSlot = 2
            };

            Assert.True(piece.HasSecondStepEvidence);
        }

        [Fact]
        public void 其余物里的过程物_不再算成品目录残留()
        {
            string root = Path.Combine(Path.GetTempPath(), "af-rest-scope-" + Guid.NewGuid().ToString("N"));
            string outputRoot = Path.Combine(root, "打包");
            string restFile = Path.Combine(outputRoot, "其余物", "打包.7z");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(restFile)!);
                File.WriteAllText(restFile, "process");

                Assert.True(ProcessArtifactLayout.IsInsideArtifactDirectory(
                    restFile,
                    Path.Combine(outputRoot, ProcessArtifactLayout.ArtifactDirectoryName)));
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                }
            }
        }
    }
}
