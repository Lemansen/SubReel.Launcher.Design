using Microsoft.Extensions.Logging;

namespace SubReel.Launcher.Design.Infrastructure;

public static class TaskExtensions
{
    /// <summary>
    /// Запускает фоновую операцию «без ожидания» так, чтобы её сбой не терялся: ошибка попадает в журнал, процесс не падает.
    /// Отмена ошибкой не считается. Продолжение выполняется в потоке интерфейса, как у обычного await.
    /// </summary>
    public static void FireAndForget(this Task task, ILogger logger, string operation) => _ = ObserveAsync(task, logger, operation);

    private static async Task ObserveAsync(Task task, ILogger logger, string operation)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Фоновая операция «{Operation}» отменена.", operation);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Фоновая операция «{Operation}» завершилась ошибкой.", operation);
        }
    }
}
