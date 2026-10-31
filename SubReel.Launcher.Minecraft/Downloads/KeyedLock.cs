namespace SubReel.Launcher.Minecraft.Downloads;

/// <summary>
/// Взаимное исключение по ключу (здесь — по пути файла): два запроса на один файл выполняются по очереди,
/// поэтому второй находит уже скачанный файл и не качает его повторно. Записи удаляются, когда ключ никому не нужен.
/// </summary>
internal sealed class KeyedLock
{
    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int References;
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IDisposable> AcquireAsync(string key, CancellationToken ct)
    {
        Entry entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _entries[key] = entry;
            }
            entry.References++;
        }

        try
        {
            await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            Unreference(key, entry);
            throw;
        }

        return new Releaser(this, key, entry);
    }

    public int ActiveKeys
    {
        get
        {
            lock (_entries) return _entries.Count;
        }
    }

    private void Unreference(string key, Entry entry)
    {
        lock (_entries)
        {
            if (--entry.References == 0) _entries.Remove(key);
        }
    }

    private sealed class Releaser : IDisposable
    {
        private readonly KeyedLock _owner;
        private readonly string _key;
        private readonly Entry _entry;
        private int _released;

        public Releaser(KeyedLock owner, string key, Entry entry)
        {
            _owner = owner;
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            _entry.Gate.Release();
            _owner.Unreference(_key, _entry);
        }
    }
}
