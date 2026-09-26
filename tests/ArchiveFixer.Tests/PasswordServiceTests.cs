using System.IO;
using System.Linq;
using ArchiveFixer.Models;
using ArchiveFixer.Services;

namespace ArchiveFixer.Tests;

public class PasswordServiceTests
{
    [Fact]
    public void GetPasswordCandidates_Order_EmptyThenTaskThenGlobalThenList()
    {
        var service = new PasswordService();
        service.AddPassword("list1");
        service.AddPassword("list2");

        var task = new ArchiveTask("C:\\x.7z")
        {
            Password = "taskpw"
        };

        var candidates = service.GetPasswordCandidates(task, "globalpw", service.Passwords, tryEmptyFirst: true);

        Assert.Equal(
            new[] { "", "taskpw", "globalpw", "list1", "list2" },
            candidates.Select(c => c.Value));
    }

    [Fact]
    public void AddPassword_Duplicate_ReturnsFalseAndKeepsSingleEntry()
    {
        var service = new PasswordService();

        bool first = service.AddPassword("abc");
        bool second = service.AddPassword("abc");

        Assert.True(first);
        Assert.False(second);
        Assert.Single(service.Passwords);
        Assert.Equal("abc", service.Passwords[0].Value);
    }

    /// <summary>
    /// **列表式密码自带冒号**（资源站密码十有八九长这样：<c>abc:123</c>、<c>www.xxx.com:8888</c>）：
    /// 候选链必须两种理解都试 —— 冒号右侧**和整行**。
    ///
    /// <para>修复前（2026-09-25 第 30 条）：含冒号的行一律按"名称:密码"切，只有右侧进候选，
    /// **正确的整行从来没被试过** —— 用户看到的就是"密码本第一条就是这个包的密码，却一直密码错误"。</para>
    ///
    /// <para>顺序也要钉住：整行排在显式密码之后（正常的几百行映射式密码本只是尾部多些噪音候选，
    /// 不会把真正要试的那几条挤出单层尝试上限）。</para>
    /// </summary>
    [Fact]
    public void GetPasswordCandidates_列表式密码带冒号_整行也要进候选且排在列表之后()
    {
        string file = Path.Combine(Path.GetTempPath(), "af_pw_colon_" + Guid.NewGuid().ToString("N") + ".txt");

        try
        {
            File.WriteAllLines(file, new[] { "abc:123", "plain" });

            var service = new PasswordService();
            service.ImportPasswordList(file);

            var task = new ArchiveTask("C:\\x.zip");
            List<PasswordItem> candidates = service.GetPasswordCandidates(
                task,
                string.Empty,
                service.Passwords,
                tryEmptyFirst: false);

            Assert.Equal(new[] { "123", "plain", "abc:123" }, candidates.Select(c => c.Value));

            PasswordItem rawLine = Assert.Single(candidates, c => c.Source == "BookRawLine");
            Assert.Equal("abc:123", rawLine.Value);
            Assert.DoesNotContain("abc:123", rawLine.Remark ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// 正常的**映射式**密码本不能被这次改动打乱：命中的那条仍然是第一优先（<c>BookMapped</c>），
    /// 整行只是排在后面的噪音候选（真实场景里它永远试不到，因为前面已经成功）。
    /// </summary>
    [Fact]
    public void GetPasswordCandidates_映射式命中仍然排第一_整行排在后面()
    {
        string file = Path.Combine(Path.GetTempPath(), "af_pw_map_" + Guid.NewGuid().ToString("N") + ".txt");

        try
        {
            File.WriteAllLines(file, new[] { "资源A:passA" });

            var service = new PasswordService();
            service.ImportPasswordList(file);

            var task = new ArchiveTask("C:\\资源A.zip");
            List<PasswordItem> candidates = service.GetPasswordCandidates(
                task,
                string.Empty,
                service.Passwords,
                tryEmptyFirst: false);

            Assert.Equal("passA", candidates[0].Value);
            Assert.Equal("BookMapped", candidates[0].Source);
            Assert.Contains(candidates, c => c.Source == "BookRawLine" && c.Value == "资源A:passA");
            Assert.True(
                candidates.FindIndex(c => c.Source == "BookMapped") <
                candidates.FindIndex(c => c.Source == "BookRawLine"),
                "映射式命中必须排在整行那一档之前");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ImportPasswordList_DeduplicatesKeepingFirstOrder()
    {
        string file = Path.Combine(Path.GetTempPath(), "af_pw_" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllLines(file, new[] { "A", "B", "A" });

            var service = new PasswordService();
            service.ImportPasswordList(file);

            Assert.Equal(new[] { "A", "B" }, service.Passwords.Select(p => p.Value));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void RemovePassword_Existing_ReturnsTrue()
    {
        var service = new PasswordService();
        service.AddPassword("abc");

        bool removed = service.RemovePassword(service.Passwords[0]);

        Assert.True(removed);
        Assert.Empty(service.Passwords);
    }

    [Fact]
    public void RemovePassword_Missing_ReturnsFalse()
    {
        var service = new PasswordService();

        bool removed = service.RemovePassword(new PasswordItem { Value = "x" });

        Assert.False(removed);
    }

    [Fact]
    public void MoveUp_MovesItemAndReturnsTrue()
    {
        var service = new PasswordService();
        service.AddPassword("first");
        service.AddPassword("second");

        bool moved = service.MoveUp(service.Passwords[1]);

        Assert.True(moved);
        Assert.Equal("second", service.Passwords[0].Value);
    }

    [Fact]
    public void MoveUp_FirstItem_ReturnsFalse()
    {
        var service = new PasswordService();
        service.AddPassword("first");

        bool moved = service.MoveUp(service.Passwords[0]);

        Assert.False(moved);
    }

    /// <summary>
    /// 日志文案要说清密码**是哪来的** —— 只对本次运行有效的手动密码（一键处理/批量开始前问的那一次）
    /// 以前走 default 分支，日志里写成"密码候选第 N 项"，事后完全看不出这个密码是用户当场给的。
    /// 同时钉住：任何分支都只出脱敏占位符，绝不出明文（AGENTS.md §6 第 5 条）。
    /// </summary>
    [Fact]
    public void BuildTryPasswordLogText_ManualBatch_SaysWhereItCameFromAndStaysMasked()
    {
        var service = new PasswordService();

        var manual = new PasswordItem
        {
            Value = "<示例密码>",
            Source = "ManualBatch",
            IsEnabled = true
        };

        string text = service.BuildTryPasswordLogText(manual, 2);

        Assert.Equal("尝试本次运行手动输入的密码：******", text);
        Assert.DoesNotContain(manual.Value, text, System.StringComparison.Ordinal);

        // 别的来源不动（这条只补 ManualBatch 那一个分支）。
        Assert.Equal("尝试空密码", service.BuildTryPasswordLogText(
            new PasswordItem { Value = string.Empty, Source = "Empty" }, 1));
        Assert.Equal("尝试密码候选第 3 项：******", service.BuildTryPasswordLogText(
            new PasswordItem { Value = "x", Source = "别的来源" }, 3));
    }
}
