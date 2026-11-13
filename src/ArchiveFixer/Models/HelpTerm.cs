namespace ArchiveFixer.Models
{
    /// <summary>说明页里的一条术语（名称 + 解释）。</summary>
    public sealed class HelpTerm
    {
        public HelpTerm(string term, string explanation)
        {
            Term = term;
            Explanation = explanation;
        }

        public string Term { get; }

        public string Explanation { get; }
    }
}
