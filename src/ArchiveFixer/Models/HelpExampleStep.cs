namespace ArchiveFixer.Models
{
    /// <summary>
    /// 说明页的一步（编号、标题、正文、可选路径、与下一步之间的连接符）。
    /// 独立字段是为了让 XAML 排成“编号卡片 → 说明 → 箭头”，而不是把整段字塞进一个 TextBlock。
    /// </summary>
    public sealed class HelpExampleStep
    {
        public HelpExampleStep(
            int number,
            string title,
            string text,
            string path = "",
            string connector = "")
        {
            Number = number;
            Title = title;
            Text = text;
            Path = path;
            Connector = connector;
        }

        public int Number { get; }

        public string Title { get; }

        public string Text { get; }

        public string Path { get; }

        /// <summary>下一步连接符；最后一步为空。</summary>
        public string Connector { get; }
    }
}
