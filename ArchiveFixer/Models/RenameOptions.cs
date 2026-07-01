namespace ArchiveFixer.Models
{
    /// <summary>
    /// 改名选项。
    /// 传给 RenameService 使用。
    /// 
    /// 第十一版要求：
    /// 所有后缀修改必须先生成预览，不能直接改名。
    /// </summary>
    public class RenameOptions
    {
        /// <summary>
        /// 操作类型。
        /// 
        /// AddExtension
        /// ReplaceLastExtension
        /// DeleteLastExtension
        /// DeleteMultipleExtensions
        /// FixByDetectedFormat
        /// </summary>
        public string OperationType { get; set; } = "FixByDetectedFormat";

        /// <summary>
        /// 目标后缀。
        /// 用于添加后缀、替换后缀。
        /// 例如 .7z。
        /// </summary>
        public string TargetExtension { get; set; } = ".7z";

        /// <summary>
        /// 删除后缀数量。
        /// DeleteMultipleExtensions 时使用。
        /// </summary>
        public int DeleteExtensionCount { get; set; } = 1;

        /// <summary>
        /// 冲突处理。
        /// 
        /// Skip
        /// Overwrite
        /// AutoRename
        /// Ask
        /// </summary>
        public string ConflictAction { get; set; } = "AutoRename";

        /// <summary>
        /// 是否改名前预览。
        /// 第十一版默认 true。
        /// </summary>
        public bool PreviewBeforeRename { get; set; } = true;

        /// <summary>
        /// Unknown 格式处理方式。
        /// 
        /// MarkUnknown
        /// Skip
        /// TryExtract
        /// </summary>
        public string UnknownFormatAction { get; set; } = "MarkUnknown";

        /// <summary>
        /// 从 AppSettings 创建智能修正后缀选项。
        /// </summary>
        public static RenameOptions CreateFixByDetectedFormat(AppSettings settings)
        {
            settings ??= AppSettings.CreateDefault();
            settings.Normalize();

            return new RenameOptions
            {
                OperationType = "FixByDetectedFormat",
                TargetExtension = settings.DefaultExtension,
                DeleteExtensionCount = 1,
                ConflictAction = settings.ConflictAction,
                PreviewBeforeRename = settings.PreviewBeforeRename,
                UnknownFormatAction = settings.UnknownFormatAction
            };
        }

        /// <summary>
        /// 创建添加后缀选项。
        /// </summary>
        public static RenameOptions CreateAddExtension(string extension, AppSettings settings)
        {
            settings ??= AppSettings.CreateDefault();
            settings.Normalize();

            return new RenameOptions
            {
                OperationType = "AddExtension",
                TargetExtension = NormalizeExtension(extension, settings.DefaultExtension),
                DeleteExtensionCount = 1,
                ConflictAction = settings.ConflictAction,
                PreviewBeforeRename = settings.PreviewBeforeRename,
                UnknownFormatAction = settings.UnknownFormatAction
            };
        }

        /// <summary>
        /// 创建替换最后一个后缀选项。
        /// </summary>
        public static RenameOptions CreateReplaceLastExtension(string extension, AppSettings settings)
        {
            settings ??= AppSettings.CreateDefault();
            settings.Normalize();

            return new RenameOptions
            {
                OperationType = "ReplaceLastExtension",
                TargetExtension = NormalizeExtension(extension, settings.DefaultExtension),
                DeleteExtensionCount = 1,
                ConflictAction = settings.ConflictAction,
                PreviewBeforeRename = settings.PreviewBeforeRename,
                UnknownFormatAction = settings.UnknownFormatAction
            };
        }

        /// <summary>
        /// 创建删除最后一个后缀选项。
        /// </summary>
        public static RenameOptions CreateDeleteLastExtension(AppSettings settings)
        {
            settings ??= AppSettings.CreateDefault();
            settings.Normalize();

            return new RenameOptions
            {
                OperationType = "DeleteLastExtension",
                TargetExtension = settings.DefaultExtension,
                DeleteExtensionCount = 1,
                ConflictAction = settings.ConflictAction,
                PreviewBeforeRename = settings.PreviewBeforeRename,
                UnknownFormatAction = settings.UnknownFormatAction
            };
        }

        /// <summary>
        /// 创建删除多个后缀选项。
        /// </summary>
        public static RenameOptions CreateDeleteMultipleExtensions(int count, AppSettings settings)
        {
            settings ??= AppSettings.CreateDefault();
            settings.Normalize();

            return new RenameOptions
            {
                OperationType = "DeleteMultipleExtensions",
                TargetExtension = settings.DefaultExtension,
                DeleteExtensionCount = count < 1 ? 1 : count,
                ConflictAction = settings.ConflictAction,
                PreviewBeforeRename = settings.PreviewBeforeRename,
                UnknownFormatAction = settings.UnknownFormatAction
            };
        }

        /// <summary>
        /// 修正非法配置。
        /// </summary>
        public void Normalize()
        {
            if (string.IsNullOrWhiteSpace(OperationType))
            {
                OperationType = "FixByDetectedFormat";
            }

            if (string.IsNullOrWhiteSpace(TargetExtension))
            {
                TargetExtension = ".7z";
            }

            TargetExtension = NormalizeExtension(TargetExtension, ".7z");

            if (DeleteExtensionCount < 1)
            {
                DeleteExtensionCount = 1;
            }

            if (string.IsNullOrWhiteSpace(ConflictAction))
            {
                ConflictAction = "AutoRename";
            }

            if (string.IsNullOrWhiteSpace(UnknownFormatAction))
            {
                UnknownFormatAction = "MarkUnknown";
            }

            PreviewBeforeRename = true;
        }

        private static string NormalizeExtension(string? extension, string fallback)
        {
            string value = string.IsNullOrWhiteSpace(extension)
                ? fallback
                : extension.Trim();

            if (string.IsNullOrWhiteSpace(value))
            {
                value = ".7z";
            }

            if (!value.StartsWith(".", System.StringComparison.Ordinal))
            {
                value = "." + value;
            }

            return value;
        }
    }
}
