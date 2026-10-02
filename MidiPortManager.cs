/*
 * MidiLink - MidiPortManager.cs
 * Copyright (c) 2025 melho
 * Licensed under GPL v3
 * Description: Opens each MIDI port once and shares it between all users (connections, keyboard).
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Windows.Devices.Midi;
using Windows.Foundation;

namespace MidiLink
{
    /// <summary>
    /// Opens each MIDI device at most once and hands out leases to it.
    /// Many drivers only allow one open handle per port, so two connections from the same IN,
    /// or the keyboard and a connection on the same OUT, must share a single port.
    /// A port is closed when its last lease is disposed, immediately when the device is unplugged,
    /// or when it fails (it is then reopened by its users).
    /// </summary>
    public sealed class MidiPortManager : IDisposable
    {
        /// <summary>
        /// Maximum time to wait for a MIDI device to open before giving up.
        /// Bluetooth LE devices can take several seconds, and Windows opens ports one at a time.
        /// </summary>
        public static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(10);

        private readonly object _sync = new();
        private readonly Dictionary<string, SharedPort> _ins = new();
        private readonly Dictionary<string, SharedPort> _outs = new();
        private volatile bool _shuttingDown;

        // Outputs lost while in use (failure or unplug): messages such as Note Off may have been lost,
        // so the device gets a reset (notes off, sustain off, pitch bend centered) when reopened.
        private readonly HashSet<string> _needsReset = new();

        // Outputs whose last send failed (device off but still listed, Bluetooth out of range...).
        // Kept across reopenings; cleared by the next successful send or when the device is unplugged.
        private readonly HashSet<string> _notResponding = new();

        /// <summary>
        /// Raised (on a background thread) when an open port stops working, e.g. a Bluetooth device
        /// that no longer answers. Its leases become dead; users should reacquire the port.
        /// </summary>
        public event Action<string>? PortFailed;

        /// <summary>
        /// Raised (on a background thread) when an output that was not responding sent a message successfully.
        /// </summary>
        public event Action<string>? DeviceRecovered;

        /// <summary>
        /// False if the last attempt to send to this output failed (and no send has succeeded since).
        /// </summary>
        public bool IsResponding(string? id)
        {
            if (id == null) return true;
            lock (_sync) return !_notResponding.Contains(id);
        }

        /// <summary>
        /// Gets a lease on an input port; <paramref name="handler"/> receives its messages on the MIDI thread.
        /// Throws if the device is missing, fails or does not answer in time.
        /// </summary>
        public async Task<InPortLease> AcquireInAsync(string id, Action<IMidiMessage> handler)
        {
            var entry = await AcquireAsync(_ins, id, isInput: true);

            // A distinct delegate per lease: two leases of the same link (overlapping updates) would
            // otherwise share an equal delegate, and disposing one would unsubscribe the other too
            // (the link then shows "Connected" but no message goes through)
            Action<IMidiMessage> subscription = message => handler(message);
            entry.AddSubscriber(subscription);
            return new InPortLease(this, entry, subscription);
        }

        /// <summary>
        /// Gets a lease on an output port. Throws if the device is missing, fails or does not answer in time.
        /// </summary>
        public async Task<OutPortLease> AcquireOutAsync(string id)
        {
            var entry = await AcquireAsync(_outs, id, isInput: false);
            return new OutPortLease(this, entry);
        }

        /// <summary>
        /// Closes the ports of a device that has been unplugged. Existing leases become dead (IsAlive = false).
        /// </summary>
        public void Invalidate(string id)
        {
            var removed = new List<SharedPort>();
            lock (_sync)
            {
                if (_ins.Remove(id, out var i)) removed.Add(i);
                if (_outs.Remove(id, out var o))
                {
                    removed.Add(o);
                    _needsReset.Add(id);
                }
                _notResponding.Remove(id); // now simply "not connected"; a replug starts fresh
            }
            foreach (var entry in removed)
                ClosePort(entry);
        }

        private async Task<SharedPort> AcquireAsync(Dictionary<string, SharedPort> map, string id, bool isInput)
        {
            SharedPort entry;
            lock (_sync)
            {
                if (_shuttingDown)
                    throw new ObjectDisposedException(nameof(MidiPortManager));

                // Reuse the port if it is already open (or being opened)
                if (!map.TryGetValue(id, out entry!))
                {
                    entry = new SharedPort(id, isInput, OnPortFailed, OnPortRecovered);
                    map[id] = entry;
                    entry.OpenTask = OpenAsync(entry);
                }
                entry.RefCount++;
            }

            try
            {
                await entry.OpenTask!;
            }
            catch
            {
                Release(entry);
                throw;
            }

            if (entry.IsDead)
            {
                Release(entry);
                throw new InvalidOperationException("MIDI device disconnected");
            }
            return entry;
        }

        private async Task OpenAsync(SharedPort entry)
        {
            try
            {
                if (entry.IsInput)
                {
                    var port = await OpenWithTimeoutAsync("IN:" + entry.Id, Guarded(() => MidiInPort.FromIdAsync(entry.Id)))
                        ?? throw new InvalidOperationException("MIDI input unavailable (used by another app?)");
                    port.MessageReceived += entry.OnMessageReceived;
                    if (!entry.SetInPort(port))
                        DisposeInPort(entry, port); // device removed while opening
                }
                else
                {
                    var port = await OpenWithTimeoutAsync("OUT:" + entry.Id, Guarded(() => MidiOutPort.FromIdAsync(entry.Id)))
                        ?? throw new InvalidOperationException("MIDI output unavailable (used by another app?)");
                    lock (_sync) entry.Suspect = _notResponding.Contains(entry.Id);
                    if (!entry.StartSender(port))
                    {
                        try { port.Dispose(); } catch { }
                    }
                    else
                    {
                        // Not sent now: a device that is off but still listed by Windows (Bluetooth)
                        // would block on it, fail and be reopened again and again. It goes out just
                        // before the first real message, i.e. only when the device is actually used.
                        bool reset;
                        lock (_sync) reset = _needsReset.Remove(entry.Id);
                        if (reset)
                            entry.SetPendingReset(ResetMessages().ToArray());
                    }
                }
                AppLog.Info($"Opened MIDI {(entry.IsInput ? "IN" : "OUT")} {entry.Id}");
            }
            catch (Exception ex)
            {
                AppLog.Error($"Cannot open MIDI {(entry.IsInput ? "IN" : "OUT")} {entry.Id}", ex);
                Forget(entry); // so the next attempt starts fresh
                entry.MarkDead();
                throw;
            }
        }

        // Native open operations that failed, returned nothing or answered late. They are never released:
        // crash dumps show Windows crashing (pure virtual call, in the finalizer thread) when such an
        // operation for an unresponsive Bluetooth device is destroyed. Keeping them costs a few bytes
        // each, and there is at most one per port per minute.
        private static readonly List<object> _keptOperations = new();

        /// <summary>
        /// Wraps a WinRT FromIdAsync call so that an operation which did not complete normally and quickly
        /// is kept alive forever instead of being destroyed by the garbage collector.
        /// </summary>
        private static Func<Task<T?>> Guarded<T>(Func<IAsyncOperation<T>> start) where T : class => async () =>
        {
            var started = Stopwatch.StartNew();
            var operation = start();
            bool clean = false;
            try
            {
                var result = await operation;
                clean = result != null && started.Elapsed < OpenTimeout;
                return result;
            }
            finally
            {
                if (!clean)
                {
                    lock (_keptOperations)
                    {
                        _keptOperations.Add(operation);
                        AppLog.Info($"Keeping a failed/slow MIDI open operation alive ({_keptOperations.Count} kept)");
                    }
                }
            }
        };

        // How long a port that finished opening after its timeout is kept for the next attempt
        private static readonly TimeSpan LateOpenKeep = TimeSpan.FromSeconds(30);

        // Native open calls still running or not yet claimed, per port ("IN:id" / "OUT:id").
        // Never two opens of the same port at once: a retry joins the pending one (a slow Bluetooth
        // device, e.g. after the PC wakes up, could otherwise get stacked concurrent opens).
        private readonly Dictionary<string, Task<IDisposable?>> _rawOpens = new();
        private readonly Dictionary<string, DateTime> _rawOpenStarted = new();

        // A native open stuck longer than this is given up (released if it ever finishes) and a new one
        // is allowed, so a device switched back on can still reconnect. Each new native open on a dead
        // Bluetooth device stalls the whole Windows MIDI stack for a moment (other links' messages wait),
        // so the delay doubles on each restart: 20 s, 40 s, 80 s... up to 10 min. Reset when the port opens.
        // (Switched-off Bluetooth devices are not opened at all anymore; a stuck open is typically a
        // device that has just been switched on, and a fresh open then succeeds right away.)
        private static readonly TimeSpan MinPendingOpenAge = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan MaxPendingOpenAge = TimeSpan.FromMinutes(10);
        private readonly Dictionary<string, int> _rawOpenRestarts = new();

        private TimeSpan StuckDelay(string key)
        {
            _rawOpenRestarts.TryGetValue(key, out int restarts);
            var delay = TimeSpan.FromTicks(MinPendingOpenAge.Ticks << Math.Min(restarts, 5));
            return delay < MaxPendingOpenAge ? delay : MaxPendingOpenAge;
        }

        /// <summary>
        /// Refresh button: allows an immediate new native open of ports stuck on an unresponsive device.
        /// </summary>
        public void ForceRetryStuckOpens()
        {
            lock (_sync)
            {
                _rawOpenRestarts.Clear();
                foreach (var key in _rawOpenStarted.Keys.ToList())
                    _rawOpenStarted[key] = DateTime.MinValue;
            }
        }

        /// <summary>
        /// Opens a port on a background thread so an unresponsive device cannot freeze the UI.
        /// If it takes longer than the timeout, the open keeps running: a later attempt reuses it,
        /// and a port that finally opens is kept for a while for that attempt, then released.
        /// </summary>
        private async Task<T?> OpenWithTimeoutAsync<T>(string key, Func<Task<T?>> open) where T : class, IDisposable
        {
            Task<IDisposable?> raw;
            lock (_sync)
            {
                _rawOpens.TryGetValue(key, out var pending);
                bool stuck = pending is { IsCompleted: false }
                             && DateTime.UtcNow - _rawOpenStarted[key] > StuckDelay(key);
                bool reusable = pending != null && !stuck
                                && (!pending.IsCompleted || (pending.IsCompletedSuccessfully && pending.Result != null));

                if (reusable)
                {
                    raw = pending!;
                    AppLog.Info($"Waiting for the pending open of {key}");
                }
                else
                {
                    if (stuck)
                    {
                        // Give up on it, but still release the port if it ever opens
                        AppLog.Info($"Open of {key} stuck for over {StuckDelay(key).TotalSeconds:0} s, trying again");
                        _rawOpenRestarts[key] = _rawOpenRestarts.GetValueOrDefault(key) + 1;
                        _ = pending!.ContinueWith(t =>
                        {
                            if (t.IsCompletedSuccessfully) { try { t.Result?.Dispose(); } catch { } }
                            else _ = t.Exception;
                        });
                    }
                    raw = Task.Run(async () => (IDisposable?)await open());
                    _rawOpens[key] = raw;
                    _rawOpenStarted[key] = DateTime.UtcNow;
                }
            }

            if (await Task.WhenAny(raw, Task.Delay(OpenTimeout)) != raw)
            {
                // Leave it running; if nobody claims the result in time, release it
                _ = raw.ContinueWith(async t =>
                {
                    await Task.Delay(LateOpenKeep);
                    bool owned;
                    lock (_sync)
                    {
                        owned = _rawOpens.TryGetValue(key, out var current) && current == t;
                        if (owned) _rawOpens.Remove(key);
                    }
                    if (!owned) return; // claimed by a later attempt
                    if (t.IsCompletedSuccessfully) { try { t.Result?.Dispose(); } catch { } }
                    else _ = t.Exception;
                });
                throw new TimeoutException("MIDI device not responding");
            }

            // Finished: claim the result so nobody else uses (or disposes) the same native port
            bool claimed;
            lock (_sync)
            {
                claimed = _rawOpens.TryGetValue(key, out var current) && current == raw;
                if (claimed) _rawOpens.Remove(key);
                if (raw.IsCompletedSuccessfully && raw.Result != null)
                    _rawOpenRestarts.Remove(key); // responding again: back to the short delay
            }
            var result = await raw; // rethrows if the open failed
            if (!claimed)
                throw new InvalidOperationException("MIDI port already taken by another attempt");
            return (T?)result;
        }

        /// <summary>
        /// An output stopped working (send failed): drop it so the next acquire opens a fresh port.
        /// </summary>
        private void OnPortFailed(SharedPort entry)
        {
            lock (_sync)
            {
                _needsReset.Add(entry.Id);
                _notResponding.Add(entry.Id);
            }
            Forget(entry);
            ClosePort(entry);
            try { PortFailed?.Invoke(entry.Id); }
            catch (Exception ex) { AppLog.Error("Error in PortFailed handler", ex); }
        }

        /// <summary>
        /// A send succeeded on an output that was marked as not responding.
        /// </summary>
        private void OnPortRecovered(SharedPort entry)
        {
            bool changed;
            lock (_sync) changed = _notResponding.Remove(entry.Id);
            if (!changed) return;

            AppLog.Info($"MIDI OUT {entry.Id} is responding again");
            try { DeviceRecovered?.Invoke(entry.Id); }
            catch (Exception ex) { AppLog.Error("Error in DeviceRecovered handler", ex); }
        }

        /// <summary>
        /// Brings a device back to a clean state on the 16 channels: sustain released, all notes off,
        /// pitch bend centered (the messages that restore them may have been lost with the connection).
        /// </summary>
        private static IEnumerable<IMidiMessage> ResetMessages()
        {
            for (byte ch = 0; ch < 16; ch++)
            {
                yield return new MidiControlChangeMessage(ch, 64, 0);  // Sustain pedal off
                yield return new MidiControlChangeMessage(ch, 123, 0); // All Notes Off
                yield return new MidiPitchBendChangeMessage(ch, 8192); // Pitch bend centered
            }
        }

        /// <summary>
        /// Removes the entry from its map if it is still the current one for its device.
        /// </summary>
        private void Forget(SharedPort entry)
        {
            lock (_sync)
            {
                var map = entry.IsInput ? _ins : _outs;
                if (map.TryGetValue(entry.Id, out var current) && current == entry)
                    map.Remove(entry.Id);
            }
        }

        /// <summary>
        /// Called when a lease is disposed: closes the port once nobody uses it anymore.
        /// </summary>
        internal void Release(SharedPort entry)
        {
            bool close = false;
            lock (_sync)
            {
                entry.RefCount--;
                if (entry.RefCount <= 0)
                {
                    var map = entry.IsInput ? _ins : _outs;
                    if (map.TryGetValue(entry.Id, out var current) && current == entry)
                        map.Remove(entry.Id);
                    close = true;
                }
            }
            if (close)
                ClosePort(entry);
        }

        private void ClosePort(SharedPort entry)
        {
            // Outputs: the sender finishes the queued messages (e.g. note offs) then disposes the port itself
            var inPort = entry.Close();
            if (inPort == null) return;

            AppLog.Info($"Closing MIDI IN {entry.Id}");
            DisposeInPort(entry, inPort);
        }

        /// <summary>
        /// Releases a native input port. On a background thread (an unresponsive device can block Dispose),
        /// except during shutdown where a late background release could run after COM is torn down.
        /// </summary>
        private void DisposeInPort(SharedPort entry, MidiInPort inPort)
        {
            void Release()
            {
                try { inPort.MessageReceived -= entry.OnMessageReceived; } catch (Exception ex) { AppLog.Error("Error detaching MIDI event", ex); }
                try { inPort.Dispose(); } catch (Exception ex) { AppLog.Error("Error disposing MIDI IN", ex); }
            }

            if (_shuttingDown)
                Release();
            else
                Task.Run(Release);
        }

        /// <summary>
        /// Closes every port (application shutdown). Waits briefly for outputs to flush and close.
        /// </summary>
        public void Dispose()
        {
            List<SharedPort> all;
            lock (_sync)
            {
                _shuttingDown = true;
                all = _ins.Values.Concat(_outs.Values).ToList();
                _ins.Clear();
                _outs.Clear();
            }
            foreach (var entry in all)
                ClosePort(entry);
            foreach (var entry in all)
                entry.WaitSenderStopped(TimeSpan.FromMilliseconds(500));
        }
    }

    /// <summary>
    /// One opened device, shared by all its leases.
    /// Outputs have a queue and a sender task: whoever sends (UI thread for the keyboard, MIDI thread
    /// for links) only enqueues, so a slow or stuck device (Bluetooth) never blocks them. The sender
    /// is also the only one to touch and finally dispose the native port, so it is never freed while in use.
    /// </summary>
    internal sealed class SharedPort
    {
        // Enough for bursts; if a device is stuck the oldest messages are dropped instead of growing forever
        private const int QueueCapacity = 4096;

        // A message queued longer than this (device was slow) is "late": played now it would be useless
        // or disturbing, so the kinds of messages where that is true are dropped instead of sent in a burst
        private static readonly long MaxLatencyTicks = Stopwatch.Frequency / 4; // 250 ms

        private readonly object _portLock = new();
        private readonly Action<SharedPort> _onFailed;
        private readonly Action<SharedPort> _onRecovered;
        private MidiInPort? _in;
        private Channel<QueuedMessage>? _queue;
        private Task? _sender;
        private volatile bool _dead;
        private Action<IMidiMessage>[] _subscribers = Array.Empty<Action<IMidiMessage>>();
        private IMidiMessage[]? _pendingReset;

        public SharedPort(string id, bool isInput, Action<SharedPort> onFailed, Action<SharedPort>? onRecovered = null)
        {
            Id = id;
            IsInput = isInput;
            _onFailed = onFailed;
            _onRecovered = onRecovered ?? (_ => { });
        }

        /// <summary>
        /// True while this output's device is known as not responding: the first successful send reports recovery.
        /// </summary>
        public volatile bool Suspect;

        public string Id { get; }
        public bool IsInput { get; }
        public Task? OpenTask { get; set; }
        public int RefCount { get; set; } // guarded by MidiPortManager._sync
        public bool IsDead => _dead;

        /// <summary>
        /// Stores the opened input. Returns false if the entry died meanwhile (caller must dispose the port).
        /// </summary>
        public bool SetInPort(MidiInPort port)
        {
            lock (_portLock)
            {
                if (_dead) return false;
                _in = port;
                return true;
            }
        }

        /// <summary>
        /// Starts the sender of an opened output. Returns false if the entry died meanwhile.
        /// </summary>
        public bool StartSender(IMidiOutPort port)
        {
            lock (_portLock)
            {
                if (_dead) return false;
                _queue = Channel.CreateBounded<QueuedMessage>(new BoundedChannelOptions(QueueCapacity)
                {
                    SingleReader = true,
                    FullMode = BoundedChannelFullMode.DropOldest
                });
                var reader = _queue.Reader;
                _sender = Task.Run(() => RunSender(port, reader));
                return true;
            }
        }

        private async Task RunSender(IMidiOutPort port, ChannelReader<QueuedMessage> reader)
        {
            try
            {
                var batch = new List<QueuedMessage>();
                var lastIndexByKey = new Dictionary<int, int>();

                // Runs until the queue is closed and drained (normal close) or the device fails.
                // Takes everything waiting at once, so late messages can be compared with newer ones.
                while (await reader.WaitToReadAsync())
                {
                    batch.Clear();
                    lastIndexByKey.Clear();
                    while (reader.TryRead(out var queued))
                    {
                        if (CoalesceKey(queued.Message) is int key)
                            lastIndexByKey[key] = batch.Count;
                        batch.Add(queued);
                    }

                    int dropped = 0;
                    for (int i = 0; i < batch.Count; i++)
                    {
                        var message = batch[i].Message;
                        if (Stopwatch.GetTimestamp() - batch[i].QueuedAt > MaxLatencyTicks)
                        {
                            // Late: skip what is useless now, and continuous values replaced by a newer one
                            // (only the last pitch bend / CC value... matters, e.g. the bend back to center)
                            bool superseded = CoalesceKey(message) is int key && lastIndexByKey[key] != i;
                            if (IsUselessWhenLate(message) || superseded)
                            {
                                dropped++;
                                continue;
                            }
                        }

                        try
                        {
                            port.SendMessage(message);
                            if (Suspect)
                            {
                                Suspect = false;
                                _onRecovered(this);
                            }
                        }
                        catch (Exception ex)
                        {
                            // e.g. 0x80070079 semaphore timeout on a Bluetooth device: the port is unusable now
                            AppLog.Error($"MIDI OUT {Id} stopped working", ex);
                            MarkDead();
                            _onFailed(this);
                            return; // finally disposes the port
                        }
                    }

                    if (dropped > 0)
                        AppLog.Info($"MIDI OUT {Id} was slow: {dropped} late message(s) skipped");
                }
            }
            catch (Exception ex)
            {
                AppLog.Error($"MIDI OUT {Id} sender error", ex);
            }
            finally
            {
                AppLog.Info($"Closing MIDI OUT {Id}");
                try { port.Dispose(); } catch (Exception ex) { AppLog.Error("Error disposing MIDI OUT", ex); }
            }
        }

        /// <summary>
        /// Events that only make sense on time: a late note or clock tick is noise.
        /// State messages are never skipped: Note Off, Program Change, SysEx, Start / Stop / Continue /
        /// Song Position (a skipped Start would leave a synced sequencer stopped). A Note Off whose
        /// Note On was skipped is harmless.
        /// </summary>
        private static bool IsUselessWhenLate(IMidiMessage message) => message.Type switch
        {
            MidiMessageType.NoteOn => ((MidiNoteOnMessage)message).Velocity > 0, // velocity 0 is a Note Off
            MidiMessageType.TimingClock or MidiMessageType.ActiveSensing => true,
            _ => false
        };

        /// <summary>
        /// Continuous values: when late, only the newest one per channel (and controller / note) is sent.
        /// Returns null for other messages.
        /// </summary>
        private static int? CoalesceKey(IMidiMessage message) => message.Type switch
        {
            MidiMessageType.ControlChange when message is MidiControlChangeMessage cc
                => 1 << 16 | cc.Channel << 8 | cc.Controller,
            MidiMessageType.PitchBendChange when message is MidiPitchBendChangeMessage pb
                => 2 << 16 | pb.Channel << 8,
            MidiMessageType.ChannelPressure when message is MidiChannelPressureMessage cp
                => 3 << 16 | cp.Channel << 8,
            MidiMessageType.PolyphonicKeyPressure when message is MidiPolyphonicKeyPressureMessage pp
                => 4 << 16 | pp.Channel << 8 | pp.Note,
            _ => null
        };

        /// <summary>
        /// Marks the entry closed. Returns the input port to dispose (outputs are disposed by their sender
        /// once the messages already queued have been sent).
        /// </summary>
        public MidiInPort? Close()
        {
            lock (_portLock)
            {
                _dead = true;
                _queue?.Writer.TryComplete();
                var inPort = _in;
                _in = null;
                return inPort;
            }
        }

        public void MarkDead()
        {
            lock (_portLock)
            {
                _dead = true;
                _queue?.Writer.TryComplete();
            }
        }

        public void WaitSenderStopped(TimeSpan timeout)
        {
            try { _sender?.Wait(timeout); } catch { }
        }

        /// <summary>
        /// Messages to send before the next one (reset after a connection loss).
        /// </summary>
        public void SetPendingReset(IMidiMessage[] messages) => Volatile.Write(ref _pendingReset, messages);

        /// <summary>
        /// Queues a message for the output. Never blocks. Returns false if the port is closed.
        /// </summary>
        public bool Send(IMidiMessage message)
        {
            if (_dead || _queue == null) return false;

            if (Interlocked.Exchange(ref _pendingReset, null) is IMidiMessage[] reset)
            {
                AppLog.Info($"Resetting MIDI OUT {Id} after a connection loss");
                foreach (var m in reset)
                    Enqueue(m);
            }
            return Enqueue(message);
        }

        private bool Enqueue(IMidiMessage message) =>
            _queue!.Writer.TryWrite(new QueuedMessage(message, Stopwatch.GetTimestamp()));

        public void AddSubscriber(Action<IMidiMessage> handler)
        {
            lock (_portLock) _subscribers = _subscribers.Append(handler).ToArray();
        }

        public void RemoveSubscriber(Action<IMidiMessage> handler)
        {
            // Exact instance only (delegate equality would also match other leases of the same handler)
            lock (_portLock) _subscribers = _subscribers.Where(h => !ReferenceEquals(h, handler)).ToArray();
        }

        /// <summary>
        /// MIDI thread: forwards the message to every subscriber (fan-out of a shared input).
        /// </summary>
        public void OnMessageReceived(MidiInPort sender, MidiMessageReceivedEventArgs args)
        {
            if (_dead) return;

            IMidiMessage message;
            try { message = args.Message; }
            catch (Exception ex) { AppLog.Error("Error reading MIDI message", ex); return; }

            foreach (var handler in Volatile.Read(ref _subscribers))
            {
                try { handler(message); }
                catch (Exception ex) { AppLog.Error("Error in MIDI handler", ex); }
            }
        }
    }

    /// <summary>
    /// A message waiting to be sent, with the time it was queued.
    /// </summary>
    internal readonly record struct QueuedMessage(IMidiMessage Message, long QueuedAt);

    /// <summary>
    /// Right to use a shared port. Dispose it to give it back.
    /// </summary>
    public abstract class PortLease : IDisposable
    {
        private readonly MidiPortManager _manager;
        private int _disposed;

        internal PortLease(MidiPortManager manager, SharedPort entry)
        {
            _manager = manager;
            Entry = entry;
        }

        internal SharedPort Entry { get; }

        /// <summary>
        /// Device ID of the leased port.
        /// </summary>
        public string DeviceId => Entry.Id;

        /// <summary>
        /// False once disposed, or when the device has been unplugged or has failed.
        /// </summary>
        public bool IsAlive => Volatile.Read(ref _disposed) == 0 && !Entry.IsDead;

        protected bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        protected virtual void OnDispose() { }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            OnDispose();
            _manager.Release(Entry);
        }
    }

    /// <summary>
    /// Lease on an input port; its handler receives the port's messages until disposed.
    /// </summary>
    public sealed class InPortLease : PortLease
    {
        private readonly Action<IMidiMessage> _handler;

        internal InPortLease(MidiPortManager manager, SharedPort entry, Action<IMidiMessage> handler)
            : base(manager, entry) => _handler = handler;

        protected override void OnDispose() => Entry.RemoveSubscriber(_handler);
    }

    /// <summary>
    /// Lease on an output port.
    /// </summary>
    public sealed class OutPortLease : PortLease
    {
        internal OutPortLease(MidiPortManager manager, SharedPort entry) : base(manager, entry) { }

        /// <summary>
        /// Queues a message for the device; never blocks. Returns false if the lease or the port is closed.
        /// </summary>
        public bool Send(IMidiMessage message) => !IsDisposed && Entry.Send(message);
    }
}
