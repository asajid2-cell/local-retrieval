using System;
using System.Collections.Concurrent;
using System.Threading;

namespace MuxTee;

// One item waiting for T3. `Rid` is set only for a REMOTE frame: the sender asked for an
// `inputResult` and muxd is waiting on it (OwnerSession.write_confirmed), so the answer is owed once the
// bytes have actually reached the child, not when they were queued.
internal readonly struct InputItem
{
    public readonly byte[] Data;
    public readonly string? Rid;

    public InputItem(byte[] data, string? rid)
    {
        Data = data;
        Rid = rid;
    }
}

// The single serialisation point for everything that goes into the child's input pipe: local keystrokes
// from T2, and remote frames from the net task. Invariant 2 lives here - each item is written whole, in
// the order it was queued, by exactly one writer (T3). Multi-byte VT sequences never interleave.
internal sealed class InputQueue
{
    private readonly BlockingCollection<InputItem> _queue = new(new ConcurrentQueue<InputItem>());
    private readonly Action<string> _log;

    // Raised by T3 after a remote item was written (ok) or the write threw (not ok). The net link turns
    // this into the wire ack; nothing here knows about websockets.
    public event Action<string, bool>? Written;

    public InputQueue(Action<string> log) => _log = log;

    public void Enqueue(byte[] chunk) => Enqueue(chunk, null);

    // `rid` is the sender's receipt id. A local keystroke passes null and owes nobody an answer.
    public void Enqueue(byte[] chunk, string? rid)
    {
        if (chunk is null || chunk.Length == 0) return;
        try
        {
            // Unbounded: a full queue would mean the child has stopped reading its input, and blocking
            // here would freeze local typing. The alternative (dropping) would corrupt sequences.
            _queue.Add(new InputItem(chunk, rid));
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding already ran - we are shutting down; drop it.
        }
    }

    // Blocks until one item is available or the queue is completed and drained.
    public bool TryTake(out InputItem item) => _queue.TryTake(out item, Timeout.Infinite);

    internal void NotifyWritten(string? rid, bool ok)
    {
        if (rid is null) return;
        try { Written?.Invoke(rid, ok); } catch (Exception ex) { _log("input ack hook: " + ex.Message); }
    }

    public void Complete() => _queue.CompleteAdding();
}
