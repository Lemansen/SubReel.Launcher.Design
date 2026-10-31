using System.Text;

namespace SubReel.Launcher.Domain.Instances;

public enum InstanceValidationError
{
    None,
    NameEmpty,
    NameTooLong,
    NameDuplicate,
    McVersionEmpty,
    MemoryInvalid,
}

/// <summary>Бизнес-правила экземпляров: проверка полей и построение имени папки. Не зависят от диска.</summary>
public static class InstanceRules
{
    public const int MaxNameLength = 64;
    public const int MinMemoryLimitMb = 256;
    public const int MaxMemoryLimitMb = 1024 * 1024;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static InstanceValidationError Validate(Instance instance, IEnumerable<Instance> others)
    {
        var name = instance.Name?.Trim() ?? "";
        if (name.Length == 0) return InstanceValidationError.NameEmpty;
        if (name.Length > MaxNameLength) return InstanceValidationError.NameTooLong;
        if (string.IsNullOrWhiteSpace(instance.McVersion)) return InstanceValidationError.McVersionEmpty;

        foreach (var other in others)
        {
            if (other.Id != instance.Id && string.Equals(other.Name.Trim(), name, StringComparison.OrdinalIgnoreCase))
                return InstanceValidationError.NameDuplicate;
        }

        var min = instance.MinMemoryMb;
        var max = instance.MaxMemoryMb;
        if (min is not null && (min < MinMemoryLimitMb || min > MaxMemoryLimitMb)) return InstanceValidationError.MemoryInvalid;
        if (max is not null && (max < MinMemoryLimitMb || max > MaxMemoryLimitMb)) return InstanceValidationError.MemoryInvalid;
        if (min is not null && max is not null && min > max) return InstanceValidationError.MemoryInvalid;

        return InstanceValidationError.None;
    }

    /// <summary>
    /// Имя папки из названия: латиница, цифры, «-», «_» и «.»; остальное становится «-». Результат безопасен как имя папки
    /// в Windows (без зарезервированных имён, без точки и пробела на конце) и никогда не пустой.
    /// </summary>
    public static string ToFolderName(string name)
    {
        var builder = new StringBuilder();
        var lastWasSeparator = false;
        foreach (var ch in name.Trim())
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '_' or '.')
            {
                builder.Append(ch);
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator && builder.Length > 0)
            {
                builder.Append('-');
                lastWasSeparator = true;
            }
        }

        var result = builder.ToString().Trim('-', '.');
        if (result.Length == 0) result = "instance";
        if (result.Length > MaxNameLength) result = result[..MaxNameLength].TrimEnd('-', '.');
        // В Windows имя «CON.txt» зарезервировано так же, как «CON»: проверяем часть до первой точки.
        if (ReservedNames.Contains(result.Split('.')[0])) result = "instance-" + result;
        return result;
    }
}
