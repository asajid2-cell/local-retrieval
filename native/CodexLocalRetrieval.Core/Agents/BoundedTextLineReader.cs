using System.Text;

namespace CodexLocalRetrieval.Core.Agents;

public sealed class BoundedTextLineReader
{
    private readonly TextReader _reader;
    private readonly int _maxLineChars;
    private readonly bool _discardOversizedLine;
    private readonly char[] _buffer;
    private int _offset;
    private int _count;
    private bool _skipLeadingLineFeed;

    public BoundedTextLineReader(
        TextReader reader,
        int maxLineChars,
        int bufferSize = 8192,
        bool discardOversizedLine = false)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (maxLineChars <= 0) throw new ArgumentOutOfRangeException(nameof(maxLineChars));
        if (bufferSize <= 0) throw new ArgumentOutOfRangeException(nameof(bufferSize));
        _reader = reader;
        _maxLineChars = maxLineChars;
        _discardOversizedLine = discardOversizedLine;
        _buffer = new char[bufferSize];
    }

    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        var line = new StringBuilder(Math.Min(_maxLineChars, 4096));

        while (true)
        {
            if (_offset == _count)
            {
                _count = await _reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                _offset = 0;
                if (_count == 0)
                    return line.Length == 0 ? null : line.ToString();
            }

            if (_skipLeadingLineFeed)
            {
                _skipLeadingLineFeed = false;
                if (_buffer[_offset] == '\n')
                {
                    _offset++;
                    continue;
                }
            }

            var ch = _buffer[_offset++];
            if (ch == '\n') return line.ToString();
            if (ch == '\r')
            {
                _skipLeadingLineFeed = true;
                return line.ToString();
            }
            if (line.Length >= _maxLineChars)
            {
                if (_discardOversizedLine)
                    await DiscardRemainderOfLineAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidDataException($"Protocol line exceeded the {_maxLineChars}-character limit.");
            }
            line.Append(ch);
        }
    }

    public string? ReadLine()
    {
        var line = new StringBuilder(Math.Min(_maxLineChars, 4096));

        while (true)
        {
            if (_offset == _count)
            {
                _count = _reader.Read(_buffer, 0, _buffer.Length);
                _offset = 0;
                if (_count == 0)
                    return line.Length == 0 ? null : line.ToString();
            }

            if (_skipLeadingLineFeed)
            {
                _skipLeadingLineFeed = false;
                if (_buffer[_offset] == '\n')
                {
                    _offset++;
                    continue;
                }
            }

            var ch = _buffer[_offset++];
            if (ch == '\n') return line.ToString();
            if (ch == '\r')
            {
                _skipLeadingLineFeed = true;
                return line.ToString();
            }
            if (line.Length >= _maxLineChars)
            {
                if (_discardOversizedLine)
                    DiscardRemainderOfLine();
                throw new InvalidDataException($"Protocol line exceeded the {_maxLineChars}-character limit.");
            }
            line.Append(ch);
        }
    }

    private async ValueTask DiscardRemainderOfLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_offset == _count)
            {
                _count = await _reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                _offset = 0;
                if (_count == 0) return;
            }

            var ch = _buffer[_offset++];
            if (ch == '\n') return;
            if (ch == '\r')
            {
                _skipLeadingLineFeed = true;
                return;
            }
        }
    }

    private void DiscardRemainderOfLine()
    {
        while (true)
        {
            if (_offset == _count)
            {
                _count = _reader.Read(_buffer, 0, _buffer.Length);
                _offset = 0;
                if (_count == 0) return;
            }

            var ch = _buffer[_offset++];
            if (ch == '\n') return;
            if (ch == '\r')
            {
                _skipLeadingLineFeed = true;
                return;
            }
        }
    }
}
