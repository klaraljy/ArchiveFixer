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
}
