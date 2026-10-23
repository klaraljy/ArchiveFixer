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

            foreach (RecursionLayerReport layer in result.Layers)
            {
                if (layer != null && layer.ListingSaidNoEncryption)
                {
                    task.PasswordStatus = StatusText.PasswordNotNeeded;
                    return;
                }
            }
        }
    }
}
