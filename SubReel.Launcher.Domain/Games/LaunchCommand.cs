namespace SubReel.Launcher.Domain.Games;

/// <summary>Всё, что нужно подставить в аргументы запуска. Пути полные; значения приходят от лаунчера, а не из сети.</summary>
public sealed record LaunchParameters
{
    public required string PlayerName { get; init; }

    /// <summary>UUID профиля без дефисов.</summary>
    public required string PlayerUuid { get; init; }

    /// <summary>Только в памяти. У офлайн-сессии — «0».</summary>
    public required string AccessToken { get; init; }

    /// <summary>msa или legacy.</summary>
    public required string UserType { get; init; }

    public required string GameDirectory { get; init; }

    /// <summary>Корень ассетов (<c>assets</c>), в нём лежат indexes и objects.</summary>
    public required string AssetsRoot { get; init; }

    /// <summary>Папка ассетов для старых версий (<c>${game_assets}</c>): копия под исходными именами.</summary>
    public required string GameAssetsDirectory { get; init; }

    public required string NativesDirectory { get; init; }

    public required string LibraryDirectory { get; init; }

    /// <summary>Файлы classpath по порядку: библиотеки, затем клиентский jar.</summary>
    public required IReadOnlyList<string> Classpath { get; init; }

    /// <summary>Файл конфигурации логирования; null — аргумент логирования не добавляется.</summary>
    public string? LoggingConfigPath { get; init; }

    public int? MinMemoryMb { get; init; }
    public int? MaxMemoryMb { get; init; }

    /// <summary>Аргументы пользователя: идут после стандартных и подставляются как есть (без обработки ${…}).</summary>
    public IReadOnlyList<string> ExtraJvmArguments { get; init; } = Array.Empty<string>();

    public int? WindowWidth { get; init; }
    public int? WindowHeight { get; init; }

    public string LauncherName { get; init; } = "SubReel";
    public string LauncherVersion { get; init; } = "0.1";
}

/// <summary>Готовая команда: всё после пути к java (аргументы JVM, главный класс, аргументы игры).</summary>
/// <param name="UnresolvedPlaceholders">Имена ${…}, для которых нет значения: они остались в аргументах как есть.</param>
public sealed record LaunchCommand(string MainClass, IReadOnlyList<string> Arguments, IReadOnlyList<string> UnresolvedPlaceholders)
{
    private const int MinSecretLength = 8;

    /// <summary>
    /// Строка для журнала. Токен заменяется на «&lt;скрыт&gt;»; слишком короткие значения (офлайн-токен «0») не скрываются,
    /// иначе замена испортила бы всю строку.
    /// </summary>
    public string ToDisplayString(string? secretToRedact = null)
    {
        var redact = !string.IsNullOrEmpty(secretToRedact) && secretToRedact.Length >= MinSecretLength;
        return string.Join(' ', Arguments.Select(argument =>
        {
            var text = redact ? argument.Replace(secretToRedact!, "<скрыт>", StringComparison.Ordinal) : argument;
            return Quote(text);
        }));
    }

    private static string Quote(string argument) =>
        argument.Length == 0 || argument.Any(char.IsWhiteSpace) || argument.Contains('"')
            ? "\"" + argument.Replace("\"", "\\\"") + "\""
            : argument;
}
