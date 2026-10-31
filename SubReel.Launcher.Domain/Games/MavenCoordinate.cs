namespace SubReel.Launcher.Domain.Games;

/// <summary>Имя библиотеки вида <c>группа:артефакт:версия[:классификатор][@расширение]</c>.</summary>
public static class MavenCoordinate
{
    /// <summary>Относительный путь файла в репозитории: <c>org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows.jar</c>.</summary>
    public static string ToRelativePath(string name)
    {
        var (group, artifact, version, classifier, extension) = Parse(name);
        var file = $"{artifact}-{version}{(classifier is null ? "" : "-" + classifier)}.{extension}";
        return $"{group.Replace('.', '/')}/{artifact}/{version}/{file}";
    }

    /// <summary>Ключ библиотеки без версии (группа:артефакт[:классификатор]) — по нему версия из потомка заменяет версию из родителя.</summary>
    public static string KeyOf(string name)
    {
        var parts = name.Split('@')[0].Split(':');
        return parts.Length switch
        {
            < 3 => name,
            3 => $"{parts[0]}:{parts[1]}",
            _ => $"{parts[0]}:{parts[1]}:{parts[3]}",
        };
    }

    private static (string Group, string Artifact, string Version, string? Classifier, string Extension) Parse(string name)
    {
        var atIndex = name.IndexOf('@');
        var extension = atIndex >= 0 ? name[(atIndex + 1)..] : "jar";
        var parts = (atIndex >= 0 ? name[..atIndex] : name).Split(':');
        if (parts.Length is < 3 or > 4 || parts.Any(string.IsNullOrWhiteSpace) || string.IsNullOrWhiteSpace(extension))
            throw new FormatException($"Некорректное имя библиотеки «{name}».");

        return (parts[0], parts[1], parts[2], parts.Length == 4 ? parts[3] : null, extension);
    }
}
