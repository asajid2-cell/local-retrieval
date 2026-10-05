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
internal sealed class MuxLink : IDisposable
{
    private const byte KindIngest = 0x01;
    private const byte KindResync = 0x03;

    private readonly string _name;
    private readonly string _ownerKey;
    private readonly int _cols, _rows;
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
    // link is unwatched until muxd says otherwise.
    private volatile bool _watched;

    public bool Watched => _watched;

    public bool Registered => _registered;

    // Fires on the transition into "registered at muxd", so the owner can push its ring as the initial
    // history. The child draws before muxd has accepted us, so those first frames are never enqueued -
    // the ring snapshot closes that gap.
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
                if (_ws is null || _ws.State != WebSocketState.Open)
                {
                    await Connect();
                    if (!_registered) { await Task.Delay(1000, _cts.Token); continue; }
                }
                await _outWake.WaitAsync(_cts.Token);
                while (_out.TryDequeue(out var frame))
                {
                    Interlocked.Add(ref _outBytes, -frame.Length);
                    await _ws!.SendAsync(frame, WebSocketMessageType.Binary, true, _cts.Token);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log("mux link send loop: " + ex.Message);
                _registered = false;
                try { await Task.Delay(1000, _cts.Token); } catch { break; }
            }
        }
    }

    private async Task Connect()
    {
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(_uri), _cts.Token);
        // A stream owner reports itself as a plain shell with no resolvable command identity (section
        // 6.3): muxtee relays a terminal that muxd may not relaunch, so it must not claim a writer slot.
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
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token);
        _ws = ws;
        _registered = true;
        _log("mux link registered stream owner " + _name + " at " + _uri);
        try { RegisteredChanged?.Invoke(); } catch (Exception ex) { _log("registered hook: " + ex.Message); }
    }

    private async Task RecvLoop()
    {
        var buffer = new byte[8192];
        while (!_cts.IsCancellationRequested)
        {
            var ws = _ws;
            if (ws is null || ws.State != WebSocketState.Open)
            {
                try { await Task.Delay(500, _cts.Token); } catch { break; }
                continue;
            }
            try
            {
                using var ms = new System.IO.MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buffer, _cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) { _registered = false; return; }
                    ms.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);

                if (r.MessageType != WebSocketMessageType.Text) continue;
                HandleText(ms.ToArray());
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log("mux link recv loop: " + ex.Message);
                _registered = false;
                _ws = null;
                try { await Task.Delay(1000, _cts.Token); } catch { break; }
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
