/*
 * MidiLink - DeviceItem.cs
 * Copyright (c) 2025 melho
 * Licensed under GPL v3
 * Description: A MIDI device shown in the device lists (connected or not).
 */
using System;
using System.ComponentModel;

namespace MidiLink
{
    /// <summary>
    /// Represents a MIDI device (input or output). A device that is not connected stays in the list
    /// (IsAvailable = false) as long as a connection uses it, so ComboBox selections are never lost.
    /// </summary>
    public class DeviceItem : INotifyPropertyChanged
    {
        private string _name;
        private bool _isAvailable;

        public DeviceItem(string id, string name, bool isAvailable)
        {
            Id = id;
            RawName = name;
            _name = name;
            _isAvailable = isAvailable;
            ShortId = ExtractShortId(id) ?? "";
        }

        /// <summary>
        /// Device identifier string.
        /// </summary>
        public string Id { get; }

        /// <summary>
        /// Short identifier extracted from device ID.
        /// </summary>
        public string ShortId { get; }

        /// <summary>
        /// Physical device the port belongs to (links a Bluetooth MIDI port to its Bluetooth device).
        /// </summary>
        public Guid? ContainerId { get; set; }

        /// <summary>
        /// Name reported by Windows (may be the generic "MIDI" for outputs).
        /// </summary>
        public string RawName { get; set; }

        /// <summary>
        /// Device display name.
        /// </summary>
        public string Name
        {
            get => _name;
            set
            {
                if (_name == value) return;
                _name = value;
                OnPropertyChanged(nameof(Name));
                OnPropertyChanged(nameof(Display));
            }
        }

        /// <summary>
        /// True if the device is currently connected.
        /// </summary>
        public bool IsAvailable
        {
            get => _isAvailable;
            set
            {
                if (_isAvailable == value) return;
                _isAvailable = value;
                OnPropertyChanged(nameof(IsAvailable));
                OnPropertyChanged(nameof(Display));
            }
        }

        /// <summary>
        /// Display string for UI (includes short ID if available, and a marker when disconnected).
        /// </summary>
        public string Display =>
            (string.IsNullOrEmpty(ShortId) ? Name : $"{Name} [{ShortId}]") + (IsAvailable ? "" : " (not connected)");

        /// <summary>
        /// Extracts a short ID from a full device ID string.
        /// </summary>
        public static string? ExtractShortId(string fullId)
        {
            if (string.IsNullOrEmpty(fullId)) return null;
            int idx = fullId.IndexOf("MIDI", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                int underscore = fullId.IndexOf('_', idx);
                if (underscore >= 0)
                {
                    int dot = fullId.IndexOf('.', underscore);
                    if (dot > underscore)
                        return fullId.Substring(underscore + 1, dot - underscore - 1);
                }
            }
            return null;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
