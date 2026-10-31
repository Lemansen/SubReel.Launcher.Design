namespace SubReel.Launcher.Application.Abstractions;

/// <summary>Следит за подключением к сети. Нужен, чтобы лаунчер сам обновил данные, когда интернет вернулся.</summary>
public interface INetworkMonitor
{
    /// <summary>Сеть появилась (или сменилась на рабочую). Срабатывает в фоновом потоке, не чаще раза в пару секунд.</summary>
    event EventHandler? Restored;
}
