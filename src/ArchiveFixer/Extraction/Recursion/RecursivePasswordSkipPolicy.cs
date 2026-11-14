using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using System;
using System.Collections.Generic;

namespace ArchiveFixer.Extraction;

/// <summary>空密码跳过的唯一判据；不记账、不写日志，由调用方处理。</summary>
internal static class RecursivePasswordSkipPolicy
{
    internal static EmptyPasswordSkip Match(
        string? candidate,
        int usableCandidates,
        ArchiveListResult? listed)
    {
        if (!string.IsNullOrEmpty(candidate) || usableCandidates <= 0)
        {
            return EmptyPasswordSkip.None;
        }

        if (PasswordProbe.ShouldSkipEmptyPassword(listed))
        {
            return EmptyPasswordSkip.EncryptedEntry;
        }

        if (listed is { Success: false }
            && string.Equals(
                listed.ErrorType,
                EngineErrorTypes.EncryptedHeaders,
                StringComparison.OrdinalIgnoreCase))
        {
            return EmptyPasswordSkip.EncryptedHeaders;
        }

        return EmptyPasswordSkip.None;
    }
}
