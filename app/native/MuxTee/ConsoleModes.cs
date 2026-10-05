using System;
using Microsoft.Win32.SafeHandles;

namespace MuxTee;

// Save the tab's console modes, put them into the shape ConPTY needs, and RESTORE them on every exit
// path. §5.1 step 2 + §12 P1: normal exit, child crash, Ctrl-Break, and taskkill /F of the child all have
// to leave the console as they found it.
//
// The restore is idempotent and is registered with AppDomain.ProcessExit and (for the fatal-severity
// cases) SetConsoleCtrlHandler, so a kill that reaches muxtee still unwinds cleanly.
internal sealed class ConsoleModes
{
    public SafeFileHandle Stdin { get; }
    public SafeFileHandle Stdout { get; }

    private uint _inOriginal, _inChanged, _outOriginal, _outChanged;
    private bool _haveIn, _haveOut, _restored;

    // Code pages are process-global, not per-handle, so these are saved and restored alongside the modes.
    private uint _outCpOriginal, _inCpOriginal;
    private bool _haveOutCp, _haveInCp;

    public ConsoleModes(SafeFileHandle stdin, SafeFileHandle stdout)
    {
        Stdin = stdin;
        Stdout = stdout;
    }

    public void Capture()
    {
        if (ConsoleApi.GetConsoleMode(Stdout, out _outOriginal))
        {
            _haveOut = true;
            var next = _outOriginal
                       | ConsoleApi.ENABLE_PROCESSED_OUTPUT
                       | ConsoleApi.ENABLE_VIRTUAL_TERMINAL_PROCESSING
                       | ConsoleApi.DISABLE_NEWLINE_AUTO_RETURN;
            ConsoleApi.SetConsoleMode(Stdout, next);
            _outChanged = _outOriginal ^ next;
        }

        if (ConsoleApi.GetConsoleMode(Stdin, out _inOriginal))
        {
            _haveIn = true;
            // VT input mode is what carries win32-input-mode sequences (Shift+Enter and friends) intact.
            // Clear LINE/ECHO/PROCESSED/QUICK_EDIT: the child owns line editing, and QuickEdit would
            // freeze us on a stray click in the tab.
            var next = (_inOriginal
                        | ConsoleApi.ENABLE_VIRTUAL_TERMINAL_INPUT
                        | ConsoleApi.ENABLE_WINDOW_INPUT
                        | ConsoleApi.ENABLE_EXTENDED_FLAGS)
                       & ~(ConsoleApi.ENABLE_LINE_INPUT
                           | ConsoleApi.ENABLE_ECHO_INPUT
                           | ConsoleApi.ENABLE_PROCESSED_INPUT
                           | ConsoleApi.ENABLE_QUICK_EDIT_MODE);
            ConsoleApi.SetConsoleMode(Stdin, next);
            _inChanged = _inOriginal ^ next;
        }

        // The tab is our outer console and we pass the inner pty's bytes through verbatim, so the console
        // has to be UTF-8 for a bullet to stay one cell. A zero reading means there is no console to set.
        _outCpOriginal = ConsoleApi.GetConsoleOutputCP();
        if (_outCpOriginal != 0 && _outCpOriginal != ConsoleApi.CP_UTF8)
            _haveOutCp = ConsoleApi.SetConsoleOutputCP(ConsoleApi.CP_UTF8);

        _inCpOriginal = ConsoleApi.GetConsoleCP();
        if (_inCpOriginal != 0 && _inCpOriginal != ConsoleApi.CP_UTF8)
            _haveInCp = ConsoleApi.SetConsoleCP(ConsoleApi.CP_UTF8);
    }

    // Swap back only the bits we changed, so atexit machinery another process installed meanwhile is not
    // clobbered. Mirrors muxrun's restore_mode_bits.
    public void Restore()
    {
        if (_restored) return;
        _restored = true;

        try
        {
            if (_haveOut && ConsoleApi.GetConsoleMode(Stdout, out var curOut))
                ConsoleApi.SetConsoleMode(Stdout, (curOut & ~_outChanged) | (_outOriginal & _outChanged));
            if (_haveIn && ConsoleApi.GetConsoleMode(Stdin, out var curIn))
                ConsoleApi.SetConsoleMode(Stdin, (curIn & ~_inChanged) | (_inOriginal & _inChanged));

            // Only the code pages we actually moved are put back, so a page another process set meanwhile
            // is left alone - the same discipline the mode restore above uses.
            if (_haveOutCp) ConsoleApi.SetConsoleOutputCP(_outCpOriginal);
            if (_haveInCp) ConsoleApi.SetConsoleCP(_inCpOriginal);
        }
        catch
        {
            // Restoring is best-effort: a console already gone is not an error worth surfacing.
        }
    }

    // Ctrl+C is the child's, not ours: tell the OS muxtee ignores it, so a ^C typed in the tab reaches
    // the child through the pipe instead of killing the tee. Ctrl-Break and Ctrl-Close still come
    // through so we can restore modes before we go.
    public void InstallCtrlHandler()
    {
        ConsoleApi.SetConsoleCtrlHandler(OnCtrl, true);
    }

    private bool OnCtrl(uint ctrlType)
    {
        const uint CTRL_C_EVENT = 0;

        if (ctrlType == CTRL_C_EVENT)
            return true; // "handled" - do not let the OS deliver it to us; the child gets it via the pipe.

        // For break/close/logoff/shutdown we are being torn down. Restore before returning FALSE so the
        // default handler runs and the process actually ends.
        Restore();
        return false;
    }
}
