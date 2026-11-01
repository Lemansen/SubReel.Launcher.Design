namespace SubReel.Launcher.Infrastructure.Storage;

/// <summary>
/// Перенос папок и файлов данных. Ничего не перезаписывает: если место назначения занято, перенос не выполняется.
/// Папка переносится переименованием; если это невозможно (другой диск, файл занят), копируется во временную «.moving»,
/// переименовывается и только потом удаляется источник, поэтому при сбое данные остаются на старом месте целыми.
/// </summary>
public static class StorageMover
{
    /// <summary>
    /// true — папка перенесена (или переносить нечего). <paramref name="problem"/> заполнен, если что-то пошло не так:
    /// при false — перенос не выполнен; при true — данные на новом месте, но старую папку удалить не удалось.
    /// </summary>
    public static bool MoveDirectory(string source, string destination, out string? problem)
    {
        problem = null;
        if (!Directory.Exists(source)) return true;
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            problem = $"«{destination}» уже существует.";
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            try
            {
                Directory.Move(source, destination);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // другой диск или занятый файл: копируем
            }

            var staging = destination + ".moving";
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            CopyDirectory(source, staging);
            Directory.Move(staging, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = ex.Message;
            return false;
        }

        try
        {
            Directory.Delete(source, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"Данные скопированы, но старую папку «{source}» удалить не удалось: {ex.Message}";
        }
        return true;
    }

    /// <summary>Переносит один файл. Существующий файл назначения не перезаписывается.</summary>
    public static bool MoveFile(string source, string destination, out string? problem)
    {
        problem = null;
        if (!File.Exists(source)) return true;
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            problem = $"«{destination}» уже существует.";
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(source, destination);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = ex.Message;
            return false;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}
