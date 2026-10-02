/*
 * MidiLink - BluetoothPresence.cs
 * Copyright (c) 2025 melho
 * Licensed under GPL v3
 * Description: Knows whether paired Bluetooth LE devices are switched on, without connecting to them.
 */
using System;
using System.Collections.Generic;
using System.Windows.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;

namespace MidiLink
{
    /// <summary>
    /// Windows keeps the MIDI ports of a paired Bluetooth device listed even when it is switched off, and
    /// opening such a port blocks the whole Windows MIDI stack for seconds (and can crash inside Windows).
    /// It also only connects the device when a port is opened. So presence is detected by listening to the
    /// device's advertisements (a switched-on, unconnected BLE device keeps advertising) plus its
    /// connection state. MIDI ports are matched to their Bluetooth device through the container id.
    /// </summary>
    public sealed class BluetoothPresence : IDisposable
    {
        private const string AepContainerId = "System.Devices.Aep.ContainerId";
        private const string AepAddress = "System.Devices.Aep.DeviceAddress";
        private const string AepIsConnected = "System.Devices.Aep.IsConnected";

        // Not seen advertising for this long (and not connected) = switched off or out of range
        private static readonly TimeSpan SeenTimeout = TimeSpan.FromSeconds(30);

        private sealed class Device
        {
            public string AepId = "";
            public Guid Container;
            public ulong Address;
            public bool Connected;
            public DateTime LastSeen = DateTime.MinValue;
            public bool WasReachable;
        }

        private readonly Dispatcher _dispatcher;
        private readonly object _lock = new();
        private readonly Dictionary<string, Device> _byAepId = new();
        private DeviceWatcher? _aepWatcher;
        private BluetoothLEAdvertisementWatcher? _adWatcher;
        private DispatcherTimer? _expiryTimer;

        public BluetoothPresence(Dispatcher dispatcher) => _dispatcher = dispatcher;

        /// <summary>
        /// Raised on the UI thread when a Bluetooth device becomes reachable or unreachable.
        /// </summary>
        public event Action? Changed;

        /// <summary>
        /// False when advertisements cannot be listened to (Bluetooth off, no adapter...):
        /// devices are then treated as reachable and MidiLink falls back to plain retries.
        /// </summary>
        public bool IsWorking => _adWatcher?.Status is BluetoothLEAdvertisementWatcherStatus.Started
                                                    or BluetoothLEAdvertisementWatcherStatus.Created;

        public void Start()
        {
            try
            {
                _aepWatcher = DeviceInformation.CreateWatcher(
                    BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
                    new[] { AepContainerId, AepAddress, AepIsConnected },
                    DeviceInformationKind.AssociationEndpoint);
                _aepWatcher.Added += (_, info) => OnAepAdded(info);
                _aepWatcher.Updated += (_, update) => OnAepUpdated(update);
                _aepWatcher.Removed += (_, update) => { lock (_lock) _byAepId.Remove(update.Id); };
                _aepWatcher.Start();

                _adWatcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
                _adWatcher.Received += (_, ad) => OnAdvertisement(ad.BluetoothAddress);
                _adWatcher.Stopped += (_, e) => AppLog.Info($"Bluetooth advertisement watcher stopped ({e.Error})");
                _adWatcher.Start();

                _expiryTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(5) };
                _expiryTimer.Tick += (_, __) => CheckChanges();
                _expiryTimer.Start();
            }
            catch (Exception ex)
            {
                AppLog.Error("Bluetooth presence detection unavailable", ex);
            }
        }

        /// <summary>
        /// True if this MIDI port's container is a paired Bluetooth LE device.
        /// </summary>
        public bool IsBluetooth(Guid? container)
        {
            if (container is not Guid c || c == Guid.Empty) return false;
            lock (_lock)
                foreach (var d in _byAepId.Values)
                    if (d.Container == c) return true;
            return false;
        }

        /// <summary>
        /// Whether a port of this device may be opened: false only when we know the Bluetooth device is off
        /// (not connected and not advertising). Opening it then would get stuck in Windows, block the whole
        /// MIDI stack, and that stuck open would also prevent the reconnection once it is switched back on.
        /// Non-Bluetooth devices, or when detection does not work, are always reachable.
        /// </summary>
        public bool IsReachable(Guid? container) => IsDetected(container);

        /// <summary>
        /// False when the Bluetooth device is known to be off. Used to close the ports of a device switched off.
        /// </summary>
        public bool IsDetected(Guid? container)
        {
            if (!IsWorking) return true;
            if (container is not Guid c || c == Guid.Empty) return true;

            bool known = false;
            lock (_lock)
            {
                foreach (var d in _byAepId.Values)
                {
                    if (d.Container != c) continue;
                    known = true;
                    if (Reachable(d)) return true;
                }
            }
            return !known;
        }


        private static bool Reachable(Device d) => d.Connected || DateTime.UtcNow - d.LastSeen < SeenTimeout;

        private void OnAepAdded(DeviceInformation info)
        {
            var d = new Device { AepId = info.Id };
            if (info.Properties.TryGetValue(AepContainerId, out var c) && c is Guid g) d.Container = g;
            if (info.Properties.TryGetValue(AepAddress, out var a) && a is string s) d.Address = ParseAddress(s);
            if (info.Properties.TryGetValue(AepIsConnected, out var conn) && conn is bool b) d.Connected = b;
            d.WasReachable = Reachable(d);
            lock (_lock) _byAepId[info.Id] = d;
        }

        private void OnAepUpdated(DeviceInformationUpdate update)
        {
            if (!update.Properties.TryGetValue(AepIsConnected, out var conn) || conn is not bool b) return;
            lock (_lock)
            {
                if (!_byAepId.TryGetValue(update.Id, out var d)) return;
                // Connection lost (switched off, even briefly): its open ports are dead. Forget the last
                // advertisement so it counts as off until it advertises again, then gets fresh ports.
                if (d.Connected && !b) d.LastSeen = DateTime.MinValue;
                d.Connected = b;
            }
            PostCheck();
        }

        private void OnAdvertisement(ulong address)
        {
            bool newlySeen = false;
            lock (_lock)
            {
                foreach (var d in _byAepId.Values)
                {
                    if (d.Address != address) continue;
                    d.LastSeen = DateTime.UtcNow;
                    newlySeen |= !d.WasReachable;
                }
            }
            if (newlySeen) PostCheck(); // switched on: reconnect right away
        }

        private void PostCheck()
        {
            try { _dispatcher.BeginInvoke(CheckChanges); } catch { }
        }

        /// <summary>
        /// UI thread: raises Changed if any device switched between reachable and unreachable.
        /// </summary>
        private void CheckChanges()
        {
            bool changed = false;
            lock (_lock)
            {
                foreach (var d in _byAepId.Values)
                {
                    bool now = Reachable(d);
                    if (now == d.WasReachable) continue;
                    d.WasReachable = now;
                    changed = true;
                    AppLog.Info($"Bluetooth device {d.Address:X12} {(now ? "detected (switched on)" : "no longer detected (off or out of range)")}");
                }
            }
            if (changed) Changed?.Invoke();
        }

        private static ulong ParseAddress(string s)
        {
            try { return Convert.ToUInt64(s.Replace(":", "").Replace("-", ""), 16); }
            catch { return 0; }
        }

        public void Dispose()
        {
            try { _expiryTimer?.Stop(); } catch { }
            try { _adWatcher?.Stop(); } catch { }
            try
            {
                if (_aepWatcher?.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                    _aepWatcher.Stop();
            }
            catch { }
        }
    }
}
