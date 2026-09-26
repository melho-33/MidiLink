/*
 * MidiLink - ConfigService.cs
 * Copyright (c) 2025 melho
 * Licensed under GPL v3
 * Description: Loading and debounced saving of the application configuration.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace MidiLink
{
    /// <summary>
    /// Application configuration (connections and theme).
    /// </summary>
    public class AppConfig
    {
        /// <summary>
        /// List of MIDI connection configurations.
        /// </summary>
        public List<MidiConnectionConfig> Connections { get; set; } = new();
        /// <summary>
        /// Selected theme ("Light", "Dark", "System").
        /// </summary>
        public string Theme { get; set; } = "System";
        /// <summary>
        /// When true, minimizing hides the window in the system tray.
        /// </summary>
        public bool MinimizeToTray { get; set; }
        /// <summary>
        /// Accent color as "#RRGGBB"; empty = Windows accent color.
        /// </summary>
        public string AccentColor { get; set; } = "";
    }

    /// <summary>
    /// Configuration for a MIDI connection (for persistence).
    /// </summary>
    public class MidiConnectionConfig
    {
        /// <summary>
        /// MIDI input device ID.
        /// </summary>
        public string InId { get; set; } = "";
        /// <summary>
        /// MIDI output device ID.
        /// </summary>
        public string OutId { get; set; } = "";
        /// <summary>
        /// Input device name, used to find the device again if its ID changes (e.g. other USB port).
        /// </summary>
        public string InName { get; set; } = "";
        /// <summary>
        /// Output device name, used to find the device again if its ID changes.
        /// </summary>
        public string OutName { get; set; } = "";
        /// <summary>
        /// True if the connection is inactive (disabled).
        /// </summary>
        public bool Inactive { get; set; }
        /// <summary>
        /// MIDI filters of the connection (null in configs saved before filters existed).
        /// </summary>
        public MidiFilterConfig? Filter { get; set; }
    }

    /// <summary>
    /// Reads and writes config.json. Saves are debounced and written from a background thread;
    /// callers pass a snapshot built on the UI thread, so the UI collections are never touched here.
    /// </summary>
    public sealed class ConfigService : IDisposable
    {
        private const int SaveDelayMs = 500;

        private readonly string _configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MidiLink",
            "config.json");

        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private readonly object _pendingLock = new();
        private readonly object _writeLock = new();
        private Timer? _saveTimer;
        private AppConfig? _pending;

        /// <summary>
        /// Loads the configuration, or returns defaults if the file is missing or invalid.
        /// </summary>
        public AppConfig Load()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    var json = File.ReadAllText(_configPath);
                    return JsonSerializer.Deserialize<AppConfig>(json, _jsonOptions) ?? new AppConfig();
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Error loading config", ex);
            }
            return new AppConfig();
        }

        /// <summary>
        /// Schedules a save of the given snapshot (debounced: only the last one within 500 ms is written).
        /// </summary>
        public void Save(AppConfig snapshot)
        {
            lock (_pendingLock)
            {
                _pending = snapshot;
                _saveTimer?.Dispose();
                _saveTimer = new Timer(_ => Flush(), null, SaveDelayMs, Timeout.Infinite);
            }
        }

        /// <summary>
        /// Writes the pending snapshot immediately, if any.
        /// </summary>
        public void Flush()
        {
            AppConfig? cfg;
            lock (_pendingLock)
            {
                cfg = _pending;
                _pending = null;
            }
            if (cfg == null) return;

            try
            {
                lock (_writeLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
                    File.WriteAllText(_configPath, JsonSerializer.Serialize(cfg, _jsonOptions));
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Error saving config", ex);
            }
        }

        /// <summary>
        /// Stops the timer and writes any pending change (so nothing is lost on exit).
        /// </summary>
        public void Dispose()
        {
            lock (_pendingLock)
            {
                _saveTimer?.Dispose();
                _saveTimer = null;
            }
            Flush();
        }
    }
}
