using System.Text.RegularExpressions;

namespace SubReel.Launcher.Domain.Games;

/// <summary>
/// Собирает командную строку игры по <see cref="ResolvedVersion"/>. Порядок как у официального лаунчера:
/// память → аргументы JVM версии (с правилами по ОС и возможностям) → логирование → аргументы пользователя → главный класс → аргументы игры.
/// Чистая логика без ввода-вывода: результат — список аргументов для <c>ProcessStartInfo.ArgumentList</c>, поэтому пробелы
/// в путях не требуют экранирования.
/// </summary>
public static class LaunchCommandBuilder
{
    private static readonly Regex Placeholder = new(@"\$\{([A-Za-z0-9_]+)\}", RegexOptions.Compiled);

    public static LaunchCommand Build(ResolvedVersion version, LaunchParameters parameters, OsEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(environment);

        var hasResolution = parameters.WindowWidth is > 0 && parameters.WindowHeight is > 0;
        var features = new Dictionary<string, bool>(environment.Features ?? new Dictionary<string, bool>())
        {
            ["has_custom_resolution"] = hasResolution,
        };
        var env = environment with { Features = features };

        var separator = environment.OsName == "windows" ? ";" : ":";
        var variables = CreateVariables(version, parameters, separator, hasResolution);
        var unresolved = new SortedSet<string>(StringComparer.Ordinal);
        var arguments = new List<string>();

        if (parameters.MinMemoryMb is > 0) arguments.Add($"-Xms{parameters.MinMemoryMb}M");
        if (parameters.MaxMemoryMb is > 0) arguments.Add($"-Xmx{parameters.MaxMemoryMb}M");

        foreach (var template in JvmTemplates(version, env))
            arguments.Add(Substitute(template, variables, unresolved));

        if (version.Logging is { } logging && !string.IsNullOrWhiteSpace(parameters.LoggingConfigPath))
        {
            var loggingVariables = new Dictionary<string, string>(StringComparer.Ordinal) { ["path"] = parameters.LoggingConfigPath };
            arguments.Add(Substitute(logging.Argument, loggingVariables, unresolved));
        }

        arguments.AddRange(parameters.ExtraJvmArguments);
        arguments.Add(version.MainClass);

        foreach (var template in GameTemplates(version, env))
            arguments.Add(Substitute(template, variables, unresolved));

        return new LaunchCommand(version.MainClass, arguments, unresolved.ToList());
    }

    private static Dictionary<string, string> CreateVariables(ResolvedVersion version, LaunchParameters p, string separator, bool hasResolution)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = p.PlayerName,
            ["auth_uuid"] = p.PlayerUuid,
            ["auth_access_token"] = p.AccessToken,
            ["auth_session"] = $"token:{p.AccessToken}:{p.PlayerUuid}",
            ["user_type"] = p.UserType,
            ["user_properties"] = "{}",
            ["clientid"] = "",
            ["auth_xuid"] = "",
            ["version_name"] = version.Id,
            ["version_type"] = version.Type ?? "release",
            ["game_directory"] = p.GameDirectory,
            ["assets_root"] = p.AssetsRoot,
            ["assets_index_name"] = version.AssetsId,
            ["game_assets"] = p.GameAssetsDirectory,
            ["natives_directory"] = p.NativesDirectory,
            ["library_directory"] = p.LibraryDirectory,
            ["classpath"] = string.Join(separator, p.Classpath),
            ["classpath_separator"] = separator,
            ["launcher_name"] = p.LauncherName,
            ["launcher_version"] = p.LauncherVersion,
        };

        if (hasResolution)
        {
            variables["resolution_width"] = p.WindowWidth!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            variables["resolution_height"] = p.WindowHeight!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return variables;
    }

    /// <summary>
    /// Аргументы JVM версии. У версий до 1.13 их нет вовсе — подставляем стандартные. Если у версии они есть, но без classpath
    /// или пути к нативным библиотекам, недостающее добавляем: без них игра не запустится.
    /// </summary>
    private static List<string> JvmTemplates(ResolvedVersion version, OsEnvironment env)
    {
        var templates = Expand(version.JvmArguments, env).ToList();

        if (templates.Count == 0)
        {
            return new List<string>
            {
                "-Djava.library.path=${natives_directory}",
                "-Dminecraft.launcher.brand=${launcher_name}",
                "-Dminecraft.launcher.version=${launcher_version}",
                "-cp",
                "${classpath}",
            };
        }

        if (!templates.Any(t => t.Contains("java.library.path", StringComparison.Ordinal)))
            templates.Insert(0, "-Djava.library.path=${natives_directory}");

        if (!templates.Any(t => t.Contains("${classpath}", StringComparison.Ordinal)))
        {
            templates.Add("-cp");
            templates.Add("${classpath}");
        }

        return templates;
    }

    /// <summary>Современный формат (arguments.game) главнее старого (minecraftArguments); старая строка делится по пробелам до подстановки.</summary>
    private static List<string> GameTemplates(ResolvedVersion version, OsEnvironment env)
    {
        if (version.GameArguments.Count > 0) return Expand(version.GameArguments, env).ToList();

        return string.IsNullOrWhiteSpace(version.LegacyGameArguments)
            ? new List<string>()
            : version.LegacyGameArguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static IEnumerable<string> Expand(IReadOnlyList<ArgumentValue> arguments, OsEnvironment env) =>
        arguments.Where(a => RuleEvaluator.IsAllowed(a.Rules, env)).SelectMany(a => a.Values);

    /// <summary>
    /// Подставляет ${имя}. Значения не просматриваются повторно, поэтому ${…} внутри ника или пути ничего не раскрывает.
    /// Неизвестное имя остаётся как есть и попадает в <paramref name="unresolved"/>.
    /// </summary>
    private static string Substitute(string template, IReadOnlyDictionary<string, string> variables, ISet<string> unresolved) =>
        Placeholder.Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            if (variables.TryGetValue(name, out var value)) return value;
            unresolved.Add(name);
            return match.Value;
        });
}
