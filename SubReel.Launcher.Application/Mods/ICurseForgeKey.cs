namespace SubReel.Launcher.Application.Mods;

/// <summary>Ключ API CurseForge (выдаётся на console.curseforge.com). Хранится зашифрованным; без него каталог CurseForge недоступен.</summary>
public interface ICurseForgeKey
{
    bool IsConfigured { get; }

    /// <summary>Расшифрованный ключ; null — не задан или не расшифровывается (например, файл перенесён на другой компьютер).</summary>
    string? Get();

    /// <summary>Сохраняет ключ; пустое значение удаляет его. false — не удалось зашифровать или записать.</summary>
    bool Set(string? key);
}
