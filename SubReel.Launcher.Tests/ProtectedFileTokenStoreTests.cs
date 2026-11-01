using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SubReel.Launcher.Application;
using SubReel.Launcher.Application.Abstractions;
using SubReel.Launcher.Application.Accounts;
using SubReel.Launcher.Application.Authentication;
using SubReel.Launcher.Domain.Accounts;
using SubReel.Launcher.Infrastructure;
using SubReel.Launcher.Infrastructure.Authentication;
using SubReel.Launcher.Infrastructure.Storage;

namespace SubReel.Launcher.Tests;

/// <summary>Обратимое «шифрование» для тестов (DPAPI есть только в Windows): XOR с ключом плюс метка, чтобы «чужие» данные не расшифровывались.</summary>
internal sealed class FakeSecretProtector : ISecretProtector
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("FAKE");
    private readonly byte _key;

    public FakeSecretProtector(byte key = 0x5A) => _key = key;

    public byte[] Protect(byte[] data) => Magic.Concat(data.Select(b => (byte)(b ^ _key))).ToArray();

    public byte[] Unprotect(byte[] protectedData)
    {
        if (protectedData.Length < Magic.Length || !protectedData.Take(Magic.Length).SequenceEqual(Magic))
            throw new CryptographicException("Данные не расшифровываются.");
        return protectedData.Skip(Magic.Length).Select(b => (byte)(b ^ _key)).ToArray();
    }
}

public sealed class ProtectedFileTokenStoreTests
{
    private const string ProfileId = "069a79f444e94726a5befca90e38aaf5";
    private const string RefreshToken = "refresh-token-SECRET-123";

    private static ProtectedFileTokenStore CreateStore(TempDir dir, ISecretProtector? protector = null) =>
        new(new AppPaths(dir.Root, dir.Root), protector ?? new FakeSecretProtector(), NullLogger<ProtectedFileTokenStore>.Instance);

    private static StoredCredentials Sample() => new()
    {
        RefreshToken = RefreshToken,
        MinecraftAccessToken = "mc-token-SECRET-456",
        MinecraftTokenExpiresAt = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero),
        Name = "Steve",
        SkinUrl = "https://textures.example/skin.png",
    };

    private static string FileFor(TempDir dir, string profileId = ProfileId) => Path.Combine(dir.Root, "launcher", "auth", profileId + ".bin");

    [Fact]
    public void Saved_credentials_are_loaded_back_unchanged()
    {
        using var dir = new TempDir();
        var store = CreateStore(dir);

        store.Save(ProfileId, Sample());
        var loaded = store.Load(ProfileId);

        Assert.NotNull(loaded);
        Assert.Equal(RefreshToken, loaded!.RefreshToken);
        Assert.Equal("mc-token-SECRET-456", loaded.MinecraftAccessToken);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero), loaded.MinecraftTokenExpiresAt);
        Assert.Equal("Steve", loaded.Name);
        Assert.Equal("https://textures.example/skin.png", loaded.SkinUrl);
    }

    [Fact]
    public void File_on_disk_does_not_contain_tokens_in_plain_text()
    {
        using var dir = new TempDir();
        CreateStore(dir).Save(ProfileId, Sample());

        var bytes = File.ReadAllBytes(FileFor(dir));
        var text = Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain(RefreshToken, text);
        Assert.DoesNotContain("mc-token-SECRET-456", text);
        Assert.DoesNotContain("RefreshToken", text);
    }

    [Fact]
    public void Save_overwrites_previous_credentials_and_leaves_no_temp_file()
    {
        using var dir = new TempDir();
        var store = CreateStore(dir);

        store.Save(ProfileId, Sample());
        store.Save(ProfileId, new StoredCredentials { RefreshToken = "second" });

        Assert.Equal("second", store.Load(ProfileId)!.RefreshToken);
        Assert.Empty(Directory.GetFiles(Path.Combine(dir.Root, "launcher", "auth"), "*.tmp"));
    }

    [Fact]
    public void Data_survives_creating_a_new_store_instance_like_after_restart()
    {
        using var dir = new TempDir();
        CreateStore(dir).Save(ProfileId, Sample());

        var restarted = CreateStore(dir);

        Assert.True(restarted.Exists(ProfileId));
        Assert.Equal(RefreshToken, restarted.Load(ProfileId)!.RefreshToken);
    }

    [Fact]
    public void Missing_record_reads_as_no_data()
    {
        using var dir = new TempDir();
        var store = CreateStore(dir);

        Assert.Null(store.Load(ProfileId));
        Assert.False(store.Exists(ProfileId));
    }

    [Fact]
    public void Profile_ids_are_stored_in_separate_files()
    {
        using var dir = new TempDir();
        var store = CreateStore(dir);
        const string other = "11111111111111111111111111111111";

        store.Save(ProfileId, Sample());
        store.Save(other, new StoredCredentials { RefreshToken = "other-refresh" });

        Assert.Equal(RefreshToken, store.Load(ProfileId)!.RefreshToken);
        Assert.Equal("other-refresh", store.Load(other)!.RefreshToken);
    }

    [Fact]
    public void Delete_removes_record_and_missing_record_is_ignored()
    {
        using var dir = new TempDir();
        var store = CreateStore(dir);
        store.Save(ProfileId, Sample());

        store.Delete(ProfileId);
        store.Delete(ProfileId); // повторное удаление ничего не ломает

        Assert.False(store.Exists(ProfileId));
        Assert.Null(store.Load(ProfileId));
        Assert.False(File.Exists(FileFor(dir)));
    }

    [Fact]
    public void Corrupted_file_reads_as_no_data()
    {
        using var dir = new TempDir();
        var store = CreateStore(dir);
        store.Save(ProfileId, Sample());
        File.WriteAllBytes(FileFor(dir), new byte[] { 1, 2, 3, 4, 5 });

        Assert.Null(store.Load(ProfileId));
    }

    [Fact]
    public void Truncated_file_reads_as_no_data()
    {
        using var dir = new TempDir();
        var store = CreateStore(dir);
        store.Save(ProfileId, Sample());
        var bytes = File.ReadAllBytes(FileFor(dir));
        File.WriteAllBytes(FileFor(dir), bytes.Take(bytes.Length / 2).ToArray());

        Assert.Null(store.Load(ProfileId));
    }

    [Fact]
    public void Empty_file_reads_as_no_data()
    {
        using var dir = new TempDir();
        var store = CreateStore(dir);
        Directory.CreateDirectory(Path.Combine(dir.Root, "launcher", "auth"));
        File.WriteAllBytes(FileFor(dir), Array.Empty<byte>());

        Assert.Null(store.Load(ProfileId));
    }

    [Fact]
    public void Data_protected_by_another_user_reads_as_no_data()
    {
        using var dir = new TempDir();
        // Файл записан «другим пользователем» (другой ключ): метка верна, но содержимое не является корректным JSON.
        CreateStore(dir, new FakeSecretProtector(key: 0x11)).Save(ProfileId, Sample());

        Assert.Null(CreateStore(dir, new FakeSecretProtector(key: 0x22)).Load(ProfileId));
    }

    [Fact]
    public void Record_without_refresh_token_reads_as_no_data()
    {
        using var dir = new TempDir();
        var store = CreateStore(dir);
        store.Save(ProfileId, new StoredCredentials { RefreshToken = "" });

        Assert.Null(store.Load(ProfileId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..\\..\\evil")]
    [InlineData("../evil")]
    [InlineData("not-a-uuid")]
    [InlineData("069a79f4-44e9-4726-a5be-fca90e38aaf5")] // с дефисами: хранится UUID без дефисов
    public void Invalid_profile_ids_never_touch_the_file_system(string profileId)
    {
        using var dir = new TempDir();
        var store = CreateStore(dir);

        Assert.Throws<ArgumentException>(() => store.Save(profileId, Sample()));
        Assert.Null(store.Load(profileId));
        Assert.False(store.Exists(profileId));
        store.Delete(profileId);
        Assert.False(Directory.Exists(Path.Combine(dir.Root, "launcher", "auth")));
    }

    [Fact]
    public void Profile_id_case_does_not_create_duplicate_records()
    {
        using var dir = new TempDir();
        var store = CreateStore(dir);

        store.Save(ProfileId.ToUpperInvariant(), Sample());

        Assert.True(store.Exists(ProfileId));
        Assert.Equal(RefreshToken, store.Load(ProfileId)!.RefreshToken);
    }

    [Fact]
    public void Container_resolves_token_store_when_protector_is_provided()
    {
        using var dir = new TempDir();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISecretProtector>(new FakeSecretProtector());
        services.AddSubReelInfrastructure(new AppPaths(dir.Root, dir.Root));
        using var provider = services.BuildServiceProvider();

        Assert.IsType<ProtectedFileTokenStore>(provider.GetRequiredService<ITokenStore>());
    }

    [Fact]
    public async Task Sign_in_survives_restart_and_removing_the_account_deletes_the_file()
    {
        using var dir = new TempDir();
        var settings = new FakeSettingsService();
        var accounts = new AccountService(settings);
        var time = new AuthTestClock();
        var store = CreateStore(dir);
        var provider = new FakeAccountProvider(store, time);

        using (var first = new SessionManager(accounts, new IAccountProvider[] { provider }, store, time, NullLogger<SessionManager>.Instance))
        {
            var account = await first.SignInAsync(AccountProviderType.Microsoft);
            Assert.True(File.Exists(FileFor(dir, account.ProfileId!)));
        }

        // «Перезапуск»: новое хранилище и менеджер видят тот же файл; сессия поднимается без повторного входа.
        var restartedStore = CreateStore(dir);
        using var second = new SessionManager(accounts, new IAccountProvider[] { provider }, restartedStore, time, NullLogger<SessionManager>.Instance);
        var microsoft = accounts.Accounts.Single(a => a.Provider == AccountProviderType.Microsoft);
        Assert.Equal(AccountAuthState.Restorable, second.GetState(microsoft));
        var session = await second.GetSessionAsync(microsoft);
        Assert.Equal(GameSession.MicrosoftUserType, session.UserType);

        accounts.Activate(accounts.Accounts.First(a => a.Provider == AccountProviderType.Offline).Name);
        Assert.True(accounts.Remove(microsoft));
        Assert.False(File.Exists(FileFor(dir, microsoft.ProfileId!)));
    }
}
