using System.Text.Json;
using SubReel.Launcher.Domain.Games;

namespace SubReel.Launcher.Minecraft.Versions;

/// <summary>Разбор version.json в модели Domain. Только разбор, без диска и сети; любой неожиданный формат — <see cref="VersionMetadataException"/>.</summary>
public static class VersionMetadataParser
{
    public const string DefaultLibrariesBaseUrl = "https://libraries.minecraft.net/";

    public static GameVersionMetadata Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ParseRoot(doc.RootElement);
        }
        catch (JsonException ex)
        {
            throw new VersionMetadataException("Описание версии не является корректным JSON.", ex);
        }
    }

    private static GameVersionMetadata ParseRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new VersionMetadataException("Описание версии должно быть JSON-объектом.");

        var id = Str(root, "id");
        if (string.IsNullOrWhiteSpace(id)) throw new VersionMetadataException("В описании версии нет поля id.");

        AssetIndexInfo? assetIndex = null;
        if (Obj(root, "assetIndex") is { } index)
        {
            var indexId = Str(index, "id");
            if (indexId is null) throw new VersionMetadataException($"В assetIndex версии «{id}» нет id.");
            assetIndex = new AssetIndexInfo(indexId, ParseDownload(index, $"assetIndex версии «{id}»"));
        }

        FileDownloadInfo? client = null;
        if (Obj(root, "downloads") is { } downloads && Obj(downloads, "client") is { } clientElement)
            client = ParseDownload(clientElement, $"клиент версии «{id}»");

        LoggingConfigInfo? logging = null;
        if (Obj(root, "logging") is { } loggingRoot && Obj(loggingRoot, "client") is { } loggingClient && Obj(loggingClient, "file") is { } file)
        {
            var fileId = Str(file, "id");
            var argument = Str(loggingClient, "argument");
            if (fileId is not null && argument is not null)
                logging = new LoggingConfigInfo(argument, fileId, ParseDownload(file, $"конфигурация логирования версии «{id}»"));
        }

        var jvm = new List<ArgumentValue>();
        var game = new List<ArgumentValue>();
        if (Obj(root, "arguments") is { } arguments)
        {
            ParseArguments(arguments, "jvm", jvm);
            ParseArguments(arguments, "game", game);
        }

        var libraries = new List<Library>();
        if (root.TryGetProperty("libraries", out var libs) && libs.ValueKind == JsonValueKind.Array)
        {
            foreach (var lib in libs.EnumerateArray()) libraries.Add(ParseLibrary(lib));
        }

        int? javaMajor = null;
        string? javaComponent = null;
        if (Obj(root, "javaVersion") is { } java)
        {
            if (java.TryGetProperty("majorVersion", out var major) && major.ValueKind == JsonValueKind.Number && major.TryGetInt32(out var majorValue))
                javaMajor = majorValue;
            javaComponent = Str(java, "component");
        }

        return new GameVersionMetadata
        {
            Id = id,
            Type = Str(root, "type"),
            InheritsFrom = Str(root, "inheritsFrom"),
            MainClass = Str(root, "mainClass"),
            Assets = Str(root, "assets"),
            AssetIndex = assetIndex,
            ClientDownload = client,
            Libraries = libraries,
            JvmArguments = jvm,
            GameArguments = game,
            LegacyGameArguments = Str(root, "minecraftArguments"),
            Logging = logging,
            JavaMajorVersion = javaMajor,
            JavaComponent = javaComponent,
        };
    }

    private static Library ParseLibrary(JsonElement element)
    {
        var name = Str(element, "name") ?? throw new VersionMetadataException("У библиотеки нет имени (name).");

        LibraryArtifact? artifact = null;
        var classifiers = new Dictionary<string, LibraryArtifact>(StringComparer.Ordinal);

        if (Obj(element, "downloads") is { } downloads)
        {
            if (Obj(downloads, "artifact") is { } artifactElement) artifact = ParseArtifact(artifactElement, name);
            if (Obj(downloads, "classifiers") is { } classifierElement)
            {
                foreach (var property in classifierElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Object) classifiers[property.Name] = ParseArtifact(property.Value, name);
                }
            }
        }
        else
        {
            // Библиотеки загрузчиков (Fabric, Quilt) описаны только именем и адресом Maven-репозитория.
            artifact = DeriveMavenArtifact(element, name);
        }

        var natives = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Obj(element, "natives") is { } nativesElement)
        {
            foreach (var property in nativesElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String) natives[property.Name] = property.Value.GetString()!;
            }
        }

        var exclude = new List<string>();
        if (Obj(element, "extract") is { } extract && extract.TryGetProperty("exclude", out var excludeArray) && excludeArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in excludeArray.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String) exclude.Add(item.GetString()!);
            }
        }

        return new Library(name, artifact, natives, classifiers, ParseRules(element), exclude);
    }

    private static LibraryArtifact? DeriveMavenArtifact(JsonElement library, string name)
    {
        string relative;
        try
        {
            relative = MavenCoordinate.ToRelativePath(name);
        }
        catch (FormatException ex)
        {
            throw new VersionMetadataException(ex.Message, ex);
        }

        var baseUrl = Str(library, "url") ?? DefaultLibrariesBaseUrl;
        if (!baseUrl.EndsWith('/')) baseUrl += "/";
        return new LibraryArtifact(relative, new FileDownloadInfo(baseUrl + relative, Str(library, "sha1"), Long(library, "size")));
    }

    private static LibraryArtifact ParseArtifact(JsonElement element, string libraryName)
    {
        var path = Str(element, "path");
        if (string.IsNullOrWhiteSpace(path)) throw new VersionMetadataException($"У файла библиотеки «{libraryName}» не указан путь.");
        return new LibraryArtifact(path, ParseDownload(element, $"библиотека «{libraryName}»", allowEmptyUrl: true));
    }

    /// <summary>Пустой адрес допустим у библиотек, которые создаёт установщик загрузчика (Forge): планировщик такие не скачивает.</summary>
    private static FileDownloadInfo ParseDownload(JsonElement element, string what, bool allowEmptyUrl = false)
    {
        var url = Str(element, "url");
        if (url is null || (!allowEmptyUrl && url.Length == 0)) throw new VersionMetadataException($"Для «{what}» не указан адрес загрузки.");
        return new FileDownloadInfo(url, Str(element, "sha1"), Long(element, "size"));
    }

    private static void ParseArguments(JsonElement arguments, string name, List<ArgumentValue> target)
    {
        if (!arguments.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                target.Add(new ArgumentValue(new[] { item.GetString()! }, Array.Empty<Rule>()));
            }
            else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("value", out var value))
            {
                var values = new List<string>();
                if (value.ValueKind == JsonValueKind.String) values.Add(value.GetString()!);
                else if (value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var v in value.EnumerateArray())
                    {
                        if (v.ValueKind == JsonValueKind.String) values.Add(v.GetString()!);
                    }
                }
                if (values.Count > 0) target.Add(new ArgumentValue(values, ParseRules(item)));
            }
        }
    }

    private static List<Rule> ParseRules(JsonElement owner)
    {
        var rules = new List<Rule>();
        if (!owner.TryGetProperty("rules", out var array) || array.ValueKind != JsonValueKind.Array) return rules;

        foreach (var ruleElement in array.EnumerateArray())
        {
            if (ruleElement.ValueKind != JsonValueKind.Object) continue;

            var action = Str(ruleElement, "action");
            if (action is not ("allow" or "disallow")) throw new VersionMetadataException($"Неизвестное действие правила: «{action}».");

            string? osName = null, osArch = null, osVersion = null;
            if (Obj(ruleElement, "os") is { } os)
            {
                osName = Str(os, "name");
                osArch = Str(os, "arch");
                osVersion = Str(os, "version");
            }

            Dictionary<string, bool>? features = null;
            if (Obj(ruleElement, "features") is { } featuresElement)
            {
                features = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var property in featuresElement.EnumerateObject())
                {
                    if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) features[property.Name] = property.Value.GetBoolean();
                }
            }

            rules.Add(new Rule(action == "allow", osName, osArch, osVersion, features));
        }
        return rules;
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    private static JsonElement? Obj(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;
}
