using SubReel.Launcher.Domain.Instances;

namespace SubReel.Launcher.Application.Instances;

/// <summary>Хранилище экземпляров. Каждый экземпляр лежит в своей папке; идентификатор экземпляра — имя папки.</summary>
public interface IInstanceRepository
{
    Task<IReadOnlyList<Instance>> ListAsync(CancellationToken ct = default);

    Task<Instance?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>Создаёт экземпляр: выбирает свободное имя папки, записывает описание и возвращает сохранённый экземпляр с <see cref="Instance.Id"/>.</summary>
    Task<Instance> CreateAsync(Instance draft, CancellationToken ct = default);

    /// <summary>Обновляет описание существующего экземпляра. false — экземпляра нет или его файл нельзя перезаписывать.</summary>
    Task<bool> UpdateAsync(Instance instance, CancellationToken ct = default);

    /// <summary>Удаляет папку экземпляра целиком вместе с модами и сохранениями. false — экземпляра нет или удалить не удалось.</summary>
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>Папка игры экземпляра (saves, mods, options.txt). Только вычисляет путь.</summary>
    string GetGameDirectory(string id);
}
