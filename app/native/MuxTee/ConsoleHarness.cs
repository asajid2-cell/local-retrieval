using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace MuxTee;

// Run muxtee.exe under a REAL console so the tee path (which requires stdin/stdout to be a console) can
// be exercised. pywinpty is how the P0 spikes did this; there is no in-proc way to hand a child a console
// from a console-less test host (AttachConsole/AllocConsole both fail here - measured in P0).
//
// The harness speaks the same protocol the spikes used: it answers the inner ConPTY's cursor-position
// request (ESC[6n -> ESC[<row>;<col>R), which INHERIT_CURSOR blocks on. A real Windows Terminal answers
// that itself; pywinpty does not, so the harness stands in for the terminal here.
internal sealed class ConsoleHarness : IDisposable
{
    private readonly Process _py;
    private readonly StringBuilder _output = new();
    private readonly object _gate = new();
    private readonly bool _answerCpr;
    private volatile int _reportedExit = int.MinValue;
    private long _bytes;

    private ConsoleHarness(Process py, bool answerCpr)
    {
        _py = py;
        _answerCpr = answerCpr;
    }

    public string Output { get { lock (_gate) return _output.ToString(); } }

    // Raw bytes delivered from the pty. A pty drops when the reader falls behind, so throughput has to be
    // judged on bytes actually delivered over time, not on wall time alone - a fast, lossy run reads as
    // bloated throughput otherwise.
    public long BytesDelivered { get { lock (_gate) return _bytes; } }

    // Drive the driver script from a temp file: pywinpty reads muxtee's stdout on its own thread and
    // echoes it to this process's stdout in a machine-readable envelope, answering CPR on the way.
    // `direct: true` runs the command straight in the pty with nothing in between - the baseline for the
    // throughput comparison, not a muxtee test.
    public static ConsoleHarness Start(string muxteeExe, IReadOnlyList<string> argv, int rows, int cols,
        bool answerCpr = true, IDictionary<string, string>? extraEnv = null, bool direct = false)
    {
        var script = Path.Combine(Path.GetTempPath(), "muxtee-harness-" + Guid.NewGuid().ToString("N") + ".py");
        File.WriteAllText(script, DriverScript, Encoding.UTF8);

        var psi = new ProcessStartInfo("python")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(muxteeExe);
        psi.ArgumentList.Add(rows.ToString());
        psi.ArgumentList.Add(cols.ToString());
        psi.ArgumentList.Add(answerCpr ? "1" : "0");
        psi.ArgumentList.Add(direct ? "direct" : "muxtee");
        psi.ArgumentList.Add("--");
        foreach (var a in argv) psi.ArgumentList.Add(a);
        if (extraEnv != null)
            foreach (var kv in extraEnv) psi.Environment[kv.Key] = kv.Value;

        var py = Process.Start(psi) ?? throw new InvalidOperationException("could not start python harness");
        var harness = new ConsoleHarness(py, answerCpr);
        py.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            // Driver envelope: "OUT:" is base64 of raw pty bytes; "EXIT:" is the child's exit status.
            if (e.Data.StartsWith("OUT:", StringComparison.Ordinal))
            {
                var bytes = Convert.FromBase64String(e.Data[4..]);
                lock (harness._gate)
                {
                    harness._output.Append(Encoding.UTF8.GetString(bytes));
                    harness._bytes += bytes.Length;
                }
            }
            else if (e.Data.StartsWith("EXIT:", StringComparison.Ordinal))
            {
                // The muxtee child is gone; the python host may linger on stdin, so the child's exit is
                // the liveness signal the tests wait on, not our host process.
                harness._reportedExit = int.Parse(e.Data[5..]);
            }
        };
        py.ErrorDataReceived += (_, _) => { };
        py.BeginOutputReadLine();
        py.BeginErrorReadLine();
        return harness;
    }

    public void Write(string text)
    {
        try
        {
            _py.StandardInput.Write("IN:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) + "\n");
            _py.StandardInput.Flush();
        }
        catch { }
    }

    public bool WaitForOutput(Func<string, bool> predicate, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (predicate(Output)) return true;
            Thread.Sleep(50);
        }
        return predicate(Output);
    }

    // Time until `target` raw bytes have arrived from the pty, or null if the timeout passed first. This is
    // how throughput has to be read: the pty sheds output once the child leaves, so both arms of a
    // throughput comparison end up short of the file, and the wall clock then measures the shedding, not
    // the stream. Time-to-N-bytes is a true rate, and it is the same N for both arms, so the ratio is fair.
    public double? WaitForBytes(long target, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            lock (_gate) if (_bytes >= target) return sw.Elapsed.TotalSeconds;
            Thread.Sleep(10);
        }
        lock (_gate) return _bytes >= target ? sw.Elapsed.TotalSeconds : null;
    }

    // The child's own exit status, reported by the driver. -1 while still running (a real child exit code
    // is always >= 0), so HasExited is "did the driver tell us" rather than "did python leave".
    public int ExitCode => _reportedExit;
    public bool HasExited => _reportedExit != int.MinValue;

    public void Dispose()
    {
        try { if (!_py.HasExited) _py.Kill(entireProcessTree: true); } catch { }
        _py.Dispose();
    }

    // The driver: spawn muxtee under pywinpty, pump its output as base64 lines, take input lines from our
    // stdin, and answer ESC[6n so INHERIT_CURSOR unblocks.
    private const string DriverScript = @"
import sys, base64, time, threading
import winpty

exe, rows, cols, answer = sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), sys.argv[4] == '1'
mode = sys.argv[5]
argv = sys.argv[7:] if len(sys.argv) > 6 and sys.argv[6] == '--' else []
# direct: the command runs in the pty with nothing in between, so argv[0] is the image itself.
spawn_argv = argv if mode == 'direct' else [exe] + argv
p = winpty.PtyProcess.spawn(spawn_argv, dimensions=(rows, cols))
stop = threading.Event()

def pump():
    answered = not answer
    while not stop.is_set():
        try:
            d = p.read(4096)
        except Exception:
            break
        if not d:
            time.sleep(0.01); continue
        sys.stdout.write('OUT:' + base64.b64encode(d.encode('utf-8', 'surrogatepass')).decode('ascii') + '\n')
        sys.stdout.flush()
        if not answered and '\x1b[6n' in d:
            try: p.write('\x1b[1;1R'); answered = True
            except Exception: pass

threading.Thread(target=pump, daemon=True).start()

def watch_exit():
    # Wait for the muxtee child to leave, then report its status so the test host can stop waiting on us.
    try:
        p.wait()
        code = p.exitstatus if p.exitstatus is not None else -1
    except Exception:
        code = -1
    sys.stdout.write('EXIT:%d\n' % code)
    sys.stdout.flush()

threading.Thread(target=watch_exit, daemon=True).start()

for line in sys.stdin:
    line = line.strip()
    if line.startswith('IN:'):
        try: p.write(base64.b64decode(line[3:]).decode('utf-8', 'surrogatepass'))
        except Exception: pass
stop.set()
try: p.terminate(force=True)
except Exception: pass
";
}
