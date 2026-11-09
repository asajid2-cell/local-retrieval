"""A full-screen TUI stand-in: alt screen, mouse tracking (1000/1002/1006), and a status line that
redraws four times a second. It reproduces what Claude Code looks like on the wire - an armed alt-screen
app whose screen is rewritten constantly - without needing a model, so the viewer's chip can be driven
against the shape that actually misbehaves.
"""
import os
import sys
import time

if os.name == "nt":
    import ctypes
    import msvcrt  # noqa: F401  (keeps the console in cooked mode until we switch)

    k = ctypes.windll.kernel32
    k.CreateFileW.restype = ctypes.c_void_p
    out = k.CreateFileW('CONOUT$', 0xC0000000, 3, None, 3, 0, None)
    mode = ctypes.c_uint()
    if k.GetConsoleMode(out, ctypes.byref(mode)):
        k.SetConsoleMode(out, mode.value | 0x0004)   # ENABLE_VIRTUAL_TERMINAL_PROCESSING


def write(text):
    sys.stdout.write(text)
    sys.stdout.flush()


def main():
    # Alt screen, mouse reporting on (what an interactive TUI does), cursor hidden.
    write('\x1b[?1049h\x1b[?1000h\x1b[?1002h\x1b[?1006h\x1b[?25l')
    n = 0
    try:
        while True:
            n += 1
            write('\x1b[H\x1b[2J')
            write('TUI SIM - an armed alt-screen app\r\n')
            write('tick=%d\r\n' % n)
            sys.stdout.write('\x1b[?1006h')
            write('clicks are reported below:\r\n')
            if n % 4 == 0:
                write('\r\n')
            time.sleep(0.25)
    except KeyboardInterrupt:
        pass
    finally:
        write('\x1b[?1000l\x1b[?1002l\x1b[?1006l\x1b[?25h\x1b[?1049l')


if __name__ == '__main__':
    main()
