using System;

namespace ArchiveFixer.Extraction
{
    internal enum EmptyPasswordSkip
    {
        None,
        EncryptedEntry,
        EncryptedHeaders
    }
}