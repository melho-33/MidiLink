/*
 * MidiLink - MidiFilter.cs
 * Copyright (c) 2025 melho
 * Licensed under GPL v3
 * Description: Per-link MIDI filters (transpose, note range, velocity, channels, message types, CC remap).
 */
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Windows.Devices.Midi;

namespace MidiLink
{
    public enum VelocityCurve { Linear, Soft, Hard }

    public enum VelocityMode { Unchanged, Range, Fixed }

    // ---------------- Persistence ----------------

    /// <summary>
    /// Filter settings as saved in config.json.
    /// </summary>
    public class MidiFilterConfig
    {
        public int Transpose { get; set; }
        public bool NoteRangeEnabled { get; set; }
        public int NoteMin { get; set; }
        public int NoteMax { get; set; } = 127;
        public VelocityCurve VelocityCurve { get; set; }
        public VelocityMode VelocityMode { get; set; }
        public int VelocityMin { get; set; } = 1;
        public int VelocityMax { get; set; } = 127;
        public int VelocityFixed { get; set; } = 100;
        public List<int> BlockedChannels { get; set; } = new();
        public int OutputChannel { get; set; }
        public bool PassControlChange { get; set; } = true;
        public bool PassPitchBend { get; set; } = true;
        public bool PassProgramChange { get; set; } = true;
        public bool PassChannelPressure { get; set; } = true;
        public bool PassPolyPressure { get; set; } = true;
        public bool PassSysEx { get; set; } = true;
        public bool PassClock { get; set; } = true;
        public bool PassActiveSensing { get; set; } = true;
        public List<CcRemapConfig> CcRemaps { get; set; } = new();
    }

    public class CcRemapConfig
    {
        public int From { get; set; }
        public int To { get; set; }
    }

    // ---------------- UI model ----------------

    /// <summary>
    /// One input channel toggle (1-16) in the filter UI.
    /// </summary>
    public class ChannelToggle : INotifyPropertyChanged
    {
        private bool _isAllowed = true;

        public ChannelToggle(int number) => Number = number;

        public int Number { get; }

        public bool IsAllowed
        {
            get => _isAllowed;
            set
            {
                if (_isAllowed == value) return;
                _isAllowed = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAllowed)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// One "CC x -> CC y" rule in the filter UI.
    /// </summary>
    public class CcRemap : INotifyPropertyChanged
    {
        private int _from;
        private int _to;

        public int From
        {
            get => _from;
            set { value = Math.Clamp(value, 0, 127); if (_from == value) return; _from = value; OnPropertyChanged(nameof(From)); }
        }

        public int To
        {
            get => _to;
            set { value = Math.Clamp(value, 0, 127); if (_to == value) return; _to = value; OnPropertyChanged(nameof(To)); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    /// <summary>
    /// Filter settings of one link, bound to the Filters flyout. UI thread only;
    /// the MIDI thread works on immutable <see cref="FilterSnapshot"/> copies.
    /// </summary>
    public class MidiFilterSettings : INotifyPropertyChanged
    {
        private int _transpose;
        private bool _noteRangeEnabled;
        private int _noteMin;
        private int _noteMax = 127;
        private VelocityCurve _velocityCurve;
        private VelocityMode _velocityMode;
        private int _velocityMin = 1;
        private int _velocityMax = 127;
        private int _velocityFixed = 100;
        private int _outputChannel;
        private bool _passControlChange = true;
        private bool _passPitchBend = true;
        private bool _passProgramChange = true;
        private bool _passChannelPressure = true;
        private bool _passPolyPressure = true;
        private bool _passSysEx = true;
        private bool _passClock = true;
        private bool _passActiveSensing = true;

        public MidiFilterSettings()
        {
            InputChannels = new ReadOnlyCollection<ChannelToggle>(
                Enumerable.Range(1, 16).Select(n => new ChannelToggle(n)).ToList());
            foreach (var ch in InputChannels)
                ch.PropertyChanged += (_, __) => OnChanged();

            CcRemaps.CollectionChanged += CcRemaps_CollectionChanged;
        }

        /// <summary>
        /// Raised after any change (used to refresh the processor and save the config).
        /// </summary>
        public event Action? Changed;

        // ----- Notes -----

        /// <summary>
        /// Transposition in semitones (-48..+48).
        /// </summary>
        public int Transpose
        {
            get => _transpose;
            set => Set(ref _transpose, Math.Clamp(value, -48, 48), nameof(Transpose), nameof(TransposeText));
        }

        public string TransposeText
        {
            get
            {
                if (Transpose == 0) return "No transposition";
                string st = $"{Transpose:+0;-0} semitone{(Math.Abs(Transpose) > 1 ? "s" : "")}";
                return Transpose % 12 == 0 ? $"{st} ({Transpose / 12:+0;-0} oct)" : st;
            }
        }

        public bool NoteRangeEnabled
        {
            get => _noteRangeEnabled;
            set => Set(ref _noteRangeEnabled, value, nameof(NoteRangeEnabled));
        }

        /// <summary>
        /// Lowest note let through (input note, before transposition).
        /// </summary>
        public int NoteMin
        {
            get => _noteMin;
            set
            {
                Set(ref _noteMin, Math.Clamp(value, 0, 127), nameof(NoteMin), nameof(NoteMinText));
                if (NoteMax < _noteMin) NoteMax = _noteMin;
            }
        }

        public int NoteMax
        {
            get => _noteMax;
            set
            {
                Set(ref _noteMax, Math.Clamp(value, 0, 127), nameof(NoteMax), nameof(NoteMaxText));
                if (NoteMin > _noteMax) NoteMin = _noteMax;
            }
        }

        public string NoteMinText => NoteName(NoteMin);
        public string NoteMaxText => NoteName(NoteMax);

        // ----- Velocity -----

        public VelocityCurve VelocityCurve
        {
            get => _velocityCurve;
            set => Set(ref _velocityCurve, value, nameof(VelocityCurve), nameof(VelocityCurveIndex));
        }

        /// <summary>
        /// For the ComboBox (Linear, Soft, Hard).
        /// </summary>
        public int VelocityCurveIndex
        {
            get => (int)VelocityCurve;
            set => VelocityCurve = (VelocityCurve)Math.Clamp(value, 0, 2);
        }

        public VelocityMode VelocityMode
        {
            get => _velocityMode;
            set => Set(ref _velocityMode, value, nameof(VelocityMode), nameof(VelocityModeIndex),
                nameof(IsVelocityRange), nameof(IsVelocityFixed));
        }

        /// <summary>
        /// For the ComboBox (Unchanged, Range, Fixed).
        /// </summary>
        public int VelocityModeIndex
        {
            get => (int)VelocityMode;
            set => VelocityMode = (VelocityMode)Math.Clamp(value, 0, 2);
        }

        public bool IsVelocityRange => VelocityMode == VelocityMode.Range;
        public bool IsVelocityFixed => VelocityMode == VelocityMode.Fixed;

        public int VelocityMin
        {
            get => _velocityMin;
            set
            {
                Set(ref _velocityMin, Math.Clamp(value, 1, 127), nameof(VelocityMin));
                if (VelocityMax < _velocityMin) VelocityMax = _velocityMin;
            }
        }

        public int VelocityMax
        {
            get => _velocityMax;
            set
            {
                Set(ref _velocityMax, Math.Clamp(value, 1, 127), nameof(VelocityMax));
                if (VelocityMin > _velocityMax) VelocityMin = _velocityMax;
            }
        }

        public int VelocityFixed
        {
            get => _velocityFixed;
            set => Set(ref _velocityFixed, Math.Clamp(value, 1, 127), nameof(VelocityFixed));
        }

        // ----- Channels -----

        /// <summary>
        /// The 16 input channels; unchecked ones are ignored.
        /// </summary>
        public ReadOnlyCollection<ChannelToggle> InputChannels { get; }

        /// <summary>
        /// 0 = keep the channel, 1-16 = send everything on that channel. Matches the ComboBox index.
        /// </summary>
        public int OutputChannel
        {
            get => _outputChannel;
            set => Set(ref _outputChannel, Math.Clamp(value, 0, 16), nameof(OutputChannel));
        }

        // ----- Message types -----

        public bool PassControlChange { get => _passControlChange; set => Set(ref _passControlChange, value, nameof(PassControlChange)); }
        public bool PassPitchBend { get => _passPitchBend; set => Set(ref _passPitchBend, value, nameof(PassPitchBend)); }
        public bool PassProgramChange { get => _passProgramChange; set => Set(ref _passProgramChange, value, nameof(PassProgramChange)); }
        public bool PassChannelPressure { get => _passChannelPressure; set => Set(ref _passChannelPressure, value, nameof(PassChannelPressure)); }
        public bool PassPolyPressure { get => _passPolyPressure; set => Set(ref _passPolyPressure, value, nameof(PassPolyPressure)); }
        public bool PassSysEx { get => _passSysEx; set => Set(ref _passSysEx, value, nameof(PassSysEx)); }
        public bool PassClock { get => _passClock; set => Set(ref _passClock, value, nameof(PassClock)); }
        public bool PassActiveSensing { get => _passActiveSensing; set => Set(ref _passActiveSensing, value, nameof(PassActiveSensing)); }

        // ----- CC remap -----

        public ObservableCollection<CcRemap> CcRemaps { get; } = new();

        private void CcRemaps_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
                foreach (CcRemap r in e.NewItems) r.PropertyChanged += CcRemap_PropertyChanged;
            if (e.OldItems != null)
                foreach (CcRemap r in e.OldItems) r.PropertyChanged -= CcRemap_PropertyChanged;
            OnChanged();
        }

        private void CcRemap_PropertyChanged(object? sender, PropertyChangedEventArgs e) => OnChanged();

        // ----- Summary -----

        /// <summary>
        /// True if at least one filter changes something.
        /// </summary>
        public bool IsActive => Describe().Count > 0;

        /// <summary>
        /// Short description of the active filters (tooltip of the Filters button).
        /// </summary>
        public string Summary
        {
            get
            {
                var parts = Describe();
                return parts.Count == 0 ? "Filters: none" : "Filters:\n• " + string.Join("\n• ", parts);
            }
        }

        private List<string> Describe()
        {
            var parts = new List<string>();
            if (Transpose != 0) parts.Add($"Transpose {Transpose:+0;-0}");
            if (NoteRangeEnabled) parts.Add($"Notes {NoteMinText} – {NoteMaxText}");
            if (VelocityCurve != VelocityCurve.Linear) parts.Add($"Velocity curve: {VelocityCurve}");
            if (VelocityMode == VelocityMode.Range) parts.Add($"Velocity {VelocityMin} – {VelocityMax}");
            if (VelocityMode == VelocityMode.Fixed) parts.Add($"Velocity fixed at {VelocityFixed}");

            var blocked = InputChannels.Where(c => !c.IsAllowed).Select(c => c.Number).ToList();
            if (blocked.Count > 0) parts.Add($"Ignored channels: {string.Join(", ", blocked)}");
            if (OutputChannel > 0) parts.Add($"Send on channel {OutputChannel}");

            var blockedTypes = new List<string>();
            if (!PassControlChange) blockedTypes.Add("CC");
            if (!PassPitchBend) blockedTypes.Add("Pitch bend");
            if (!PassProgramChange) blockedTypes.Add("Program change");
            if (!PassChannelPressure) blockedTypes.Add("Channel aftertouch");
            if (!PassPolyPressure) blockedTypes.Add("Poly aftertouch");
            if (!PassSysEx) blockedTypes.Add("SysEx");
            if (!PassClock) blockedTypes.Add("Clock & transport");
            if (!PassActiveSensing) blockedTypes.Add("Active sensing");
            if (blockedTypes.Count > 0) parts.Add($"Blocked: {string.Join(", ", blockedTypes)}");

            foreach (var r in CcRemaps.Where(r => r.From != r.To))
                parts.Add($"CC {r.From} → CC {r.To}");
            return parts;
        }

        // ----- Helpers -----

        /// <summary>
        /// Note name, with C4 = 60 (e.g. "C4 (60)").
        /// </summary>
        public static string NoteName(int note)
        {
            string[] names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
            return $"{names[note % 12]}{note / 12 - 1} ({note})";
        }

        /// <summary>
        /// Back to "no filter".
        /// </summary>
        public void Reset() => Load(new MidiFilterConfig());

        public void SetAllChannels(bool allowed)
        {
            foreach (var ch in InputChannels) ch.IsAllowed = allowed;
        }

        public MidiFilterConfig ToConfig() => new()
        {
            Transpose = Transpose,
            NoteRangeEnabled = NoteRangeEnabled,
            NoteMin = NoteMin,
            NoteMax = NoteMax,
            VelocityCurve = VelocityCurve,
            VelocityMode = VelocityMode,
            VelocityMin = VelocityMin,
            VelocityMax = VelocityMax,
            VelocityFixed = VelocityFixed,
            BlockedChannels = InputChannels.Where(c => !c.IsAllowed).Select(c => c.Number).ToList(),
            OutputChannel = OutputChannel,
            PassControlChange = PassControlChange,
            PassPitchBend = PassPitchBend,
            PassProgramChange = PassProgramChange,
            PassChannelPressure = PassChannelPressure,
            PassPolyPressure = PassPolyPressure,
            PassSysEx = PassSysEx,
            PassClock = PassClock,
            PassActiveSensing = PassActiveSensing,
            CcRemaps = CcRemaps.Select(r => new CcRemapConfig { From = r.From, To = r.To }).ToList()
        };

        public void Load(MidiFilterConfig cfg)
        {
            Transpose = cfg.Transpose;
            NoteRangeEnabled = cfg.NoteRangeEnabled;
            // Open the max first so setting the min never drags the max along
            NoteMax = 127;
            NoteMin = cfg.NoteMin;
            NoteMax = cfg.NoteMax;
            VelocityCurve = cfg.VelocityCurve;
            VelocityMode = cfg.VelocityMode;
            VelocityMax = 127;
            VelocityMin = cfg.VelocityMin;
            VelocityMax = cfg.VelocityMax;
            VelocityFixed = cfg.VelocityFixed;
            foreach (var ch in InputChannels)
                ch.IsAllowed = !cfg.BlockedChannels.Contains(ch.Number);
            OutputChannel = cfg.OutputChannel;
            PassControlChange = cfg.PassControlChange;
            PassPitchBend = cfg.PassPitchBend;
            PassProgramChange = cfg.PassProgramChange;
            PassChannelPressure = cfg.PassChannelPressure;
            PassPolyPressure = cfg.PassPolyPressure;
            PassSysEx = cfg.PassSysEx;
            PassClock = cfg.PassClock;
            PassActiveSensing = cfg.PassActiveSensing;
            CcRemaps.Clear();
            foreach (var r in cfg.CcRemaps)
                CcRemaps.Add(new CcRemap { From = r.From, To = r.To });
            OnChanged();
        }

        /// <summary>
        /// Immutable copy used by the MIDI thread.
        /// </summary>
        public FilterSnapshot Snapshot()
        {
            var ccMap = new int[128];
            for (int i = 0; i < 128; i++) ccMap[i] = i;
            foreach (var r in CcRemaps) ccMap[r.From] = r.To;

            return new FilterSnapshot(
                Transpose,
                NoteRangeEnabled ? NoteMin : 0,
                NoteRangeEnabled ? NoteMax : 127,
                VelocityCurve, VelocityMode, VelocityMin, VelocityMax, VelocityFixed,
                InputChannels.Select(c => c.IsAllowed).ToArray(),
                OutputChannel,
                PassControlChange, PassPitchBend, PassProgramChange, PassChannelPressure,
                PassPolyPressure, PassSysEx, PassClock, PassActiveSensing,
                ccMap);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, params string[] props)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            foreach (var p in props)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
            OnChanged();
        }

        private void OnChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
            Changed?.Invoke();
        }
    }

    // ---------------- Processing ----------------

    /// <summary>
    /// Immutable filter settings, safe to read from the MIDI thread.
    /// </summary>
    public sealed record FilterSnapshot(
        int Transpose, int NoteMin, int NoteMax,
        VelocityCurve VelocityCurve, VelocityMode VelocityMode, int VelocityMin, int VelocityMax, int VelocityFixed,
        bool[] AllowedChannels, int OutputChannel,
        bool PassControlChange, bool PassPitchBend, bool PassProgramChange, bool PassChannelPressure,
        bool PassPolyPressure, bool PassSysEx, bool PassClock, bool PassActiveSensing,
        int[] CcMap);

    /// <summary>
    /// Applies a link's filters to incoming messages (MIDI thread).
    /// Remembers held notes so a note always gets its Note Off on the same channel / pitch as its
    /// Note On, even if the transposition or range changed meanwhile (no stuck notes).
    /// </summary>
    public sealed class MidiFilterProcessor
    {
        private volatile FilterSnapshot _settings;
        private readonly object _heldLock = new();
        private readonly Dictionary<(byte Channel, byte Note), (byte Channel, byte Note)> _held = new();

        public MidiFilterProcessor(FilterSnapshot settings) => _settings = settings;

        /// <summary>
        /// Replaces the settings (UI thread); takes effect on the next message.
        /// </summary>
        public void Update(FilterSnapshot settings) => _settings = settings;

        /// <summary>
        /// Returns the message to send, or null to drop it.
        /// </summary>
        public IMidiMessage? Process(IMidiMessage message)
        {
            var s = _settings;
            switch (message.Type)
            {
                case MidiMessageType.NoteOn:
                {
                    var m = (MidiNoteOnMessage)message;
                    return m.Velocity == 0
                        ? NoteOff(s, m.Channel, m.Note, 0) // Note On with velocity 0 is a Note Off
                        : NoteOn(s, m);
                }

                case MidiMessageType.NoteOff:
                {
                    var m = (MidiNoteOffMessage)message;
                    return NoteOff(s, m.Channel, m.Note, m.Velocity);
                }

                case MidiMessageType.PolyphonicKeyPressure:
                {
                    var m = (MidiPolyphonicKeyPressureMessage)message;
                    if (!s.PassPolyPressure || !ChannelAllowed(s, m.Channel)) return null;
                    if (!MapNote(s, m.Note, out byte note)) return null;
                    return new MidiPolyphonicKeyPressureMessage(OutChannel(s, m.Channel), note, m.Pressure);
                }

                case MidiMessageType.ControlChange:
                {
                    var m = (MidiControlChangeMessage)message;
                    if (!s.PassControlChange || !ChannelAllowed(s, m.Channel)) return null;
                    byte ch = OutChannel(s, m.Channel);
                    byte cc = (byte)s.CcMap[m.Controller];
                    return ch == m.Channel && cc == m.Controller ? message : new MidiControlChangeMessage(ch, cc, m.ControlValue);
                }

                case MidiMessageType.ProgramChange:
                {
                    var m = (MidiProgramChangeMessage)message;
                    if (!s.PassProgramChange || !ChannelAllowed(s, m.Channel)) return null;
                    byte ch = OutChannel(s, m.Channel);
                    return ch == m.Channel ? message : new MidiProgramChangeMessage(ch, m.Program);
                }

                case MidiMessageType.ChannelPressure:
                {
                    var m = (MidiChannelPressureMessage)message;
                    if (!s.PassChannelPressure || !ChannelAllowed(s, m.Channel)) return null;
                    byte ch = OutChannel(s, m.Channel);
                    return ch == m.Channel ? message : new MidiChannelPressureMessage(ch, m.Pressure);
                }

                case MidiMessageType.PitchBendChange:
                {
                    var m = (MidiPitchBendChangeMessage)message;
                    if (!s.PassPitchBend || !ChannelAllowed(s, m.Channel)) return null;
                    byte ch = OutChannel(s, m.Channel);
                    return ch == m.Channel ? message : new MidiPitchBendChangeMessage(ch, m.Bend);
                }

                case MidiMessageType.SystemExclusive:
                    return s.PassSysEx ? message : null;

                case MidiMessageType.TimingClock:
                case MidiMessageType.Start:
                case MidiMessageType.Continue:
                case MidiMessageType.Stop:
                case MidiMessageType.SongPositionPointer:
                    return s.PassClock ? message : null;

                case MidiMessageType.ActiveSensing:
                    return s.PassActiveSensing ? message : null;

                default:
                    return message;
            }
        }

        private IMidiMessage? NoteOn(FilterSnapshot s, MidiNoteOnMessage m)
        {
            if (!ChannelAllowed(s, m.Channel)) return null;
            if (m.Note < s.NoteMin || m.Note > s.NoteMax) return null;
            if (!MapNote(s, m.Note, out byte note)) return null;

            byte ch = OutChannel(s, m.Channel);
            byte velocity = MapVelocity(s, m.Velocity);

            lock (_heldLock)
                _held[(m.Channel, m.Note)] = (ch, note);

            return ch == m.Channel && note == m.Note && velocity == m.Velocity
                ? m
                : new MidiNoteOnMessage(ch, note, velocity);
        }

        private IMidiMessage? NoteOff(FilterSnapshot s, byte channel, byte inNote, byte velocity)
        {
            // Released the way it was pressed, whatever the current settings
            lock (_heldLock)
            {
                if (_held.Remove((channel, inNote), out var target))
                    return new MidiNoteOffMessage(target.Channel, target.Note, velocity);
            }

            // Unknown note (pressed before the link opened...): apply the current filters
            if (!ChannelAllowed(s, channel)) return null;
            if (inNote < s.NoteMin || inNote > s.NoteMax) return null;
            if (!MapNote(s, inNote, out byte note)) return null;
            return new MidiNoteOffMessage(OutChannel(s, channel), note, velocity);
        }

        /// <summary>
        /// Note Offs for every note still held (sent when the link closes). Clears the held notes.
        /// </summary>
        public List<IMidiMessage> ReleaseHeldNotes()
        {
            lock (_heldLock)
            {
                var messages = _held.Values
                    .Select(t => (IMidiMessage)new MidiNoteOffMessage(t.Channel, t.Note, 0))
                    .ToList();
                _held.Clear();
                return messages;
            }
        }

        private static bool ChannelAllowed(FilterSnapshot s, byte channel) => s.AllowedChannels[channel & 0x0F];

        private static byte OutChannel(FilterSnapshot s, byte channel) =>
            s.OutputChannel == 0 ? channel : (byte)(s.OutputChannel - 1);

        private static bool MapNote(FilterSnapshot s, byte inNote, out byte outNote)
        {
            int n = inNote + s.Transpose;
            outNote = (byte)Math.Clamp(n, 0, 127);
            return n >= 0 && n <= 127; // transposed out of the MIDI range: dropped
        }

        private static byte MapVelocity(FilterSnapshot s, byte velocity)
        {
            double v = velocity;
            if (s.VelocityCurve == VelocityCurve.Soft)
                v = 127.0 * Math.Pow(v / 127.0, 0.6); // light touch plays louder
            else if (s.VelocityCurve == VelocityCurve.Hard)
                v = 127.0 * Math.Pow(v / 127.0, 1.6); // needs to play harder

            int result = (int)Math.Round(v);
            result = s.VelocityMode switch
            {
                VelocityMode.Range => Math.Clamp(result, s.VelocityMin, s.VelocityMax),
                VelocityMode.Fixed => s.VelocityFixed,
                _ => result
            };
            return (byte)Math.Clamp(result, 1, 127); // 0 would turn the Note On into a Note Off
        }
    }
}
