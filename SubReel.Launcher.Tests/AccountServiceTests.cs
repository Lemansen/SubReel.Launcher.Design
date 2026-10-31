using SubReel.Launcher.Application.Accounts;
using SubReel.Launcher.Domain.Accounts;

namespace SubReel.Launcher.Tests;

public sealed class AccountServiceTests
{
    private static (AccountService Service, FakeSettingsService Settings) Create()
    {
        var settings = new FakeSettingsService();
        return (new AccountService(settings), settings);
    }

    [Theory]
    [InlineData("Steve", true)]
    [InlineData("a_b", true)]
    [InlineData("ab", false)]
    [InlineData("seventeen_chars_x", false)]
    [InlineData("bad name", false)]
    [InlineData("ник", false)]
    public void Nickname_rules(string name, bool expected) =>
        Assert.Equal(expected, AccountService.IsValidNickname(name));

    [Fact]
    public void Empty_account_list_gets_default_account()
    {
        var settings = new FakeSettingsService();
        settings.Current.Accounts.Clear();
        _ = new AccountService(settings);

        Assert.Single(settings.Current.Accounts);
        Assert.Equal(settings.Current.Accounts[0].Name, settings.Current.ActiveAccount);
    }

    [Fact]
    public void Unknown_active_account_falls_back_to_first()
    {
        var settings = new FakeSettingsService();
        settings.Current.ActiveAccount = "ghost";
        _ = new AccountService(settings);

        Assert.Equal(settings.Current.Accounts[0].Name, settings.Current.ActiveAccount);
    }

    [Fact]
    public void Add_valid_name_creates_and_activates_and_saves()
    {
        var (service, settings) = Create();

        Assert.True(service.AddAndActivate("  Alex_1 "));

        Assert.Equal("Alex_1", settings.Current.ActiveAccount);
        Assert.Contains(settings.Current.Accounts, a => a.Name == "Alex_1");
        Assert.True(settings.SaveCount > 0);
    }

    [Fact]
    public void Add_invalid_name_changes_nothing()
    {
        var (service, settings) = Create();

        Assert.False(service.AddAndActivate("x"));

        Assert.Single(settings.Current.Accounts);
        Assert.Equal(0, settings.SaveCount);
    }

    [Fact]
    public void Add_existing_name_does_not_duplicate()
    {
        var (service, settings) = Create();
        service.AddAndActivate("Alex_1");
        service.AddAndActivate("Alex_1");

        Assert.Equal(1, settings.Current.Accounts.Count(a => a.Name == "Alex_1"));
    }

    [Fact]
    public void Active_and_only_account_cannot_be_removed()
    {
        var (service, settings) = Create();
        Assert.False(service.Remove(service.Active));
        Assert.Single(settings.Current.Accounts);

        service.AddAndActivate("Alex_1");
        Assert.False(service.Remove(service.Active));
        Assert.Equal(2, settings.Current.Accounts.Count);
    }

    [Fact]
    public void Inactive_account_can_be_removed()
    {
        var (service, settings) = Create();
        service.AddAndActivate("Alex_1");
        var other = settings.Current.Accounts.First(a => a.Name != "Alex_1");

        Assert.True(service.Remove(other));
        Assert.DoesNotContain(other, settings.Current.Accounts);
    }

    [Fact]
    public void Rename_active_updates_name_and_active_pointer()
    {
        var (service, settings) = Create();

        Assert.True(service.RenameActive("Notch_2"));

        Assert.Equal("Notch_2", settings.Current.ActiveAccount);
        Assert.Equal("Notch_2", service.Active.Name);
    }

    [Fact]
    public void Rename_to_name_of_other_account_is_rejected()
    {
        var (service, settings) = Create();
        service.AddAndActivate("Alex_1");
        var otherName = settings.Current.Accounts.First(a => a.Name != "Alex_1").Name;

        Assert.False(service.RenameActive(otherName));
        Assert.Equal("Alex_1", service.Active.Name);
    }

    [Fact]
    public void Rename_to_own_name_is_allowed()
    {
        var (service, _) = Create();
        Assert.True(service.RenameActive(service.Active.Name));
    }
}
