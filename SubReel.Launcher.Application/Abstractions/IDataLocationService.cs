namespace SubReel.Launcher.Application.Abstractions;

public enum DataLocationError
{
    None,
    /// <summary>Нужен полный путь (например, D:\SubReel).</summary>
    NotAbsolute,
    /// <summary>Новая папка лежит внутри текущей папки данных.</summary>
    NestedInCurrent,
    /// <summary>Текущая папка данных лежит внутри выбранной.</summary>
    ContainsCurrent,
    /// <summary>В выбранную папку нельзя записать (нет прав, диск только для чтения или недоступен).</summary>
    NotWritable,
}

/// <param name="Root">Папка данных, которая будет использована после перезапуска (с учётом правил выбора).</param>
public sealed record DataLocationChange(bool Succeeded, string? Root, DataLocationError Error);

/// <summary>
/// Где лаунчер хранит данные. По умолчанию у всех <see cref="DefaultRoot"/>; пользователь может выбрать другую папку (например, на другом диске).
/// Смена вступает в силу при следующем запуске: до открытия каких-либо файлов лаунчер переносит данные на новое место.
/// </summary>
public interface IDataLocationService
{
    /// <summary>Папка данных по умолчанию.</summary>
    string DefaultRoot { get; }

    /// <summary>Папка данных, с которой лаунчер работает в этом сеансе.</summary>
    string CurrentRoot { get; }

    /// <summary>Выбранная, но ещё не применённая папка (применится после перезапуска); null — смены не ожидается.</summary>
    string? PendingRoot { get; }

    /// <summary>true — сейчас используется не папка по умолчанию.</summary>
    bool IsCustom { get; }

    /// <summary>
    /// Запоминает новую папку данных. Если выбранная папка не пуста и не является папкой данных SubReel, внутри неё создаётся подпапка «SubReel»,
    /// чтобы не смешивать данные лаунчера с чужими файлами. Выбор текущей папки отменяет ожидающую смену.
    /// </summary>
    DataLocationChange Change(string chosenFolder);

    /// <summary>Возвращает папку по умолчанию (применится после перезапуска).</summary>
    DataLocationChange ResetToDefault();
}
