using System;
using System.Collections.Generic;

namespace MuxTee;

// A console KEY_EVENT (key-down) -> the string a stock terminal sends for that key.
//
// T2 used to forward ONLY k.UnicodeChar, which is '\0' for every key that carries no character: the
// arrows, Home/End, Insert/Delete, PageUp/PageDown and Shift+Tab were dropped before they ever reached
// the child, so up-arrow never recalled history. This mirrors muxctl.ConsoleInputTranslator /
// translate_key_event - the proven table the local attach already uses - so the tee and the local attach
// hand the hosted app identical bytes.
internal static class KeyTranslator
{
    // dwControlKeyState bits (wincon.h).
    public const uint RIGHT_ALT_PRESSED = 0x0001;
    public const uint LEFT_ALT_PRESSED = 0x0002;
    public const uint RIGHT_CTRL_PRESSED = 0x0004;
    public const uint LEFT_CTRL_PRESSED = 0x0008;
    public const uint SHIFT_PRESSED = 0x0010;

    private const ushort VK_TAB = 0x09;

    // Alt. Alt+numpad composition delivers its character on this key's key-UP record, not on key-down.
    public const ushort VK_MENU = 0x12;

    // Navigation keys (virtual-key code) -> VT sequences. The bytes are exactly what muxctl emits for the
    // same keys, so key handling is identical across the tee and the local attach. (readline and
    // PSReadLine accept both the CSI and SS3 forms, so DECCKM's application-cursor encoding is not needed
    // to make these work.)
    private static readonly Dictionary<ushort, string> VkToVt = new()
    {
        [0x26] = "\x1b[A",    // Up
        [0x28] = "\x1b[B",    // Down
        [0x27] = "\x1b[C",    // Right
        [0x25] = "\x1b[D",    // Left
        [0x24] = "\x1b[H",    // Home
        [0x23] = "\x1b[F",    // End
        [0x21] = "\x1b[5~",   // PageUp
        [0x22] = "\x1b[6~",   // PageDown
        [0x2E] = "\x1b[3~",   // Delete
        [0x2D] = "\x1b[2~",   // Insert
    };

    // What one key-down record contributes to the child's input, or "" for a key with no mapping. Returned
    // as text (not bytes) so the T2 batch is still transcoded to UTF-8 once, and a surrogate pair split
    // across records re-joins before it is encoded.
    public static string ForKeyDown(ushort vk, char ch, uint ctrlState)
    {
        if (ch != '\0')
        {
            if (vk == VK_TAB && (ctrlState & SHIFT_PRESSED) != 0) return "\x1b[Z";   // Shift+Tab = back-tab
            var text = ch.ToString();
            bool alt = (ctrlState & (LEFT_ALT_PRESSED | RIGHT_ALT_PRESSED)) != 0;
            bool ctrl = (ctrlState & (LEFT_CTRL_PRESSED | RIGHT_CTRL_PRESSED)) != 0;
            // Plain Alt+printable is the ESC-prefixed meta form; AltGr (Ctrl+Alt) stays a bare character.
            if (alt && !ctrl && ch >= ' ' && ch != '\x7f') return "\x1b" + text;
            return text;
        }
        return VkToVt.TryGetValue(vk, out var seq) ? seq : string.Empty;
    }

    // Alt+numpad composition arrives as the character on the ALT key-UP record; every other key-up is a
    // release and contributes nothing.
    public static string ForAltNumpadKeyUp(ushort vk, char ch)
        => (vk == VK_MENU && ch != '\0') ? ch.ToString() : string.Empty;
}
