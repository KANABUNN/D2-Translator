namespace D2Translator.Application.Translation;

/// <summary>
/// 外部要求の同時実行数（合計 2）を、返信を優先して割り当てる（仕様書 15.4）。
/// ただし返信を連続 3 件通したら、待っている受信を少なくとも 1 件通す。
/// </summary>
public sealed class PriorityGate(int capacity, int maxConsecutivePriority = 3)
{
    private readonly Lock _gate = new();
    private readonly Queue<TaskCompletionSource> _priority = new();
    private readonly Queue<TaskCompletionSource> _normal = new();
    private int _available = capacity;
    private int _consecutivePriority;

    public async Task<IDisposable> EnterAsync(bool priority, CancellationToken ct)
    {
        TaskCompletionSource waiter;
        lock (_gate)
        {
            if (_available > 0 && _priority.Count == 0 && (priority || _normal.Count == 0))
            {
                _available--;
                Count(priority);
                return new Releaser(this);
            }

            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            (priority ? _priority : _normal).Enqueue(waiter);
        }

        // 取り消された待機は、割り当て時に TrySetResult が失敗して読み飛ばされる
        using (ct.Register(() => waiter.TrySetCanceled(ct)))
        {
            await waiter.Task.ConfigureAwait(false);
        }

        return new Releaser(this);
    }

    private void Count(bool priority) => _consecutivePriority = priority ? _consecutivePriority + 1 : 0;

    private void Release()
    {
        lock (_gate)
        {
            _available++;
            while (_available > 0)
            {
                bool takePriority = _priority.Count > 0 && (_normal.Count == 0 || _consecutivePriority < maxConsecutivePriority);
                Queue<TaskCompletionSource>? queue = takePriority ? _priority : _normal.Count > 0 ? _normal : null;
                if (queue is null)
                {
                    break;
                }

                if (queue.Dequeue().TrySetResult())
                {
                    _available--;
                    Count(takePriority);
                }
            }
        }
    }

    private sealed class Releaser(PriorityGate owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release();
            }
        }
    }
}
