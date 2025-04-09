namespace EphemeralMongo.Download;

internal sealed class NamedMutex
{
    private readonly Dictionary<string, ReferenceAwareMutex> _mutexes;

    public NamedMutex()
    {
        this._mutexes = new Dictionary<string, ReferenceAwareMutex>(StringComparer.Ordinal);
    }

    public Task WaitAsync(string name, CancellationToken cancellationToken)
    {
        return this.GetOrAdd(name).WaitAsync(cancellationToken);
    }

    private ReferenceAwareMutex GetOrAdd(string name)
    {
        lock (this._mutexes)
        {
            ReferenceAwareMutex mutex;

            if (this._mutexes.TryGetValue(name, out var existingMutex))
            {
                mutex = existingMutex;
            }
            else
            {
                mutex = new ReferenceAwareMutex();
                this._mutexes.Add(name, mutex);
            }

            mutex.IncrementReferenceCount();
            return mutex;
        }
    }

    public void Release(string name)
    {
        lock (this._mutexes)
        {
            if (this._mutexes.TryGetValue(name, out var mutex))
            {
                mutex.DecrementReferenceCount();
                mutex.Release();

                if (!mutex.IsReferenced)
                {
                    this._mutexes.Remove(name);
                    mutex.Dispose();
                }
            }
        }
    }

    private sealed class ReferenceAwareMutex : IDisposable
    {
        private readonly SemaphoreSlim _mutex;

        // Accessing and modifying this field can be done without Interlocked because it's done inside a lock
        private int _referenceCount;

        public ReferenceAwareMutex()
        {
            this._mutex = new SemaphoreSlim(1, 1);
            this._referenceCount = 0;
        }

        public bool IsReferenced => this._referenceCount > 0;

        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            await this._mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public void IncrementReferenceCount() => this._referenceCount++;

        public void DecrementReferenceCount() => this._referenceCount--;

        public void Release()
        {
            this._mutex.Release();
        }

        public void Dispose()
        {
            this._mutex.Dispose();
        }
    }
}