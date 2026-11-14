using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 2026-11-11 真机 336 形状：**同一基名的普通 PNG 与 7z 分卷首片同目录/跨目录**
    /// （`336.png` 53,418 字节、`336.7z.001` 636,736,722 自述整包、`336.7z.002`）。
    ///
    /// <para>旧行为：PNG 被 `CollectPieces` 当同基名候选收进组 ⇒ 已收字节合计比自述整包多 53,418
    /// ⇒ `SizeAnomaly` ⇒ 首片永久 Pending。现在钉三件事：① 普通媒体不进候选；
    /// ② 脏后缀首片仍能被标准名快通道覆盖；③ 普通媒体不会撑大 `ReceivedBytes`。</para>
    /// </summary>
    public sealed class GroupAnalysisContentMediaTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "af-group-media-" + Guid.NewGuid().ToString("N"));

        public GroupAnalysisContentMediaTests()
        {
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
                // 临时目录清不掉不影响结论。
            }
        }

        [Fact]
        public void 同基名普通PNG_不得撑大七z分卷组的已收字节()
        {
            string first = Create("336.7z.001删", 100);
            string second = Create("336.7z.002", 20);
            string png = Create("336.png", BuildPng());

            var handoffs = new[]
            {
                FirstPiece(first, 100, 120),
                Piece(second, 20, 2)
            };

            VolumeCandidateDiscovery.VolumeCandidate candidate = Assert.Single(
                VolumeCandidateDiscovery.Analyze(handoffs, new[] { _root }));

            Assert.Equal(2, candidate.Pieces.Count);
            Assert.DoesNotContain(png, candidate.Pieces);
            Assert.Equal(120, candidate.ReceivedBytes);
            Assert.Equal(first, candidate.EntryPath);
            Assert.Contains("连成 1..2", candidate.Evidence, StringComparison.Ordinal);
            Assert.DoesNotContain("> 自述整包", candidate.Evidence, StringComparison.Ordinal);
        }

        [Fact]
        public void 跨目录同基名普通PNG_不得撑大七z分卷组的已收字节()
        {
            string piecesDir = Path.Combine(_root, "336");
            string pngDir = Path.Combine(_root, "3");
            Directory.CreateDirectory(piecesDir);
            Directory.CreateDirectory(pngDir);

            string first = CreateIn(piecesDir, "336.7z.001删", 100);
            string second = CreateIn(piecesDir, "336.7z.002", 20);
            string png = CreateIn(pngDir, "336.png", BuildPng());

            var handoffs = new[]
            {
                FirstPiece(first, 100, 120),
                Piece(second, 20, 2)
            };

            VolumeCandidateDiscovery.VolumeCandidate candidate = Assert.Single(
                VolumeCandidateDiscovery.Analyze(handoffs, new[] { piecesDir, pngDir }));

            Assert.Equal(2, candidate.Pieces.Count);
            Assert.DoesNotContain(png, candidate.Pieces);
            Assert.Equal(120, candidate.ReceivedBytes);
        }

        [Fact]
        public void 同基名非媒体无关文件_按几何不成立不得算进分卷组()
        {
            string first = Create("336.7z.001删", 100);
            string second = Create("336.7z.002", 20);
            string unrelated = Create("336.txt", 30);

            var handoffs = new[]
            {
                FirstPiece(first, 100, 120),
                Piece(second, 20, 2)
            };

            VolumeCandidateDiscovery.VolumeCandidate candidate = Assert.Single(
                VolumeCandidateDiscovery.Analyze(handoffs, new[] { _root }));

            Assert.Equal(2, candidate.Pieces.Count);
            Assert.DoesNotContain(unrelated, candidate.Pieces);
            Assert.Equal(120, candidate.ReceivedBytes);
            Assert.Contains("已忽略 1 份", candidate.Evidence, StringComparison.Ordinal);
        }

        [Fact]
        public async System.Threading.Tasks.Task 脏后缀首片加普通PNG_候选全被标准名快通道覆盖()
        {
            string first = Path.Combine(_root, "336.7z.001删");
            string second = Path.Combine(_root, "336.7z.002");
            string png = Path.Combine(_root, "336.png");

            var ledger = new FirstStepFileLedger();

            ledger.AddOrReplace(new FirstStepFileLedgerEntry
            {
                Path = first,
                BaseName = "336",
                Size = 100,
                Format = VolumeContentFormat.SevenZip,
                BodyKind = ArchiveBodyKind.VolumeFirstPiece,
                FirstVolumeFlag = true,
                NameSlot = 1,
                DeclaredTotalBytes = 120,
                SourceLayer = _root,
                IsGatePassed = true
            });

            ledger.AddOrReplace(new FirstStepFileLedgerEntry
            {
                Path = second,
                BaseName = "336",
                Size = 20,
                Format = VolumeContentFormat.SevenZip,
                BodyKind = ArchiveBodyKind.VolumePiece,
                NameSlot = 2,
                SourceLayer = _root,
                IsGatePassed = true
            });

            ledger.AddOrReplace(new FirstStepFileLedgerEntry
            {
                Path = png,
                BaseName = "336",
                Size = 45,
                Format = VolumeContentFormat.Unknown,
                ContentKind = FileContentKind.NormalMedia,
                SourceLayer = _root,
                IsGatePassed = true
            });

            Assert.Equal(2, ledger.SecondStepCandidates("336").Count);

            var pipeline = new VolumeBoundaryPipeline();
            var resolver = new CountingResolver();
            pipeline.Register(resolver);

            IReadOnlyList<PreparedVolumeGroup> groups = await pipeline.ResolveAllAsync(
                ledger,
                baseName => new VolumeBoundaryContext { BaseName = baseName, Ledger = ledger });

            Assert.Empty(groups);
            Assert.Equal(0, resolver.Calls);
        }

        private string Create(string name, int length)
        {
            string path = Path.Combine(_root, name);
            File.WriteAllBytes(path, new byte[length]);
            return path;
        }

        private string Create(string name, byte[] bytes)
        {
            string path = Path.Combine(_root, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private static string CreateIn(string directory, string name, int length)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, name);
            File.WriteAllBytes(path, new byte[length]);
            return path;
        }

        private static string CreateIn(string directory, string name, byte[] bytes)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private static VolumeHandoffEntry FirstPiece(string path, long size, long declaredTotal) =>
            new()
            {
                Path = path,
                BaseName = "336",
                Format = VolumeContentFormat.SevenZip,
                Size = size,
                BodyKind = ArchiveBodyKind.VolumeFirstPiece,
                FirstVolumeFlag = true,
                NameVolumeIndex = 1,
                Membership = VolumeNumberFromContent.VolumeMembershipVerdict.Unknown,
                ContentFail = VolumeNumberFail.None,
                DeclaredTotalBytes = declaredTotal,
                SourceLayer = Path.GetDirectoryName(path) ?? string.Empty,
                Source = VolumeHandoffSource.UserImported
            };

        private VolumeHandoffEntry Piece(string path, long size, int slot) =>
            new()
            {
                Path = path,
                BaseName = "336",
                Format = VolumeContentFormat.SevenZip,
                Size = size,
                BodyKind = ArchiveBodyKind.VolumePiece,
                VolumeNumber = slot,
                NameVolumeIndex = slot,
                Membership = VolumeNumberFromContent.VolumeMembershipVerdict.Unknown,
                ContentFail = VolumeNumberFail.None,
                SourceLayer = Path.GetDirectoryName(path) ?? string.Empty,
                Source = VolumeHandoffSource.UserImported
            };

        /// <summary>PNG：8 字节签名 + IHDR + IEND（与 <see cref="MediaContentFactTests"/> 同一结构）。</summary>
        private static byte[] BuildPng()
        {
            var bytes = new byte[45];

            byte[] signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            signature.CopyTo(bytes, 0);

            int position = 8;
            bytes[position] = 0;
            bytes[position + 1] = 0;
            bytes[position + 2] = 0;
            bytes[position + 3] = 13;
            bytes[position + 4] = (byte)'I';
            bytes[position + 5] = (byte)'H';
            bytes[position + 6] = (byte)'D';
            bytes[position + 7] = (byte)'R';
            position += 12 + 13;

            bytes[position + 4] = (byte)'I';
            bytes[position + 5] = (byte)'E';
            bytes[position + 6] = (byte)'N';
            bytes[position + 7] = (byte)'D';

            return bytes;
        }

        private sealed class CountingResolver : IVolumeBoundaryResolver
        {
            public int Calls { get; private set; }

            public VolumeNamingFamily Family => VolumeNamingFamily.SevenZipNumbered;

            public System.Threading.Tasks.Task<PreparedVolumeGroup?> ResolveAsync(
                VolumeBoundaryContext context,
                CancellationToken cancellationToken = default)
            {
                Calls++;

                return System.Threading.Tasks.Task.FromResult<PreparedVolumeGroup?>(new PreparedVolumeGroup
                {
                    BaseName = context.BaseName,
                    Family = Family,
                    State = PreparedGroupState.Complete,
                    Evidence = "counting"
                });
            }
        }
    }
}
