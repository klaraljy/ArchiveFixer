using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 小功能组合的 pairwise 用例生成器。它不是把业务结果硬编码进用例表，
    /// 而是自动保证：每个参数的每个取值都出现，且任意两个参数的任意取值组合至少出现一次。
    /// 具体业务断言仍由特定解压/源包/其余物/空间不足各自的测试负责。
    /// </summary>
    public sealed class PairwiseMatrixTests
    {
        private static readonly Dictionary<string, string[]> Dimensions = new()
        {
            ["模式"] = new[] { "普通一键", "特定解压" },
            ["源包"] = new[] { "留下", "彻底删除" },
            ["其余物"] = new[] { "留下", "彻底删除" },
            ["空间不足"] = new[] { "关", "开" },
            ["嵌套"] = new[] { "单层", "多层" },
            ["分卷"] = new[] { "无", "标准分卷" },
            ["落点"] = new[] { "包名", "指定位置" },
            ["密码"] = new[] { "无", "已知", "错误" }
        };

        [Fact]
        public void Pairwise组合矩阵_覆盖所有二元组且明显少于全排列()
        {
            List<Dictionary<string, string>> cases = BuildPairwise(Dimensions);

            int fullProduct = Dimensions.Values.Aggregate(1, (current, values) => current * values.Length);
            Assert.True(cases.Count < fullProduct, $"pairwise 没有减少用例：{cases.Count} >= {fullProduct}");

            foreach (string dimension in Dimensions.Keys)
            {
                foreach (string value in Dimensions[dimension])
                {
                    Assert.Contains(cases, item => item[dimension] == value);
                }
            }

            string[] names = Dimensions.Keys.ToArray();
            for (int i = 0; i < names.Length; i++)
            {
                for (int j = i + 1; j < names.Length; j++)
                {
                    foreach (string left in Dimensions[names[i]])
                    {
                        foreach (string right in Dimensions[names[j]])
                        {
                            Assert.Contains(cases, item =>
                                item[names[i]] == left && item[names[j]] == right);
                        }
                    }
                }
            }

            Assert.Contains(cases, item => item["模式"] == "特定解压" && item["源包"] == "彻底删除");
            Assert.Contains(cases, item => item["空间不足"] == "开" && item["源包"] == "彻底删除");
            Assert.Contains(cases, item => item["分卷"] == "标准分卷" && item["嵌套"] == "多层");
        }

        private static List<Dictionary<string, string>> BuildPairwise(
            IReadOnlyDictionary<string, string[]> dimensions)
        {
            string[] names = dimensions.Keys.ToArray();
            var requirements = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < names.Length; i++)
            {
                for (int j = i + 1; j < names.Length; j++)
                {
                    foreach (string left in dimensions[names[i]])
                    {
                        foreach (string right in dimensions[names[j]])
                        {
                            requirements.Add(Requirement(names[i], left, names[j], right));
                        }
                    }
                }
            }

            var cases = new List<Dictionary<string, string>>();
            while (requirements.Count > 0)
            {
                var seed = requirements.First();
                var (seedLeft, seedLeftValue, seedRight, seedRightValue) = Parse(seed);
                var item = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [seedLeft] = seedLeftValue,
                    [seedRight] = seedRightValue
                };

                foreach (string name in names)
                {
                    if (item.ContainsKey(name))
                    {
                        continue;
                    }

                    string bestValue = dimensions[name][0];
                    int bestCover = -1;

                    foreach (string value in dimensions[name])
                    {
                        int cover = requirements.Count(requirement =>
                            IsCoveredByValue(requirement, name, value, item));
                        if (cover > bestCover)
                        {
                            bestCover = cover;
                            bestValue = value;
                        }
                    }

                    item[name] = bestValue;
                }

                foreach (string requirement in requirements.ToArray())
                {
                    if (IsCovered(requirement, item))
                    {
                        requirements.Remove(requirement);
                    }
                }

                cases.Add(item);
            }

            return cases;
        }

        private static bool IsCoveredByValue(
            string requirement,
            string dimension,
            string value,
            IReadOnlyDictionary<string, string> assigned)
        {
            var probe = new Dictionary<string, string>(assigned, StringComparer.Ordinal)
            {
                [dimension] = value
            };
            return IsCovered(requirement, probe);
        }

        private static bool IsCovered(string requirement, IReadOnlyDictionary<string, string> item)
        {
            var (left, leftValue, right, rightValue) = Parse(requirement);
            return item.TryGetValue(left, out string? actualLeft)
                   && item.TryGetValue(right, out string? actualRight)
                   && actualLeft == leftValue
                   && actualRight == rightValue;
        }

        private static string Requirement(string left, string leftValue, string right, string rightValue) =>
            left + "\u001f" + leftValue + "\u001f" + right + "\u001f" + rightValue;

        private static (string Left, string LeftValue, string Right, string RightValue) Parse(string requirement)
        {
            string[] parts = requirement.Split('\u001f');
            return (parts[0], parts[1], parts[2], parts[3]);
        }
    }
}
