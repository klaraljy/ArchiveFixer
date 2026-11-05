using System.Linq;
using ArchiveFixer.Engines;
using ArchiveFixer.Models;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 「清单里一个加密条目都没有 ⇒ 结论与密码无关」的唯一出口（从 `ExtractionCoordinator` 搬出的第 6 个功能族，2026-10-23）。
    ///
    /// <para>判据只读结构化事实 <see cref="RecursionLayerReport.ListingSaidNoEncryption"/>（某一层的清单成功且
    /// <c>IsEncrypted=false</c>）⇒ 任务写「不需要密码」；⛔ 不按名字/后缀/引擎话猜。</para>
    /// </summary>
    public static class RecursionPasswordStatus
    {
        public static void Apply(ArchiveTask? task, RecursionResult? result)
        {
            if (task == null || result?.Layers == null)
            {
                return;
            }

            /*
             * 递归路每层的结果必须折成同一个终态，不能只写“不需要密码”：
             * - 任何一层用非空密码成功 ⇒ 密码正确；
             * - 任何一层明确密码类失败 ⇒ 密码错误；
             * - 任何一层明确需要密码/加密头 ⇒ 需要密码；
             * - 只看到“清单无加密”或空密码成功 ⇒ 不需要密码。
             * 优先级按“已经证明成功”优先于失败证据，避免内层成功后被外层噪声覆盖。
             */
            bool sawCorrect = result.Layers.Any(layer =>
                layer != null && string.Equals(layer.UsedPasswordMasked, "******", System.StringComparison.Ordinal));
            if (sawCorrect)
            {
                task.PasswordStatus = StatusText.PasswordCorrect;
                return;
            }

            bool sawWrong = result.Layers.Any(layer =>
                layer != null && (layer.DetectedErrorType == EngineErrorTypes.WrongPassword
                                  || layer.DetectedErrorType == EngineErrorTypes.PasswordOrCorrupted));
            if (sawWrong)
            {
                task.PasswordStatus = StatusText.WrongPassword;
                return;
            }

            bool sawNeed = result.Layers.Any(layer =>
                layer != null && (layer.DetectedErrorType == EngineErrorTypes.NeedPassword
                                  || layer.DetectedErrorType == EngineErrorTypes.EncryptedHeaders));
            if (sawNeed)
            {
                task.PasswordStatus = StatusText.PasswordNeed;
                return;
            }

            if (result.Layers.Any(layer =>
                    layer != null
                    && (layer.ListingSaidNoEncryption
                        || string.Equals(layer.UsedPasswordMasked, "空密码", System.StringComparison.Ordinal))))
            {
                task.PasswordStatus = StatusText.PasswordNotNeeded;
            }
        }
    }
}
