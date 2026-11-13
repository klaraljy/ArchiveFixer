using ArchiveFixer.Models;
using System;

namespace ArchiveFixer.Extraction
{
    /// <summary>失败结论的最后分类；只算结论/文案，不碰工作区。</summary>
    internal static class RecursiveFailureClassifier
    {
        internal static (RecursionStopReason Reason, string? Message, bool ExactReported) ApplyEncryptedHeaders(
            RecursionStopReason reason,
            bool listedAnyCandidate,
            bool sawEncryptedHeadersListFailure,
            bool sawOtherListFailure,
            string layerLabel,
            string encryptedHeadersListMessage,
            Action<RecursionStopReason, string> reportStopReason,
            Action<string, string> log)
        {
            bool conclusion =
                reason == RecursionStopReason.WrongPassword
                && !listedAnyCandidate
                && sawEncryptedHeadersListFailure
                && !sawOtherListFailure;

            if (!conclusion)
            {
                return (reason, null, false);
            }

            reportStopReason(
                RecursionStopReason.EncryptedHeaders,
                $"{layerLabel}：引擎给的是加密头，连清单都列不出来");

            string message = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.RecursionEncryptedHeadersMessageFormat,
                encryptedHeadersListMessage);

            log(
                "WARN",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.RecursionEncryptedHeadersLogFormat,
                    layerLabel,
                    StatusText.EncryptedHeaders));

            return (RecursionStopReason.EncryptedHeaders, message, true);
        }
    }
}
