using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Engines;

namespace ArchiveFixer.Extraction
{
    public readonly record struct EncryptedHeaderManifestRecoveryResult(
        LayerManifest Manifest,
        IReadOnlyList<(string Path, long Size)> Entries);

    /// <summary>
    /// 加密头（-mhe）的清单恢复：换引擎兜底成功时主引擎之前列不出目录，
    /// 但命中密码已经知道了，必须拿这个密码再列一次；列出来才允许后续完整性核对。
    /// 列不出来仍返回 Unavailable，⛔ 不伪造清单。
    /// </summary>
    public static class EncryptedHeaderManifestRecovery
    {
        public static async Task<EncryptedHeaderManifestRecoveryResult> TryRelistAsync(
            IArchiveEngine? engine,
            string? archivePath,
            string? password,
            CancellationToken cancellationToken = default,
            Action<string>? log = null)
        {
            if (engine == null
                || string.IsNullOrWhiteSpace(archivePath)
                || string.IsNullOrEmpty(password))
            {
                return new EncryptedHeaderManifestRecoveryResult(
                    LayerManifest.Unavailable("没有可用密码，无法重新取加密头清单"),
                    Array.Empty<(string, long)>());
            }

            try
            {
                ArchiveListResult listed = await engine
                    .ListAsync(ArchiveRequest.For(archivePath!, password!), cancellationToken)
                    .ConfigureAwait(false);

                if (listed.Success)
                {
                    log?.Invoke($"用命中的密码重新列目录成功（{listed.Entries.Count} 个条目）");
                    return new EncryptedHeaderManifestRecoveryResult(
                        LayerManifest.From(listed),
                        PartialPublishPlanner.ToManifestEntries(listed));
                }
            }
            catch
            {
                // 列不出来仍然按“判不出”处理。
            }

            return new EncryptedHeaderManifestRecoveryResult(
                LayerManifest.Unavailable("用命中密码重新列目录仍然失败（加密头/引擎不支持）"),
                Array.Empty<(string, long)>());
        }
    }
}
