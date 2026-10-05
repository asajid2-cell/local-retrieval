using System;
using System.Collections.Concurrent;
using System.Threading;

namespace MuxTee;

// The single serialisation point for everything that goes into the child's input pipe: local keystrokes
// from T2, and remote frames from the net task. Invariant 2 lives here - each item is written whole, in
// the order it was queued, by exactly one writer (T3). Multi-byte VT sequences never interleave.
internal sealed class InputQueue
{
    private readonly BlockingCollection<byte[]> _queue = new(new ConcurrentQueue<byte[]>());
    private readonly Action<string> _log;

    public InputQueue(Action<string> log) => _log = log;

    public void Enqueue(byte[] chunk)
    {
        if (chunk.Length == 0) return;
        try
        {
            // Unbounded: a full queue would mean the child has stopped reading its input, and blocking
            // here would freeze local typing. The alternative (dropping) would corrupt sequences.
            _queue.Add(chunk);
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding already ran - we are shutting down; drop it.
        }
    }

    // Blocks until one chunk is available or the queue is completed and drained.
    public bool TryTake(out byte[] chunk) => _queue.TryTake(out chunk!, Timeout.Infinite);

    public void Complete() => _queue.CompleteAdding();
}
