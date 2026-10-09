using System;
using System.Collections.Generic;

namespace MuxTee;

// A console KEY_EVENT (key-down) -> the string a stock terminal sends for that key.
//
// T2 used to forward ONLY k.UnicodeChar, which is '\0' for every key that carries no character: the
// arrows, Home/End, Insert/Delete, PageUp/PageDown, the whole F-key row and Shift+Tab were dropped
// before they ever reached the child, so up-arrow never recalled history. This mirrors
// muxctl.ConsoleInputTranslator / translate_key_event - the proven table the local attach already uses -
// so the tee and the local attach hand the hosted app identical bytes.
//
// The encodings below are xterm's, which Windows Terminal also emits, so a mux tab and a plain tab
// deliver the same bytes to the shell: CSI for the navigation block, SS3 for unmodified F1-F4, CSI with
// a modifier parameter (1 + shift(1) + alt(2) + ctrl(4)) for every modified form. Backspace is the one
// place the console's own idea (0x08) differs from a terminal's (0x7f): WT sends 0x7f for Backspace and
// 0x08 for Ctrl+Backspace, and getting this backwards makes Backspace delete a whole word in a TUI.
internal static class KeyTranslator
{
    // dwControlKeyState bits (wincon.h).
    public const uint RIGHT_ALT_PRESSED = 0x0001;
    public const uint LEFT_ALT_PRESSED = 0x0002;
    public const uint RIGHT_CTRL_PRESSED = 0x0004;
    public const uint LEFT_CTRL_PRESSED = 0x0008;
    public const uint SHIFT_PRESSED = 0x0010;

    private const ushort VK_BACK = 0x08;
    private const ushort VK_TAB = 0x09;

    // Alt. Alt+numpad composition delivers its character on this key's key-UP record, not on key-down.
    public const ushort VK_MENU = 0x12;

    // Navigation and function keys (virtual-key code) -> (unmodified sequence, modified sequence). The
    // modified form is a format string whose {0} is the xterm modifier parameter. Plain CSI/SS3 forms
    // are exactly what muxctl emits, so key handling is identical across the tee and the local attach.
    private static readonly Dictionary<ushort, (string Plain, string Mod)> VkToVt = new()
    {
        [0x26] = ("\x1b[A", "\x1b[1;{0}A"),       // Up
        [0x28] = ("\x1b[B", "\x1b[1;{0}B"),       // Down
        [0x27] = ("\x1b[C", "\x1b[1;{0}C"),       // Right
        [0x25] = ("\x1b[D", "\x1b[1;{0}D"),       // Left
        [0x24] = ("\x1b[H", "\x1b[1;{0}H"),       // Home
        [0x23] = ("\x1b[F", "\x1b[1;{0}F"),       // End
        [0x21] = ("\x1b[5~", "\x1b[5;{0}~"),      // PageUp
        [0x22] = ("\x1b[6~", "\x1b[6;{0}~"),      // PageDown
        [0x2E] = ("\x1b[3~", "\x1b[3;{0}~"),      // Delete
        [0x2D] = ("\x1b[2~", "\x1b[2;{0}~"),      // Insert
        [0x70] = ("\x1bOP", "\x1b[1;{0}P"),       // F1
        [0x71] = ("\x1bOQ", "\x1b[1;{0}Q"),       // F2
        [0x72] = ("\x1bOR", "\x1b[1;{0}R"),       // F3
        [0x73] = ("\x1bOS", "\x1b[1;{0}S"),       // F4
        [0x74] = ("\x1b[15~", "\x1b[15;{0}~"),    // F5
        [0x75] = ("\x1b[17~", "\x1b[17;{0}~"),    // F6
        [0x76] = ("\x1b[18~", "\x1b[18;{0}~"),    // F7
        [0x77] = ("\x1b[19~", "\x1b[19;{0}~"),    // F8
        [0x78] = ("\x1b[20~", "\x1b[20;{0}~"),    // F9
        [0x79] = ("\x1b[21~", "\x1b[21;{0}~"),    // F10
        [0x7A] = ("\x1b[23~", "\x1b[23;{0}~"),    // F11
        [0x7B] = ("\x1b[24~", "\x1b[24;{0}~"),    // F12
    };

    // What one key-down record contributes to the child's input, or "" for a key with no mapping. Returned
    // as text (not bytes) so the T2 batch is still transcoded to UTF-8 once, and a surrogate pair split
    // across records re-joins before it is encoded.
    public static string ForKeyDown(ushort vk, char ch, uint ctrlState)
    {
        bool shift = (ctrlState & SHIFT_PRESSED) != 0;
        bool alt = (ctrlState & (LEFT_ALT_PRESSED | RIGHT_ALT_PRESSED)) != 0;
        bool ctrl = (ctrlState & (LEFT_CTRL_PRESSED | RIGHT_CTRL_PRESSED)) != 0;

        // Backspace: a terminal sends DEL (0x7f); Ctrl+Backspace sends BS (0x08). The console hands us BS
        // for both, so we distinguish them here rather than pass 0x08 straight through (which a TUI reads
        // as "delete word"). Ctrl+Shift+Backspace stays DEL, matching WT.
        if (vk == VK_BACK) return (ctrl && !shift) ? "\x08" : "\x7f";

        if (ch != '\0')
        {
            if (vk == VK_TAB && shift) return "\x1b[Z";   // Shift+Tab = back-tab
            var text = ch.ToString();
            // Plain Alt+printable is the ESC-prefixed meta form; AltGr (Ctrl+Alt) stays a bare character.
            if (alt && !ctrl && ch >= ' ' && ch != '\x7f') return "\x1b" + text;
            return text;
        }

        // A key with no character: navigation and function keys. Fold the modifiers into xterm's
        // parameter (1 = none) so Ctrl/Shift/Alt+Arrow and friends arrive as the real sequence.
        if (!VkToVt.TryGetValue(vk, out var seq)) return string.Empty;
        int mod = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
        return mod == 1 ? seq.Plain : string.Format(seq.Mod, mod);
    }

    // Alt+numpad composition arrives as the character on the ALT key-UP record; every other key-up is a
    // release and contributes nothing.
    public static string ForAltNumpadKeyUp(ushort vk, char ch)
        => (vk == VK_MENU && ch != '\0') ? ch.ToString() : string.Empty;
}
