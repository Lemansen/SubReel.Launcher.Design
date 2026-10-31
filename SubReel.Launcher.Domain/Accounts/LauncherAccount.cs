namespace SubReel.Launcher.Domain.Accounts;

public sealed class LauncherAccount
{
    /// <summary>Подпись в интерфейсе для офлайн-аккаунта (поле <see cref="Kind"/> показывается как есть).</summary>
    public const string OfflineKind = "Офлайн";

    /// <summary>Подпись в интерфейсе для аккаунта Microsoft.</summary>
    public const string MicrosoftKind = "Microsoft";

    private ProfileCosmetics _cosmetics = new();

    public string Name { get; set; } = "";
    public string Kind { get; set; } = OfflineKind;

    /// <summary>Источник аккаунта. У старых настроек без этого поля — офлайн.</summary>
    public AccountProviderType Provider { get; set; } = AccountProviderType.Offline;

    /// <summary>
    /// UUID профиля Minecraft без дефисов (только для Microsoft-аккаунтов). По нему лежат токены в защищённом хранилище.
    /// Сами токены в настройках не хранятся никогда.
    /// </summary>
    public string? ProfileId { get; set; }

    /// <summary>Оформление профиля: цвет ника, рамка кнопки, аватарка. У старых настроек без этого поля — значения по умолчанию.</summary>
    public ProfileCosmetics Cosmetics
    {
        get => _cosmetics;
        set => _cosmetics = value ?? new ProfileCosmetics();
    }
}
