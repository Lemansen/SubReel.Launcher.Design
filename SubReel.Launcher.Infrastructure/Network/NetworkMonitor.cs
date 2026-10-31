using System.Net.NetworkInformation;
using SubReel.Launcher.Application.Abstractions;

namespace SubReel.Launcher.Infrastructure.Network;

/// <summary>
/// Сообщает о появлении сети (события ОС). Подряд идущие события склеиваются: Windows при подключении шлёт их несколько,
/// а сразу после подключения DNS и маршруты ещё не готовы, поэтому выдерживается короткая пауза.
/// </summary>
public sealed class NetworkMonitor : INetworkMonitor, IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private CancellationTokenSource? _pending;
    private bool _disposed;

    public NetworkMonitor()
    {
        NetworkChange.NetworkAvailabilityChanged += OnChanged;
        NetworkChange.NetworkAddressChanged += OnChanged;
    }

    public event EventHandler? Restored;

    public void Dispose()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnChanged;
        NetworkChange.NetworkAddressChanged -= OnChanged;
        lock (_gate)
        {
            _disposed = true;
            _pending?.Cancel();
        }
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_disposed) return;
            _pending?.Cancel(); // новое событие сбрасывает ожидание предыдущего
            cts = _pending = new CancellationTokenSource();
        }
        _ = RaiseLaterAsync(cts.Token);
    }

    private async Task RaiseLaterAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Debounce, ct).ConfigureAwait(false);
            if (NetworkInterface.GetIsNetworkAvailable()) Restored?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            // пришло более новое событие или лаунчер закрывается
        }
        catch (Exception)
        {
            // подписчик не должен ронять процесс через неотслеживаемую задачу; свои сбои он логирует сам
        }
    }
}
