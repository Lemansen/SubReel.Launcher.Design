using SubReel.Launcher.Domain.Versions;

namespace SubReel.Launcher.Domain.Modpacks;

/// <summary>Сборка Modrinth (.mrpack) не подходит для импорта: файл повреждён, формат не поддерживается или в нём небезопасное содержимое.</summary>
public sealed class ModpackException : Exception
{
    public ModpackException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>Один файл сборки (мод, ресурспак, шейдер…), который нужно скачать в папку игры.</summary>
/// <param name="Path">Относительный путь внутри папки игры (<c>mods/sodium.jar</c>), всегда с «/», проверенный на выход за папку.</param>
/// <param name="Sha1">SHA-1 (hex, нижний регистр).</param>
/// <param name="Sha512">SHA-512 (hex, нижний регистр).</param>
/// <param name="Url">Адрес скачивания: https и узел из списка доверенных (<see cref="MrpackParser.TrustedHosts"/>).</param>
/// <param name="Size">Размер в байтах; null — не указан.</param>
public sealed record MrpackFile(string Path, string Sha1, string Sha512, string Url, long? Size);

/// <summary>Разобранный <c>modrinth.index.json</c>.</summary>
/// <param name="LoaderVersion">Версия загрузчика; null у ванили.</param>
/// <param name="Files">Только файлы, нужные на клиенте (серверные пропущены).</param>
public sealed record MrpackIndex(
    string Name,
    string? VersionId,
    string? Summary,
    string McVersion,
    LoaderKind Loader,
    string? LoaderVersion,
    IReadOnlyList<MrpackFile> Files);
