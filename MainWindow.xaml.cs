/*
 * MidiLink - MainWindow.xaml.cs
 * Copyright (c) 2025 melho
 * Licensed under GPL v3
 * Description: Main window for the MIDI routing application.
 */
using Microsoft.Win32;
using ModernWpf;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Devices.Enumeration;
using Windows.Devices.Midi;

namespace MidiLink
{
    /// <summary>
    /// Main application window. Handles UI, configuration, MIDI routing, and theme management.
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged, IDisposable
    {
        // Observable collections for MIDI input and output devices.
        private readonly ObservableCollection<DeviceItem> _inDevices = new();
        private readonly ObservableCollection<DeviceItem> _outDevices = new();

        /// <summary>
        /// Read-only collection of available MIDI input devices.
        /// </summary>
        public ReadOnlyObservableCollection<DeviceItem> InDevices { get; }

        /// <summary>
        /// Read-only collection of available MIDI output devices.
        /// </summary>
        public ReadOnlyObservableCollection<DeviceItem> OutDevices { get; }

        /// <summary>
        /// Collection of MIDI connection rows (links between input and output).
        /// </summary>
        public ObservableCollection<ConnectionRow> Connections { get; } = new();

        // Status text displayed in the UI.
        private string _statusText = "Ready";

        /// <summary>
        /// Global mute flag. If true, all MIDI routing is disabled.
        /// </summary>
        public static bool GlobalMute { get; private set; } = false;


        private IMidiOutPort? _currentKeyboardOutPort;

        /// <summary>
        /// Status text property for UI binding.
        /// </summary>
        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(nameof(StatusText)); }
        }

        // Path to the configuration file.
        private readonly string _configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MidiLink",
            "config.json");
        private void EnsureConfigDirectory()
        {
            var dir = Path.GetDirectoryName(_configPath);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir!);
        }

        // JSON serializer options for config file.
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        // Timer for debounced config saving.
        private Timer? _saveTimer;

        /// <summary>
        /// Initializes the main window and sets up event handlers.
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            InDevices = new ReadOnlyObservableCollection<DeviceItem>(_inDevices);
            OutDevices = new ReadOnlyObservableCollection<DeviceItem>(_outDevices);

            Loaded += async (_, __) =>
            {
                await RefreshDevicesAsync();
                LoadConfigAndRestore();

                if (OutDevices.Any())
                {
                    OutKeyboardCombo.SelectedValue = OutDevices.Last().Id;
                }

                // Reopen all connections after loading config
                foreach (var c in Connections)
                    _ = UpdateConnectionAsync(c);
            };

            Unloaded += (_, __) => Dispose();
        }

        // ---------------- UI Event Handlers ----------------

        /// <summary>
        /// Adds a new MIDI connection row, disabled by default.
        /// </summary>
        private void AddConnection_Click(object sender, RoutedEventArgs e)
        {
            var row = new ConnectionRow
            {
                IsInactive = true // New connections are disabled by default
            };
            row.PropertyChanged += Connection_PropertyChanged;
            Connections.Add(row);
            SaveConfig();
            UpdateStatusCounts();
        }

        /// <summary>
        /// Removes a MIDI connection row.
        /// </summary>
        private void RemoveConnection_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ConnectionRow row)
            {
                row.Dispose();
                row.PropertyChanged -= Connection_PropertyChanged;
                Connections.Remove(row);
                SaveConfig();
                UpdateStatusCounts();
            }
        }

        /// <summary>
        /// Refreshes the list of MIDI devices and updates connections.
        /// </summary>
        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshDevicesAsync();
            EnsurePlaceholdersForMissingSelections();

            foreach (var c in Connections)
                _ = UpdateConnectionAsync(c);

            UpdateStatusCounts();
        }

        /// <summary>
        /// Toggles global mute for all connections.
        /// </summary>
        private void DisableAllButton_Click(object sender, RoutedEventArgs e)
        {
            GlobalMute = !GlobalMute;

            // Update button text according to mute state
            DisableAllButton.Content = GlobalMute ? "Enable all" : "Disable all";

            // Force all connections to re-evaluate (close or open as appropriate)
            foreach (var c in Connections)
                _ = UpdateConnectionAsync(c);

            UpdateStatusCounts();
        }


        /// <summary>
        /// Handles property changes in connection rows.
        /// </summary>
        private void Connection_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is ConnectionRow row &&
                (e.PropertyName is nameof(ConnectionRow.InId) ||
                 e.PropertyName is nameof(ConnectionRow.OutId) ||
                 e.PropertyName is nameof(ConnectionRow.IsInactive)))
            {
                _ = UpdateConnectionAsync(row);
                SaveConfig();
                UpdateStatusCounts();
            }
        }

        // ---------------- Theme Management ----------------

        /// <summary>
        /// Applies system theme (light/dark) based on Windows settings.
        /// </summary>
        private void AutoTheme()
        {
            if (IsLightTheme())
                ThemeManager.Current.ApplicationTheme = ApplicationTheme.Light;
            else
                ThemeManager.Current.ApplicationTheme = ApplicationTheme.Dark;
        }

        /// <summary>
        /// Checks if Windows is using light theme for apps.
        /// </summary>
        private static bool IsLightTheme()
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int i && i > 0;
        }

        /// <summary>
        /// Handles theme selection changes from the UI ComboBox.
        /// </summary>
        private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ThemeSelector.SelectedItem is ComboBoxItem item)
            {
                string themeChoice = item.Tag?.ToString();
                if (!string.IsNullOrEmpty(themeChoice))
                {
                    ApplyTheme(themeChoice);
                    SaveConfig(); // Auto-save config on theme change
                }
            }
        }

        // ---------------- Device Scan & Helpers ----------------

        /// <summary>
        /// Refreshes the list of MIDI input and output devices.
        /// </summary>
        private async Task RefreshDevicesAsync()
        {
            // Save previous selections to restore after refresh
            var previousSelections = Connections
                .Select(c => new { Conn = c, c.InId, c.OutId })
                .ToList();

            _inDevices.Clear();
            _outDevices.Clear();

            // Scan for MIDI input and output devices
            var inInfos = await DeviceInformation.FindAllAsync(MidiInPort.GetDeviceSelector());
            var outInfos = await DeviceInformation.FindAllAsync(MidiOutPort.GetDeviceSelector());

            foreach (var di in inInfos)
                _inDevices.Add(DeviceItem.FromDeviceInfo(di));

            // Map input device names by short ID for better output naming
            var inNameByShortId = _inDevices
                .Where(d => !string.IsNullOrEmpty(d.ShortId))
                .GroupBy(d => d.ShortId)
                .ToDictionary(g => g.Key, g => g.First().Name);

            foreach (var di in outInfos)
            {
                var dev = DeviceItem.FromDeviceInfo(di);
                if (dev.Name.Equals("MIDI", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(dev.ShortId) &&
                    inNameByShortId.TryGetValue(dev.ShortId, out var betterName))
                {
                    dev.Name = betterName;
                }
                _outDevices.Add(dev);
            }

            // Restore previous selections if still available
            foreach (var prev in previousSelections)
            {
                if (!string.IsNullOrEmpty(prev.InId) &&
                    _inDevices.Any(d => d.Id == prev.InId))
                    prev.Conn.InId = prev.InId;

                if (!string.IsNullOrEmpty(prev.OutId) &&
                    _outDevices.Any(d => d.Id == prev.OutId))
                    prev.Conn.OutId = prev.OutId;
            }

            // Restore keyboard output selection if still available
            if (_currentKeyboardOutPort != null)
            {
                try
                {
                    string currentId = _currentKeyboardOutPort.DeviceId;
                    if (_outDevices.Any(d => d.Id == currentId))
                        OutKeyboardCombo.SelectedValue = currentId;
                }
                catch
                {
                    // DeviceId may not be available -> ignore
                }
            }

            StatusText = $"Devices: {InDevices.Count} IN, {OutDevices.Count} OUT" + (GlobalMute ? " (all disabled)" : "");
        }

        /// <summary>
        /// Adds placeholder devices for missing selections in the UI.
        /// </summary>
        private void EnsurePlaceholdersForMissingSelections()
        {
            foreach (var c in Connections)
            {
                if (!string.IsNullOrWhiteSpace(c.InId) && !_inDevices.Any(d => d.Id == c.InId))
                    _inDevices.Add(DeviceItem.Placeholder(c.InId!, isOut: false));

                if (!string.IsNullOrWhiteSpace(c.OutId) && !_outDevices.Any(d => d.Id == c.OutId))
                    _outDevices.Add(DeviceItem.Placeholder(c.OutId!, isOut: true));
            }
        }

        // ---------------- MIDI Routing ----------------

        /// <summary>
        /// Opens or closes MIDI connections based on current state and global mute.
        /// </summary>
        private async Task UpdateConnectionAsync(ConnectionRow row)
        {
            try
            {
                // Close connection if muted, inactive, or missing device IDs
                if (GlobalMute || row.IsInactive || string.IsNullOrWhiteSpace(row.InId) || string.IsNullOrWhiteSpace(row.OutId))
                {
                    row.Close();
                    return;
                }

                // If already active and no need to reopen, skip
                if (row.IsActive && !row.NeedsReopen)
                    return;

                // If needs reopen, close first
                if (row.NeedsReopen)
                {
                    row.Close();
                    row.NeedsReopen = false;
                }

                if (row.IsActive) return;

                // Try to open MIDI ports
                var inPort = await MidiInPort.FromIdAsync(row.InId!);
                var outPort = await MidiOutPort.FromIdAsync(row.OutId!);

                if (inPort == null || outPort == null)
                    return;

                row.Open(inPort, outPort);
            }
            catch (Exception ex)
            {
                StatusText = $"Error opening: {ex.Message}";
            }
        }

        // ---------------- Configuration ----------------

        /// <summary>
        /// Loads configuration from file and restores connections and theme.
        /// </summary>
        private void LoadConfigAndRestore()
        {
            try
            {
                AppConfig cfg = new();

                if (File.Exists(_configPath))
                {
                    var json = File.ReadAllText(_configPath);
                    cfg = JsonSerializer.Deserialize<AppConfig>(json, _jsonOptions) ?? new AppConfig();
                }

                // If theme is missing, default to System
                if (string.IsNullOrEmpty(cfg.Theme))
                    cfg.Theme = "System";

                ApplyTheme(cfg.Theme);

                // Sync ComboBox selection with loaded theme
                foreach (var item in ThemeSelector.Items)
                {
                    if (item is ComboBoxItem cbi && (cbi.Tag?.ToString() == cfg.Theme))
                    {
                        ThemeSelector.SelectedItem = cbi;
                        break;
                    }
                }

                // Load MIDI connections from config
                Connections.Clear();
                foreach (var c in cfg.Connections)
                {
                    var row = new ConnectionRow
                    {
                        InId = c.InId,
                        OutId = c.OutId,
                        IsInactive = c.Inactive
                    };
                    row.PropertyChanged += Connection_PropertyChanged;
                    Connections.Add(row);
                }

                EnsurePlaceholdersForMissingSelections();
                UpdateStatusCounts();
            }
            catch (Exception ex)
            {
                StatusText = $"Error loading config: {ex.Message}";
            }
        }

        /// <summary>
        /// Saves configuration to file (debounced).
        /// </summary>
        private void SaveConfig()
        {
            // Debounce save for 500ms
            _saveTimer?.Dispose();
            _saveTimer = new Timer(_ =>
            {
                try
                {
                    var cfg = new AppConfig
                    {
                        Connections = Connections.Select(c => new MidiConnectionConfig
                        {
                            InId = c.InId ?? string.Empty,
                            OutId = c.OutId ?? string.Empty,
                            Inactive = c.IsInactive
                        }).ToList(),
                        Theme = CurrentTheme
                    };

                    var json = JsonSerializer.Serialize(cfg, _jsonOptions);
                    EnsureConfigDirectory();
                    File.WriteAllText(_configPath, json);
                }
                catch (Exception ex)
                {
                    StatusText = $"Error saving config: {ex.Message}";
                }
            }, null, 500, Timeout.Infinite);
        }
        private string CurrentTheme = "System";

        /// <summary>
        /// Applies the selected theme to the application.
        /// </summary>
        private void ApplyTheme(string theme)
        {
            CurrentTheme = theme;

            switch (theme)
            {
                case "Light":
                    ThemeManager.Current.ApplicationTheme = ApplicationTheme.Light;
                    break;

                case "Dark":
                    ThemeManager.Current.ApplicationTheme = ApplicationTheme.Dark;
                    break;

                case "System":
                    AutoTheme();
                    break;
            }
        }

        /// <summary>
        /// Updates the status bar with connection counts.
        /// </summary>
        private void UpdateStatusCounts()
        {
            int total = Connections.Count;
            int inactive = Connections.Count(c => c.IsInactive);
            int active = Connections.Count(c => c.IsActive);
            StatusText = $"Links: {total}" + (GlobalMute ? " (all disabled)" : "");
        }

        /// <summary>
        /// PropertyChanged event for INotifyPropertyChanged.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));

        /// <summary>
        /// Disposes all connections and timers.
        /// </summary>
        public void Dispose()
        {
            foreach (var c in Connections)
                c.Dispose();
            _saveTimer?.Dispose();
        }

        private void ToggleKeyboardPanel_Checked(object sender, RoutedEventArgs e)
        {
            OutKeyboardCombo.Visibility = Visibility.Visible;
            PianoPanel.Visibility = Visibility.Visible;
        }

        private void ToggleKeyboardPanel_Unchecked(object sender, RoutedEventArgs e)
        {
            OutKeyboardCombo.Visibility = Visibility.Hidden;
            PianoPanel.Visibility = Visibility.Collapsed;
        }

        private void PianoKey_Down(object sender, MouseButtonEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string noteStr && int.TryParse(noteStr, out int note) && ToggleKeyboardPanel.IsChecked == true)
            {
                SendNoteOn(note);
            }
        }

        private void PianoKey_Up(object sender, MouseButtonEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string noteStr && int.TryParse(noteStr, out int note) && ToggleKeyboardPanel.IsChecked == true)
            {
                SendNoteOff(note);
            }
        }

        private void SendNoteOn(int note, byte velocity = 100)
        {
            if (_currentKeyboardOutPort != null)
            {
                var noteOn = new MidiNoteOnMessage(0, (byte)note, velocity); // channel 0
                _currentKeyboardOutPort.SendMessage(noteOn);
            }
        }

        private void SendNoteOff(int note)
        {
            if (_currentKeyboardOutPort != null)
            {
                var noteOff = new MidiNoteOffMessage(0, (byte)note, 0);
                _currentKeyboardOutPort.SendMessage(noteOff);
            }
        }

        private async void OutKeyboardCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (OutKeyboardCombo.SelectedValue is string outId && !string.IsNullOrWhiteSpace(outId))
            {
                _currentKeyboardOutPort?.Dispose();
                _currentKeyboardOutPort = null;

                try
                {
                    _currentKeyboardOutPort = await MidiOutPort.FromIdAsync(outId);
                }
                catch (Exception ex)
                {
                    StatusText = $"Error opening output: {ex.Message}";
                }
            }
        }

    }

    /// <summary>
    /// Represents a MIDI device (input or output) or a placeholder for missing devices.
    /// </summary>
    public class DeviceItem
    {
        /// <summary>
        /// Device identifier string.
        /// </summary>
        public string Id { get; set; } = "";
        /// <summary>
        /// Device display name.
        /// </summary>
        public string Name { get; set; } = "";
        /// <summary>
        /// Short identifier extracted from device ID.
        /// </summary>
        public string ShortId { get; set; } = "";
        /// <summary>
        /// Display string for UI (includes short ID if available).
        /// </summary>
        public string Display => string.IsNullOrEmpty(ShortId) ? Name : $"{Name} [{ShortId}]";
        /// <summary>
        /// True if this is a placeholder device.
        /// </summary>
        public bool IsPlaceholder { get; set; }

        /// <summary>
        /// Creates a DeviceItem from a DeviceInformation object.
        /// </summary>
        public static DeviceItem FromDeviceInfo(DeviceInformation di) =>
            new()
            {
                Id = di.Id,
                Name = di.Name,
                ShortId = ExtractShortId(di.Id) ?? "",
                IsPlaceholder = false
            };

        /// <summary>
        /// Creates a placeholder DeviceItem for missing devices.
        /// </summary>
        public static DeviceItem Placeholder(string fullId, bool isOut) =>
            new()
            {
                Id = fullId,
                Name = isOut ? "NOT FOUND" : "NOT FOUND",
                ShortId = ExtractShortId(fullId) ?? "",
                IsPlaceholder = true
            };

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
    }

    /// <summary>
    /// Represents a MIDI connection row (link between input and output devices).
    /// </summary>
    public class ConnectionRow : INotifyPropertyChanged, IDisposable
    {
        private string? _inId;
        private string? _outId;
        private bool _inactive;
        private MidiInPort? _inPort;
        private IMidiOutPort? _outPort;

        private SolidColorBrush _arrowBrush = new SolidColorBrush(Colors.Gray);
        public SolidColorBrush ArrowBrush
        {
            get => _arrowBrush;
            set
            {
                _arrowBrush = value;
                OnPropertyChanged(nameof(ArrowBrush));
            }
        }
        /// <summary>
        /// Indicates if the connection needs to be reopened.
        /// </summary>
        public bool NeedsReopen { get; set; }

        /// <summary>
        /// MIDI input device ID.
        /// </summary>
        public string? InId
        {
            get => _inId;
            set
            {
                if (_inId != value)
                {
                    _inId = value;
                    NeedsReopen = true;
                    OnPropertyChanged(nameof(InId));
                    OnPropertyChanged(nameof(IsActive));
                }
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
                if (_outId != value)
                {
                    _outId = value;
                    NeedsReopen = true;
                    OnPropertyChanged(nameof(OutId));
                    OnPropertyChanged(nameof(IsActive));
                }
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
                if (_inactive != value)
                {
                    _inactive = value;
                    OnPropertyChanged(nameof(IsInactive));
                    OnPropertyChanged(nameof(ActiveLabel));
                    OnPropertyChanged(nameof(IsActive));
                }
            }
        }

        /// <summary>
        /// Label for the enable/disable button in the UI.
        /// </summary>
        public string ActiveLabel => IsInactive ? "Enable" : "Disable";
        /// <summary>
        /// True if the connection is active (not inactive and both ports are open).
        /// </summary>
        public bool IsActive => !IsInactive && _inPort != null && _outPort != null;

        /// <summary>
        /// Opens the MIDI connection (subscribes to input events).
        /// </summary>
        public void Open(MidiInPort inPort, IMidiOutPort outPort)
        {
            Close();
            _inPort = inPort;
            _outPort = outPort;
            _inPort.MessageReceived += InPort_MessageReceived;
            OnPropertyChanged(nameof(IsActive));
        }

        /// <summary>
        /// Handles incoming MIDI messages and routes them to the output port.
        /// </summary>

        private DispatcherTimer? _flashTimer;

        private void InPort_MessageReceived(MidiInPort sender, MidiMessageReceivedEventArgs args)
        {
            try
            {
                if (!IsInactive && _outPort != null)
                    _outPort.SendMessage(args.Message);

                Application.Current.Dispatcher.Invoke(() =>
                {
                    ArrowBrush.Color = Colors.Orange;

                    if (_flashTimer == null)
                    {
                        _flashTimer = new DispatcherTimer
                        {
                            Interval = TimeSpan.FromMilliseconds(500)
                        };
                        _flashTimer.Tick += (s, e) =>
                        {
                            ArrowBrush.Color = Colors.Gray;
                            _flashTimer.Stop();
                        };
                    }

                    // 🔄 Redémarrer le timer à chaque nouveau message
                    _flashTimer.Stop();
                    _flashTimer.Start();
                });
            }
            catch (Exception ex)
            {
                // Update status text in case of MIDI send error
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (Application.Current.MainWindow is MainWindow mw)
                        mw.StatusText = $"Error sending MIDI: {ex.Message}";
                });
            }
        }

        /// <summary>
        /// Closes the MIDI connection and disposes ports.
        /// </summary>
        public void Close()
        {
            if (_inPort != null)
            {
                try { _inPort.MessageReceived -= InPort_MessageReceived; } catch (Exception ex) { Application.Current.Dispatcher.Invoke(() => { if (Application.Current.MainWindow is MainWindow mw) mw.StatusText = $"Error detaching MIDI event: {ex.Message}"; }); }
                try { _inPort.Dispose(); } catch (Exception ex) { Application.Current.Dispatcher.Invoke(() => { if (Application.Current.MainWindow is MainWindow mw) mw.StatusText = $"Error disposing MIDI IN: {ex.Message}"; }); }
                _inPort = null;
            }
            if (_outPort != null)
            {
                try { _outPort.Dispose(); } catch (Exception ex) { Application.Current.Dispatcher.Invoke(() => { if (Application.Current.MainWindow is MainWindow mw) mw.StatusText = $"Error disposing MIDI OUT: {ex.Message}"; }); }
                _outPort = null;
            }
            OnPropertyChanged(nameof(IsActive));
        }

        /// <summary>
        /// Disposes the connection row.
        /// </summary>
        public void Dispose() => Close();

        /// <summary>
        /// PropertyChanged event for INotifyPropertyChanged.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string prop) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

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
        /// True if the connection is inactive (disabled).
        /// </summary>
        public bool Inactive { get; set; }
    }
}
