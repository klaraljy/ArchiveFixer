using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ArchiveFixer.Storage
{    /// <summary>安全检查的结论。</summary>
    public sealed class DeleteSafetyDecision
    {
        public bool IsAllowed { get; init; }

        public DeleteBlockReason Reason { get; init; }

        /// <summary>中文说明，可直接给用户看 / 进日志。</summary>
        public string Message { get; init; } = string.Empty;

        public static DeleteSafetyDecision Allow(string message = "安全检查通过")
        {
            return new DeleteSafetyDecision
            {
                IsAllowed = true,
                Reason = DeleteBlockReason.None,
                Message = message
            };
        }

        public static DeleteSafetyDecision Block(DeleteBlockReason reason, string message)
        {
            return new DeleteSafetyDecision
            {
                IsAllowed = false,
                Reason = reason,
                Message = message
            };
        }
    }

}
