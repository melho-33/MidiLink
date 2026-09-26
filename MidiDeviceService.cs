/*
 * MidiLink - MidiDeviceService.cs
 * Copyright (c) 2025 melho
 * Licensed under GPL v3
 * Description: Keeps the lists of MIDI devices up to date (plug / unplug detection).
 */
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.Devices.Enumeration;
using Windows.Devices.Midi;

namespace MidiLink
{
    /// <summary>
    /// Maintains the MIDI input/output device lists with DeviceWatchers.
    /// Lists are updated item by item (never cleared) so ComboBox selections are preserved.
    /// All members must be used from the UI thread; watcher events are marshalled to it.
    /// </summary>
    public sealed class MidiDeviceService : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private DeviceWatcher? _inWatcher;
        private DeviceWatcher? _outWatcher;
        private bool _inEnumerated;
        private bool _outEnumerated;

        public MidiDeviceService(Dispatcher dispatcher) => _dispatcher = dispatcher;

        /// <summary>
        /// MIDI input devices (connected ones, plus disconnected ones still in use).
        /// </summary>
        public ObservableCollection<DeviceItem> InDevices { get; } = new();

        /// <summary>
        /// MIDI output devices (connected ones, plus disconnected ones still in use).
        /// </summary>
        public ObservableCollection<DeviceItem> OutDevices { get; } = new();

        /// <summary>
        /// Tells whether a device ID is selected somewhere; such a device is kept in the list when unplugged.
        /// </summary>
        public Func<string, bool> IsInUse { get; set; } = _ => false;

        /// <summary>
        /// True once the initial enumeration of both inputs and outputs is complete.
        /// </summary>
        public bool IsEnumerated { get; private set; }

        /// <summary>
        /// Raised when a device disappears (its ports must be closed).
        /// </summary>
        public event Action<string>? DeviceRemoved;

        /// <summary>
        /// Raised after the initial enumeration and after every change once enumerated.
        /// </summary>
        public event Action? DevicesChanged;

        /// <summary>
        /// Starts watching MIDI devices.
        /// </summary>
        public void Start()
        {
            _inWatcher = StartWatcher(MidiInPort.GetDeviceSelector(), isOut: false);
            _outWatcher = StartWatcher(MidiOutPort.GetDeviceSelector(), isOut: true);
        }

        private DeviceWatcher StartWatcher(string selector, bool isOut)
        {
            var watcher = DeviceInformation.CreateWatcher(selector);
            watcher.Added += (_, info) =>
            {
                string id = info.Id, name = info.Name;
                Post(() => OnAdded(isOut, id, name));
            };
            watcher.Removed += (_, update) =>
            {
                string id = update.Id;
                Post(() => OnRemoved(isOut, id));
            };
            watcher.Updated += (_, __) => { }; // must be handled for Added/Removed to be raised
            watcher.EnumerationCompleted += (_, __) => Post(() => OnEnumerationCompleted(isOut));
            watcher.Start();
            return watcher;
        }

        private void Post(Action action)
        {
            try
            {
                _dispatcher.BeginInvoke(() =>
                {
                    try { action(); }
                    catch (Exception ex) { AppLog.Error("Error handling device change", ex); }
                });
            }
            catch (Exception ex)
            {
                AppLog.Error("Error dispatching device change", ex);
            }
        }

        private void OnAdded(bool isOut, string id, string name)
        {
            AddOrUpdate(isOut, id, name);
            AppLog.Info($"Device connected ({(isOut ? "OUT" : "IN")}): {name} {id}");
            FixOutNames();
            if (IsEnumerated) DevicesChanged?.Invoke();
        }

        private void OnRemoved(bool isOut, string id)
        {
            if (!MarkRemoved(isOut, id)) return;
            DeviceRemoved?.Invoke(id);
            if (IsEnumerated) DevicesChanged?.Invoke();
        }

        private void OnEnumerationCompleted(bool isOut)
        {
            if (isOut) _outEnumerated = true; else _inEnumerated = true;
            if (!IsEnumerated && _inEnumerated && _outEnumerated)
            {
                IsEnumerated = true;
                DevicesChanged?.Invoke();
            }
        }

        /// <summary>
        /// Full rescan (Refresh button): reconciles the lists with what Windows reports right now.
        /// </summary>
        public async Task RescanAsync()
        {
            var ins = await DeviceInformation.FindAllAsync(MidiInPort.GetDeviceSelector());
            var outs = await DeviceInformation.FindAllAsync(MidiOutPort.GetDeviceSelector());

            Reconcile(isOut: false, ins);
            Reconcile(isOut: true, outs);
            FixOutNames();
            DevicesChanged?.Invoke();
        }

        private void Reconcile(bool isOut, IReadOnlyList<DeviceInformation> infos)
        {
            var present = new HashSet<string>(infos.Select(i => i.Id));

            foreach (var info in infos)
                AddOrUpdate(isOut, info.Id, info.Name);

            foreach (var item in List(isOut).Where(d => d.IsAvailable && !present.Contains(d.Id)).ToList())
            {
                if (MarkRemoved(isOut, item.Id))
                    DeviceRemoved?.Invoke(item.Id);
            }
        }

        private void AddOrUpdate(bool isOut, string id, string name)
        {
            var item = Find(id, isOut);
            if (item == null)
            {
                List(isOut).Add(new DeviceItem(id, name, isAvailable: true));
            }
            else
            {
                item.RawName = name;
                item.Name = name;
                item.IsAvailable = true;
            }
        }

        private bool MarkRemoved(bool isOut, string id)
        {
            var item = Find(id, isOut);
            if (item == null || !item.IsAvailable) return false;

            AppLog.Info($"Device disconnected ({(isOut ? "OUT" : "IN")}): {item.Name} {id}");

            // Keep it (greyed) while selected, otherwise the ComboBox would lose its selection
            if (IsInUse(id))
                item.IsAvailable = false;
            else
                List(isOut).Remove(item);
            return true;
        }

        /// <summary>
        /// Adds a "not connected" entry for a saved device, so its selection can be displayed.
        /// </summary>
        public void EnsurePlaceholder(string id, string? name, bool isOut)
        {
            if (string.IsNullOrWhiteSpace(id) || Find(id, isOut) != null) return;
            List(isOut).Add(new DeviceItem(id, string.IsNullOrWhiteSpace(name) ? "Unknown device" : name, isAvailable: false));
        }

        /// <summary>
        /// Removes disconnected devices that are no longer selected anywhere.
        /// </summary>
        public void PruneUnused()
        {
            foreach (var list in new[] { InDevices, OutDevices })
                foreach (var item in list.Where(d => !d.IsAvailable && !IsInUse(d.Id)).ToList())
                    list.Remove(item);
        }

        /// <summary>
        /// Finds a device by ID.
        /// </summary>
        public DeviceItem? Find(string? id, bool isOut) =>
            string.IsNullOrEmpty(id) ? null : List(isOut).FirstOrDefault(d => d.Id == id);

        /// <summary>
        /// True if the device is currently connected.
        /// </summary>
        public bool IsAvailable(string? id, bool isOut) => Find(id, isOut)?.IsAvailable == true;

        /// <summary>
        /// Windows often names outputs just "MIDI": use the name of the input with the same short ID.
        /// </summary>
        private void FixOutNames()
        {
            var inNameByShortId = InDevices
                .Where(d => d.IsAvailable && !string.IsNullOrEmpty(d.ShortId))
                .GroupBy(d => d.ShortId)
                .ToDictionary(g => g.Key, g => g.First().Name);

            foreach (var dev in OutDevices)
            {
                if (dev.IsAvailable &&
                    dev.RawName.Equals("MIDI", StringComparison.OrdinalIgnoreCase) &&
                    inNameByShortId.TryGetValue(dev.ShortId, out var betterName))
                {
                    dev.Name = betterName;
                }
            }
        }

        private ObservableCollection<DeviceItem> List(bool isOut) => isOut ? OutDevices : InDevices;

        /// <summary>
        /// Stops the device watchers.
        /// </summary>
        public void Dispose()
        {
            foreach (var watcher in new[] { _inWatcher, _outWatcher })
            {
                try
                {
                    if (watcher != null &&
                        (watcher.Status == DeviceWatcherStatus.Started || watcher.Status == DeviceWatcherStatus.EnumerationCompleted))
                        watcher.Stop();
                }
                catch (Exception ex)
                {
                    AppLog.Error("Error stopping device watcher", ex);
                }
            }
        }
    }
}
