using System;

namespace MuxTee;

// A fixed-capacity byte ring. T1 appends every chunk it reads from the child; a reconnect replays the
// whole thing so the viewer's history matches ours without asking anyone.
//
// Not thread-safe: the single output thread (T1) is the only writer, and a snapshot is taken under the
// same thread's turn. Callers that cross threads must lock around the ring themselves.
internal sealed class RingBuffer
{
    private readonly byte[] _buffer;
    private int _start;      // index of the oldest live byte
    private int _count;      // live bytes

    public RingBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _buffer = new byte[capacity];
    }

    public int Capacity => _buffer.Length;
    public int Count => _count;

    // We drop from the FRONT on overflow. Terminal output is a stream, so the newest bytes are the ones
    // that describe the current screen; the oldest are the ones a viewer can afford to lose.
    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= _buffer.Length)
        {
            // The chunk alone is bigger than the ring: keep its tail, which is all that fits.
            bytes[^_buffer.Length..].CopyTo(_buffer);
            _start = 0;
            _count = _buffer.Length;
            return;
        }

        var overflow = _count + bytes.Length - _buffer.Length;
        if (overflow > 0)
        {
            _start = (_start + overflow) % _buffer.Length;
            _count -= overflow;
        }

        var writeAt = (_start + _count) % _buffer.Length;
        var firstSpan = Math.Min(bytes.Length, _buffer.Length - writeAt);
        bytes[..firstSpan].CopyTo(_buffer.AsSpan(writeAt));
        if (firstSpan < bytes.Length)
            bytes[firstSpan..].CopyTo(_buffer);
        _count += bytes.Length;
    }

    // Oldest-to-newest copy. Used for the reconnect snapshot, so it is a plain allocation on purpose -
    // this runs once per reconnect, never on the hot path.
    public byte[] Snapshot()
    {
        var outBytes = new byte[_count];
        if (_count == 0) return outBytes;
        var firstSpan = Math.Min(_count, _buffer.Length - _start);
        Array.Copy(_buffer, _start, outBytes, 0, firstSpan);
        if (firstSpan < _count)
            Array.Copy(_buffer, 0, outBytes, firstSpan, _count - firstSpan);
        return outBytes;
    }

    public void Clear()
    {
        _start = 0;
        _count = 0;
    }
}
