using System;
using System.Collections.Generic;
using System.Text;

namespace MuxTee;

// The mouse half of "act like a stock terminal".
//
// Windows Terminal sits OUTSIDE muxtee and already resolves a wheel notch the way a real terminal does
// (see docs/scroll-parity.json): it scrolls its own scrollback on the normal screen, sends arrow keys for
// the alternate screen under DECSET 1007, and hands a mouse report to the app only when the app asked to
// track the mouse. It decides that from the child's output, which T1 passes through verbatim - so WT's
// decision is identical with and without muxtee in the chain.
//
// What breaks is the last hop. WT delivers that report to us as a MOUSE_EVENT record; T2 used to drop it,
// so a tracking TUI (htop, vim, claude) got no wheel, no click and no drag in a mux tab. We now re-encode
// the record into the VT mouse report the inner app actually asked for and write it into the inner pty.
//
// The encoding has to be the one the app negotiated (SGR/urxvt/X10), so the tracker below watches the
// child's own DECSET/DECRST output for those modes and for mouse tracking itself.

// Watches a stream of child output for the DEC private modes that govern the mouse. Kept as a byte-level
// state machine so a sequence split across two reads still lands.
internal sealed class ScreenModeTracker
{
    private enum S { Ground, Esc, Csi }

    private S _state = S.Ground;
    private readonly List<int> _params = new();
    private int _cur;
    private bool _haveCur;
    private bool _private;

    private bool _m9, _m1000, _m1002, _m1003;   // mouse tracking (any one => tracking on)
    public bool Sgr { get; private set; }        // 1006
    public bool Urxvt { get; private set; }      // 1015
    public bool AltScroll { get; private set; }  // 1007
    public bool AlternateScreen { get; private set; }  // 47 / 1047 / 1049

    // "The app wants mouse reports" - any tracking mode being on. This is the gate: without it a report
    // would just be echoed as literal bytes by a shell that never asked for one.
    public bool MouseTracking => _m9 || _m1000 || _m1002 || _m1003;

    public void Feed(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            switch (_state)
            {
                case S.Ground:
                    if (b == 0x1b) _state = S.Esc;
                    break;

                case S.Esc:
                    if (b == '[') { Reset(); _state = S.Csi; }
                    else if (b != 0x1b) _state = S.Ground;   // an OSC or a lone ESC; not a mode set
                    break;

                case S.Csi:
                    if (b >= 0x40 && b <= 0x7e) { Apply(b); _state = S.Ground; }
                    else if (b == '?' || b == '<' || b == '>' || b == '=') _private = b == '?';
                    else if (b >= '0' && b <= '9') { _cur = _cur * 10 + (b - '0'); _haveCur = true; }
                    else if (b == ';' || b == ':') { PushCur(); }
                    else if (b >= 0x20 && b <= 0x2f) { /* intermediate byte (e.g. '$'); not a mode set */ }
                    else _state = S.Ground;                   // malformed; abandon
                    break;
            }
        }
    }

    private void Reset()
    {
        _params.Clear();
        _cur = 0;
        _haveCur = false;
        _private = false;
    }

    private void PushCur()
    {
        if (_haveCur) { _params.Add(_cur); _cur = 0; _haveCur = false; }
    }

    private void Apply(byte final)
    {
        if (!_private || (final != (byte)'h' && final != (byte)'l')) return;
        PushCur();
        bool set = final == (byte)'h';
        foreach (var p in _params) Set(p, set);
    }

    private void Set(int mode, bool on)
    {
        switch (mode)
        {
            case 9: _m9 = on; break;
            case 1000: _m1000 = on; break;
            case 1002: _m1002 = on; break;
            case 1003: _m1003 = on; break;
            case 1006: Sgr = on; break;
            case 1015: Urxvt = on; break;
            case 1007: AltScroll = on; break;
            case 47:
            case 1047:
            case 1049: AlternateScreen = on; break;
        }
    }
}

// A console MOUSE_EVENT -> the VT mouse report a terminal would send, in the encoding the app negotiated.
internal static class MouseInput
{
    // X10 coordinate bytes are raw 32+c, and can run past 0x7f; muxctl clamps to 222 for the same reason
    // (kept identical so the tee and the local attach emit byte-identical reports).
    private const int MaxCoord = 222;

    public static byte[] Encode(uint buttonState, uint ctrlState, uint eventFlags, int x, int y,
        bool sgr, bool urxvt)
    {
        int code; bool release = false;
        bool motion = (eventFlags & ConsoleApi.MOUSE_MOVED) != 0;

        if ((eventFlags & ConsoleApi.MOUSE_WHEELED) != 0)
        {
            code = ((short)(buttonState >> 16)) > 0 ? 64 : 65;
        }
        else if ((eventFlags & ConsoleApi.MOUSE_HWHEELED) != 0)
        {
            code = ((short)(buttonState >> 16)) > 0 ? 66 : 67;
        }
        else
        {
            uint b = buttonState;
            if ((b & ConsoleApi.FROM_LEFT_1ST_BUTTON_PRESSED) != 0) code = 0;
            else if ((b & ConsoleApi.FROM_LEFT_2ND_BUTTON_PRESSED) != 0) code = 1;
            else if ((b & ConsoleApi.RIGHTMOST_BUTTON_PRESSED) != 0) code = 2;
            else if ((b & ConsoleApi.FROM_LEFT_3RD_BUTTON_PRESSED) != 0) code = 128;
            else if ((b & ConsoleApi.FROM_LEFT_4TH_BUTTON_PRESSED) != 0) code = 129;
            else code = 3;                                     // no button: a release (or bare motion)
            if (code == 3 && !motion) release = true;
            if (motion) code += 32;
        }

        // xterm modifier bits ride the button code: shift 4, meta 8, ctrl 16.
        if ((ctrlState & KeyTranslator.SHIFT_PRESSED) != 0) code += 4;
        if ((ctrlState & (KeyTranslator.LEFT_ALT_PRESSED | KeyTranslator.RIGHT_ALT_PRESSED)) != 0) code += 8;
        if ((ctrlState & (KeyTranslator.LEFT_CTRL_PRESSED | KeyTranslator.RIGHT_CTRL_PRESSED)) != 0) code += 16;

        x = Math.Clamp(x, 1, MaxCoord);
        y = Math.Clamp(y, 1, MaxCoord);

        if (sgr)
            return Encoding.ASCII.GetBytes($"\x1b[<{code};{x};{y}{(release ? 'm' : 'M')}");
        if (urxvt)
            return Encoding.ASCII.GetBytes($"\x1b[{code + 32};{x};{y}M");

        // X10: raw bytes, so a coordinate above 0x7f stays one byte rather than being UTF-8 doubled.
        return new byte[]
        {
            0x1b, (byte)'[', (byte)'M',
            (byte)(32 + code), (byte)(32 + x), (byte)(32 + y),
        };
    }
}
