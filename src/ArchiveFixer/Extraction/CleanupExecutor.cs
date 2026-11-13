using System;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Extraction
{
    /// <summary>唯一清理执行顺序入口；具体动作由调用方按既有执行体接线。</summary>
    public static class CleanupExecutor
    {
        public static async Task ExecuteAsync(
            ChainCleanupPlan plan,
            Func<ChainCleanupStep, CancellationToken, Task> execute,
            CancellationToken cancellationToken)
        {
            if (plan == null || execute == null)
            {
                return;
            }

            foreach (ChainCleanupStep step in plan.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await execute(step, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>唯一删除执行入口：计划先成立，执行体只接收同一个计划对象。</summary>
        public static T ExecuteDeletion<T>(DeletionPlan plan, Func<DeletionPlan, T> execute)
        {
            ArgumentNullException.ThrowIfNull(plan);
            ArgumentNullException.ThrowIfNull(execute);

            return execute(plan);
        }
    }
}

