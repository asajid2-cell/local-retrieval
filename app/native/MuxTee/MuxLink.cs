using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MuxTee;

// The P2 net task: muxtee's link to the local muxd over its loopback websocket (ws://127.0.0.1:LOCAL_PORT).
//
// It registers this tab as a STREAM owner (spec section 6.2): muxd keeps the raw VT in a ring and reports
// kind "local-tab". Output travels as BINARY frames dispatched on the first byte (section 6.1):
//
//   0x01 <vt>   ingest        - append to the ring
//   0x03 <vt>   replace_history - clear the ring and ingest (used after a resync)
//
// Input from muxd arrives as {"t":"i","d":<base64>} and is written into the same InputQueue T3 drains,
// so a remote keystroke and a local keystroke take exactly the same path into the child.
//
// Everything here is fire-and-forget off T1: the send queue is bounded and drops its OLDEST frame on
// overflow, because this is a live screen, and the newest VT is the part that matters. T1 must never
// block on the network (invariant 1), so Enqueue only ever touches the queue and a wake.
//
// One socket, both directions. A muxd restart closes the socket, and the tab has to come back on its
// own: the receive half must survive a Close frame (it used to `return`, leaving the tab deaf), the send
// half must not block forever on a frame that an unwatched tab will never produce, and a reconnect must
// start from a clean slate. These are the whole of B1.
internal sealed class MuxLink : IDisposable
{
    private const byte KindIngest = 0x01;
    private const byte KindResync = 0x03;

    // Bounds one connect handshake. A muxd that accepts the TCP socket but never finishes the websocket
    // handshake must not wedge the send loop - the tab still has to re-register within a bounded time.
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    // The send loop waits for a frame, but never longer than this. An UNWATCHED tab produces no frames
    // at all (Enqueue skips the copy), so a bare wait would sleep straight through a muxd restart and the
    // tab would never notice the socket had died. The timeout just re-runs the loop, which re-checks the
    // socket state - that is how a dead socket is detected with no outbound frame to fail on.
    private static readonly TimeSpan ReconnectPoll = TimeSpan.FromSeconds(1);

    private readonly string _name;
    private readonly string _ownerKey;
    // Mutable: a reconnect must announce the tab's CURRENT size, not the one it was built with, so a tab
    // resized while the link was down comes back at the right dimensions. (B2 wires the resize path that
    // calls SetSize; today nothing changes it, so the reconnect hello equals the construction size.)
    private int _cols, _rows;
    private readonly string _uri;
    private readonly InputQueue _input;
    private readonly Action<string> _log;

    // Bounded so a slow or absent muxd cannot grow T1's memory. 1 MiB, matching the spec's net queue.
    private readonly ConcurrentQueue<byte[]> _out = new();
    private readonly SemaphoreSlim _outWake = new(0);
    private long _outBytes;
    private readonly long _outCap = 1024 * 1024;

    private readonly CancellationTokenSource _cts = new();
    private ClientWebSocket? _ws;
    private Task? _send, _recv;
    private volatile bool _registered;
    // spec section 6.5: muxd tells us when a relay viewer is actually watching this tab. Until then -
    // and again once the last viewer leaves - we skip the frame copy entirely, because bytes nobody is
    // looking at are pure allocation on the path a local keystroke has to share. Starts false: a fresh
    // link is unwatched until muxd says otherwise. A reconnect resets it, because a watch granted by the
    // muxd we just lost means nothing to the one that replaced it.
    private volatile bool _watched;

    public bool Watched => _watched;

    public bool Registered => _registered;

    // Fires on the transition into "registered at muxd", so the owner can push its ring as the initial
    // history. The child draws before muxd has accepted us, so those first frames are never enqueued -
    // the ring snapshot closes that gap. It also fires on every reconnect, so a tab that was watched
    // before a muxd restart comes back whole.
    public event Action? RegisteredChanged;

    // Fires on the rising edge of watched (spec section 6.5). While unwatched we skip sending, so muxd's
    // ring froze at the last watched moment; a fresh ring snapshot on this edge makes muxd's history equal
    // ours the instant a viewer attaches, which is what the attach's `sb` request then reads back.
    public event Action? WatchChanged;

    public MuxLink(string name, string ownerKey, int cols, int rows, string uri, InputQueue input, Action<string> log)
    {
        _name = name;
        _ownerKey = ownerKey;
        _cols = cols;
        _rows = rows;
        _uri = uri;
        _input = input;
        _log = log;
    }

    public static string DefaultUri()
        => "ws://127.0.0.1:" + (Environment.GetEnvironmentVariable("MUXTEE_LOCAL_PORT") ?? "7699");

    // The size the next hello reports. A resize calls this so a later reconnect announces the current
    // dimensions rather than the ones the link was constructed with.
    public void SetSize(int cols, int rows)
    {
        _cols = cols;
        _rows = rows;
    }

    public void Start()
    {
        _send = Task.Run(SendLoop);
        _recv = Task.Run(RecvLoop);
    }

    // Called from T1. Enqueue only - never blocks, never throws into the output path.
    public void Enqueue(byte[] chunk)
    {
        if (!_watched) return;   // spec section 6.5: nobody is looking; do not even copy
        if (chunk.Length == 0) return;
        var frame = new byte[chunk.Length + 1];
        frame[0] = KindIngest;
        Buffer.BlockCopy(chunk, 0, frame, 1, chunk.Length);
        _out.Enqueue(frame);
        var total = Interlocked.Add(ref _outBytes, frame.Length);
        // Drop the OLDEST frames until we are back under the cap. The tail of the stream is the screen.
        while (total > _outCap && _out.TryDequeue(out var dropped))
            total = Interlocked.Add(ref _outBytes, -dropped.Length);
        try { _outWake.Release(); } catch (SemaphoreFullException) { }
    }

    // Replace the ring wholesale (spec section 6.1, kind 0x03). Used when the local screen and the ring
    // have diverged - e.g. after a resize the ring never saw. Queued in order, so it lands after whatever
    // T1 already put ahead of it.
    // NOTE (B3): unlike Enqueue, this does NOT apply the 1 MiB cap - a resync snapshot is one frame that
    // has to land whole. B3 revisits whether a pathological ring size needs its own ceiling here.
    public void ReplaceHistory(byte[] snapshot)
    {
        var frame = new byte[snapshot.Length + 1];
        frame[0] = KindResync;
        Buffer.BlockCopy(snapshot, 0, frame, 1, snapshot.Length);
        _out.Enqueue(frame);
        Interlocked.Add(ref _outBytes, frame.Length);
        try { _outWake.Release(); } catch (SemaphoreFullException) { }
    }

    private async Task SendLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var ws = _ws;
                if (ws is null || ws.State != WebSocketState.Open)
                {
                    await Connect();
                    ws = _ws;
                }

                // Wait for a frame, but never indefinitely (see ReconnectPoll). Timing out is not an
                // error: it is the tick that lets an idle tab notice its socket is gone.
                await _outWake.WaitAsync(ReconnectPoll, _cts.Token);
                // Send on the socket captured for THIS iteration, never on _ws: the receive half can drop
                // the link and null _ws mid-send, and dereferencing it here would be a needless NRE.
                while (ws is not null && _out.TryDequeue(out var frame))
                {
                    Interlocked.Add(ref _outBytes, -frame.Length);
                    await ws.SendAsync(frame, WebSocketMessageType.Binary, true, _cts.Token);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log("mux link send loop: " + ex.Message);
                DropLink();
                try { await Task.Delay(1000, _cts.Token); } catch { break; }
            }
        }
    }

    private async Task Connect()
    {
        // A socket that is present but not Open is a corpse: only DropLink disposes the live one, so a
        // socket left half-open (say the receive half saw a Close first) would otherwise linger. Clear it
        // before dialing its replacement.
        var previous = _ws;
        if (previous is not null)
        {
            _ws = null;
            try { previous.Dispose(); } catch { }
        }

        var ws = new ClientWebSocket();
        // Bound the handshake only. Reusing this token for the established socket's I/O is the trap that
        // makes a live tab go deaf: the deadline fires mid-session and every receive throws.
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        attempt.CancelAfter(ConnectTimeout);
        try
        {
            await ws.ConnectAsync(new Uri(_uri), attempt.Token);

            // A fresh muxd knows nothing about us: the gate starts closed, and frames queued for the link
            // we just lost are stale screen, so drop them rather than replay them into the new ring.
            _watched = false;
            while (_out.TryDequeue(out var stale)) Interlocked.Add(ref _outBytes, -stale.Length);

            // A stream owner reports itself as a plain shell with no resolvable command identity (section
            // 6.3): muxtee relays a terminal that muxd may not relaunch, so it must not claim a writer
            // slot. cols/rows are read NOW, so a reconnect announces the tab's current size, not the
            // construction-time one.
            var hello = new
            {
                t = "owner",
                s = _name,
                ownerKey = _ownerKey,
                cmd = "",
                cols = _cols,
                rows = _rows,
                stream = true,
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(hello);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, attempt.Token);
        }
        catch
        {
            // The handshake or the hello never completed, so this socket is not the link: dispose it and
            // let the send loop's handler back off and retry. Leaving it undisposed leaks the socket.
            try { ws.Dispose(); } catch { }
            throw;
        }

        _ws = ws;
        _registered = true;
        _log("mux link registered stream owner " + _name + " at " + _uri);
        try { RegisteredChanged?.Invoke(); } catch (Exception ex) { _log("registered hook: " + ex.Message); }
    }

    // The one place a lost socket is recorded. Both loops call it, so whichever half sees the failure
    // first leaves the same clean state for the other to reconnect from.
    private void DropLink()
    {
        _registered = false;
        _watched = false;
        var ws = _ws;
        _ws = null;
        try { ws?.Dispose(); } catch { }
    }

    private async Task RecvLoop()
    {
        var buffer = new byte[8192];
        while (!_cts.IsCancellationRequested)
        {
            var ws = _ws;
            if (ws is null || ws.State != WebSocketState.Open)
            {
                try { await Task.Delay(200, _cts.Token); } catch { break; }
                continue;
            }
            try
            {
                using var ms = new System.IO.MemoryStream();
                WebSocketReceiveResult r;
                var closed = false;
                do
                {
                    r = await ws.ReceiveAsync(buffer, _cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) { closed = true; break; }
                    ms.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);

                // A Close frame ends THIS socket, not the loop. Null the link and go round, so the send
                // half dials a fresh one. `return` here is what used to leave a tab permanently deaf
                // after muxd restarted.
                if (closed) { DropLink(); continue; }

                if (r.MessageType != WebSocketMessageType.Text) continue;
                HandleText(ms.ToArray());
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log("mux link recv loop: " + ex.Message);
                DropLink();
                try { await Task.Delay(500, _cts.Token); } catch { break; }
            }
        }
    }

    private void HandleText(byte[] payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (!root.TryGetProperty("t", out var t)) return;
            switch (t.GetString())
            {
                case "i":
                    if (root.TryGetProperty("d", out var d) && d.ValueKind == JsonValueKind.String)
                    {
                        var data = Convert.FromBase64String(d.GetString()!);
                        if (data.Length > 0) _input.Enqueue(data);
                    }
                    break;
                case "watch":
                    // muxd has a relay viewer attached (spec section 6.5); start feeding the frame queue.
                    // The ring went stale while we were skipping, so hand back a fresh snapshot for muxd to
                    // serve the attach's `sb` from.
                    if (!_watched)
                    {
                        _watched = true;
                        try { WatchChanged?.Invoke(); } catch (Exception ex) { _log("watch hook: " + ex.Message); }
                    }
                    break;
                case "unwatch":
                    // Last viewer left. Stop copying, and drop whatever is queued so a reattach starts
                    // clean rather than replaying a burst of stale screen.
                    _watched = false;
                    while (_out.TryDequeue(out var stale)) Interlocked.Add(ref _outBytes, -stale.Length);
                    break;
                case "redraw":
                    // muxd saw the screen and its ring disagree; the caller re-sends history.
                    try { WatchChanged?.Invoke(); } catch (Exception ex) { _log("redraw hook: " + ex.Message); }
                    break;
                case "kill":
                    // Remote kill of a local tab is not wired in P2; the local terminal owns its lifetime.
                    _log("mux link received kill; ignoring for a stream owner");
                    break;
            }
        }
        catch (Exception ex)
        {
            _log("mux link bad frame: " + ex.Message);
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _ws?.Dispose(); } catch { }
        // A local write must never be delayed by the link going away, so this does not wait on the tasks.
    }
}
