using SubReel.Launcher.Domain.Accounts;
using SubReel.Launcher.Domain.Settings;

namespace SubReel.Launcher.Infrastructure.Settings;

/// <summary>
/// Приводит настройки старых форматов к текущему. Миграции только дополняют и исправляют структуру:
/// ни аккаунты, ни оформление, ни выбранные версии они не удаляют (кроме заведомо пустых записей).
/// </summary>
public static class LauncherSettingsMigrator
{
    private const int MaxRecentVersions = 6;

    /// <returns>true — настройки изменены и их нужно сохранить.</returns>
    public static bool Migrate(LauncherSettings settings)
    {
        var changed = false;
        if (settings.SchemaVersion < 1)
        {
            MigrateToV1(settings);
            settings.SchemaVersion = 1;
            changed = true;
        }
        return changed;
    }

    /// <summary>v0 → v1: файл без версии формата. Чиним null-ы и пропуски, которые мог оставить ручной редактор или старый лаунчер.</summary>
    private static void MigrateToV1(LauncherSettings settings)
    {
        settings.RecentVersions ??= new List<string>();
        settings.RecentVersions = settings.RecentVersions
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct()
            .Take(MaxRecentVersions)
            .ToList();

        settings.Accounts ??= new List<LauncherAccount>();
        settings.Accounts.RemoveAll(a => a is null || string.IsNullOrWhiteSpace(a.Name));
        foreach (var account in settings.Accounts)
        {
            if (string.IsNullOrWhiteSpace(account.Kind)) account.Kind = "Офлайн";
        }

        if (settings.Accounts.Count == 0)
            settings.Accounts.Add(new LauncherAccount { Name = "Gamer", Kind = "Офлайн" });

        if (settings.Accounts.All(a => a.Name != settings.ActiveAccount))
            settings.ActiveAccount = settings.Accounts[0].Name;
    }
}
