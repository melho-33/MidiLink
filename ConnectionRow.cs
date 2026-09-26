/*
 * MidiLink - ConnectionRow.cs
 * Copyright (c) 2025 melho
 * Licensed under GPL v3
 * Description: A MIDI link between one input and one output.
 */
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Devices.Midi;

namespace MidiLink
{
    /// <summary>
    /// State of a connection, shown on its row.
    /// </summary>
    public enum ConnectionState
    {
        Disabled,
        Incomplete,
        Connecting,
        Connected,
        DeviceMissing,
        /// <summary>Ports open, but the output device did not answer the last message (e.g. switched off).</summary>
        NotResponding,
        Error
    }

    /// <summary>
    /// Represents a MIDI connection row (link between input and output devices).
    /// Ports come from the shared <see cref="MidiPortManager"/>. UI-thread only, except the MIDI callback.
    /// </summary>
    public class ConnectionRow : INotifyPropertyChanged, IDisposable
    {
        private string? _inId;
        private string? _outId;
        private bool _inactive;
        private ConnectionState _state = ConnectionState.Disabled;
        private string _stateText = "";

        private InPortLease? _inLease;
        private volatile OutPortLease? _outLease;

        // Incremented by each UpdateAsync: an older call finishing late must not apply its result
        private int _version;

        // 1 while a flash / error report is queued on the UI thread (avoids flooding it with MIDI clock etc.)
        private int _flashPending;
        private int _errorPending;
        private DispatcherTimer? _flashTimer;

        private readonly MidiFilterProcessor _processor;

        public ConnectionRow()
        {
            _processor = new MidiFilterProcessor(Filter.Snapshot());
            Filter.Changed += () => _processor.Update(Filter.Snapshot());
        }

        /// <summary>
        /// Filters applied to the messages of this link.
        /// </summary>
        public MidiFilterSettings Filter { get; } = new();

        /// <summary>
        /// Color of the arrow, flashes when messages go through.
        /// </summary>
        public SolidColorBrush ArrowBrush { get; } = new SolidColorBrush(Colors.Gray);

        /// <summary>
        /// MIDI input device ID.
        /// </summary>
        public string? InId
        {
            get => _inId;
            set
            {
                if (_inId == value) return;
                _inId = value;
                OnPropertyChanged(nameof(InId));
            }
        }

        /// <summary>
        /// MIDI output device ID.
        /// </summary>
        public string? OutId
        {
            get => _outId;
            set
            {
                if (_outId == value) return;
                _outId = value;
                OnPropertyChanged(nameof(OutId));
            }
        }

        /// <summary>
        /// True if the connection is inactive (disabled).
        /// </summary>
        public bool IsInactive
        {
            get => _inactive;
            set
            {
                if (_inactive == value) return;
                _inactive = value;
                OnPropertyChanged(nameof(IsInactive));
                OnPropertyChanged(nameof(ActiveLabel));
            }
        }

        /// <summary>
        /// Label for the enable/disable button in the UI.
        /// </summary>
        public string ActiveLabel => IsInactive ? "Enable" : "Disable";

        /// <summary>
        /// Current state of the connection.
        /// </summary>
        public ConnectionState State
        {
            get => _state;
            private set
            {
                if (_state == value) return;
                _state = value;
                OnPropertyChanged(nameof(State));
                OnPropertyChanged(nameof(IsActive));
            }
        }

        /// <summary>
        /// Short human-readable state, shown on the row.
        /// </summary>
        public string StateText
        {
            get => _stateText;
            private set
            {
                if (_stateText == value) return;
                _stateText = value;
                OnPropertyChanged(nameof(StateText));
            }
        }

        /// <summary>
        /// True if MIDI is currently routed.
        /// </summary>
        public bool IsActive => State == ConnectionState.Connected;

        /// <summary>
        /// Brings the connection to the state its settings ask for: opens, reopens or closes the ports.
        /// Safe to call as often as needed; does nothing if already connected to the right devices.
        /// </summary>
        public async Task UpdateAsync(MidiPortManager ports, MidiDeviceService devices, bool globalMute)
        {
            int version = ++_version;

            if (globalMute || IsInactive)
            {
                Close();
                SetState(ConnectionState.Disabled, globalMute ? "All disabled" : "Disabled");
                return;
            }

            if (string.IsNullOrWhiteSpace(InId) || string.IsNullOrWhiteSpace(OutId))
            {
                Close();
                SetState(ConnectionState.Incomplete, "Select IN and OUT");
                return;
            }

            // A side is fine if it is open, still working and on the selected device
            bool inOk = _inLease is { IsAlive: true } inL && inL.DeviceId == InId;
            bool outOk = _outLease is { IsAlive: true } outL && outL.DeviceId == OutId;
            if (inOk && outOk)
            {
                SetConnected(ports);
                return;
            }

            if (!devices.IsAvailable(InId, isOut: false) || !devices.IsAvailable(OutId, isOut: true))
            {
                Close();
                SetState(ConnectionState.DeviceMissing, "Device not connected");
                return;
            }

            // Reopen only the side that failed or changed: the other one keeps working
            // (closing and reopening a healthy input would lose its messages meanwhile)
            if (!inOk)
            {
                _inLease?.Dispose();
                _inLease = null;
            }
            if (!outOk)
                ReleaseOutput();

            SetState(ConnectionState.Connecting, "Connecting...");
            string inId = InId!, outId = OutId!;
            var inTask = inOk ? null : ports.AcquireInAsync(inId, OnMessage);
            var outTask = outOk ? null : ports.AcquireOutAsync(outId);
            InPortLease? newIn = null;
            OutPortLease? newOut = null;
            try
            {
                // Both open in parallel (each has its own timeout)
                if (inTask != null)
                {
                    try
                    {
                        newIn = await inTask;
                    }
                    catch
                    {
                        _ = outTask?.ContinueWith(t =>
                        {
                            if (t.IsCompletedSuccessfully) t.Result.Dispose();
                            else _ = t.Exception;
                        });
                        throw;
                    }
                }
                if (outTask != null)
                    newOut = await outTask;
            }
            catch (Exception ex)
            {
                newIn?.Dispose();
                if (version == _version)
                    SetState(ConnectionState.Error, ex.Message);
                return;
            }

            // Settings changed while we were opening: this result is obsolete
            if (version != _version)
            {
                newIn?.Dispose();
                newOut?.Dispose();
                return;
            }

            if (newIn != null) _inLease = newIn;
            if (newOut != null) _outLease = newOut;
            SetConnected(ports);
        }

        /// <summary>
        /// Connected, unless the output is known as not responding (still listed by Windows but switched off...).
        /// </summary>
        private void SetConnected(MidiPortManager ports)
        {
            if (ports.IsResponding(OutId))
                SetState(ConnectionState.Connected, "Connected");
            else
                SetState(ConnectionState.NotResponding, "Output not responding (switched off?)");
        }

        private void SetState(ConnectionState state, string text)
        {
            State = state;
            StateText = text;
        }

        /// <summary>
        /// MIDI thread: forwards an incoming message to the output.
        /// </summary>
        private void OnMessage(IMidiMessage message)
        {
            try
            {
                if (!IsInactive && _processor.Process(message) is IMidiMessage output)
                    _outLease?.Send(output);
            }
            catch (Exception ex)
            {
                AppLog.Error($"Error sending MIDI to {OutId}", ex);
                if (Interlocked.Exchange(ref _errorPending, 1) == 0)
                {
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        _errorPending = 0;
                        if (State == ConnectionState.Connected)
                            SetState(ConnectionState.Error, $"Send failed: {ex.Message}");
                    });
                }
            }

            // At most one queued flash: MIDI clock alone is 24 messages per beat
            if (Interlocked.Exchange(ref _flashPending, 1) == 0)
                Application.Current?.Dispatcher.BeginInvoke(Flash);
        }

        private void Flash()
        {
            _flashPending = 0;
            ArrowBrush.Color = Colors.Orange;

            if (_flashTimer == null)
            {
                _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _flashTimer.Tick += (s, e) =>
                {
                    ArrowBrush.Color = Colors.Gray;
                    _flashTimer.Stop();
                };
            }

            // Restart the timer on each new message
            _flashTimer.Stop();
            _flashTimer.Start();
        }

        /// <summary>
        /// Gives the ports back to the manager (it closes them if nobody else uses them).
        /// </summary>
        public void Close()
        {
            var inLease = _inLease;
            var outLease = _outLease;
            _inLease = null;
            _outLease = null;
            inLease?.Dispose();
            ReleaseOutput(outLease);
        }

        private void ReleaseOutput()
        {
            var outLease = _outLease;
            _outLease = null;
            ReleaseOutput(outLease);
        }

        private void ReleaseOutput(OutPortLease? outLease)
        {
            if (outLease == null) return;

            // Release the notes still held through this link, or they would ring forever
            SendAll(outLease, _processor.ReleaseHeldNotes());
            outLease.Dispose();
        }

        private void SendAll(OutPortLease lease, List<IMidiMessage> messages)
        {
            try
            {
                foreach (var m in messages)
                    lease.Send(m);
            }
            catch (Exception ex)
            {
                AppLog.Error($"Error sending note offs to {OutId}", ex);
            }
        }

        /// <summary>
        /// Disposes the connection row.
        /// </summary>
        public void Dispose()
        {
            _version++; // cancel any pending UpdateAsync
            Close();
            _flashTimer?.Stop();
        }

        /// <summary>
        /// PropertyChanged event for INotifyPropertyChanged.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string prop) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
