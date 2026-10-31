namespace SubReel.Launcher.Application.Abstractions;

/// <summary>Хранилище своих аватарок аккаунтов.</summary>
public interface IAvatarStorage
{
    /// <summary>Копирует картинку в папку лаунчера и возвращает путь к копии (null — не удалось, причина записана в журнал).</summary>
    string? Save(string accountName, string sourcePath);
}
