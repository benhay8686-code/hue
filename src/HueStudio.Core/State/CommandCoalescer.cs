namespace HueStudio.Core.State;

/// <summary>
/// Keeps sliders and colour wheels smooth without flooding the bridge.
///
/// The bridge handles roughly 10 light commands/second and 1 group command/second. While the user
/// drags, we send at most one command per key every <see cref="MinInterval"/>, always the latest
/// value, and always send the final value when they stop.
/// </summary>
public sealed class CommandCoalescer(TimeSpan minInterval)
{
    private readonly Dictionary<string, Slot> _slots = new();
    private readonly Lock _gate = new();

    public TimeSpan MinInterval { get; } = minInterval;

    /// <summary>Raised when a send fails (e.g. bridge offline), so the UI can show it.</summary>
    public event EventHandler<Exception>? Failed;

    public void Submit(string key, Func<CancellationToken, Task> send)
    {
        Slot slot;
        lock (_gate)
        {
            if (!_slots.TryGetValue(key, out slot!))
                _slots[key] = slot = new Slot();
            slot.Pending = send;
            if (slot.Running) return; // the running loop will pick up the latest value
            slot.Running = true;
        }
        _ = RunAsync(key, slot);
    }

    private async Task RunAsync(string key, Slot slot)
    {
        while (true)
        {
            Func<CancellationToken, Task>? next;
            lock (_gate)
            {
                next = slot.Pending;
                slot.Pending = null;
                if (next is null)
                {
                    slot.Running = false;
                    _slots.Remove(key);
                    return;
                }
            }

            var started = DateTime.UtcNow;
            try { await next(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { Failed?.Invoke(this, ex); }

            var wait = MinInterval - (DateTime.UtcNow - started);
            if (wait > TimeSpan.Zero) await Task.Delay(wait).ConfigureAwait(false);
        }
    }

    private sealed class Slot
    {
        public Func<CancellationToken, Task>? Pending;
        public bool Running;
    }
}
