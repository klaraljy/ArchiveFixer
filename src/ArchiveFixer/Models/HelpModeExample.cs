using System.Collections.Generic;

namespace ArchiveFixer.Models
{
    /// <summary>
    /// 一种处理方式的完整图文例子。普通档位、特定解压、空间不足各一份；
    /// 例子只写用户能看见的事实，不写内部实现名。
    /// </summary>
    public sealed class HelpModeExample
    {
        public HelpModeExample(
            string key,
            string title,
            string badge,
            string summary,
            IReadOnlyList<HelpExampleStep> steps,
            string outputTree = "",
            string boundary = "",
            IReadOnlyList<string>? notes = null)
        {
            Key = key;
            Title = title;
            Badge = badge;
            Summary = summary;
            Steps = steps;
            OutputTree = outputTree;
            Boundary = boundary;
            Notes = notes ?? new List<string>();
        }

        public string Key { get; }

        public string Title { get; }

        public string Badge { get; }

        public string Summary { get; }

        public IReadOnlyList<HelpExampleStep> Steps { get; }

        public string OutputTree { get; }

        public string Boundary { get; }

        public IReadOnlyList<string> Notes { get; }
    }
}
