using System;
using System.Text;

namespace MuxTee;

internal static class Utf
{
    // T2 receives INPUT_RECORDs from a win32-input-mode console, so the uChars are the already-encoded VT
    // or win32-input sequence as UTF-16 units. We transcode to bytes once here; the pipe takes bytes.
    //
    // A read call's records are concatenated into ONE chunk so a sequence split across records (a
    // surrogate pair, or a CSI that arrived in pieces) still lands in the pipe as one write.
    public static byte[] EncodeChunk(string text)
    {
        if (text.Length == 0) return Array.Empty<byte>();
        var bytes = Encoding.UTF8.GetBytes(text);
        return bytes;
    }

    public static string Decode(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);
}
