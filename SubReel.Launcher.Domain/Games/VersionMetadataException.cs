namespace SubReel.Launcher.Domain.Games;

/// <summary>Описание версии (version.json, индекс ассетов) отсутствует, повреждено или противоречиво.</summary>
public class VersionMetadataException : Exception
{
    public VersionMetadataException(string message) : base(message) { }
    public VersionMetadataException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Такой версии нет ни на диске, ни в каталоге Mojang.</summary>
public sealed class VersionNotFoundException : VersionMetadataException
{
    public VersionNotFoundException(string versionId) : base($"Версия «{versionId}» не найдена.") => VersionId = versionId;
    public string VersionId { get; }
}
