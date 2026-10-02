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
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Devices.Midi;

namespace MidiLink
{
    /// <summary>
    /// Main application window. Handles UI, configuration, MIDI routing, and theme management.
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged, IDisposable
    {
        private readonly MidiPortManager _ports = new();
        private readonly MidiDeviceService _devices;
        private readonly ConfigService _config = new();

        /// <summary>
        /// Available MIDI input devices.
        /// </summary>
        public ObservableCollection<DeviceItem> InDevices => _devices.InDevices;

        /// <summary>
        /// Available MIDI output devices.
        /// </summary>
        public ObservableCollection<DeviceItem> OutDevices => _devices.OutDevices;

        /// <summary>
        /// Collection of MIDI connection rows (links between input and output).
        /// </summary>
        public ObservableCollection<ConnectionRow> Connections { get; } = new();

        // Status text displayed in the UI.
        private string _statusText = "Ready";

        /// <summary>
        /// Global mute flag. If true, all MIDI routing is disabled.
        /// </summary>
        public bool GlobalMute { get; private set; }

        // Virtual keyboard output (shared with connections through the port manager)
        private OutPortLease? _keyboardLease;
        private int _keyboardVersion;

        // True while the config is being applied: avoids saving a half-loaded state
        private bool _loading;
        private bool _disposed;

        // System tray
        private readonly TrayIcon _trayIcon = new();
        private bool _minimizeToTray;

        /// <summary>
        /// Status text property for UI binding.
        /// </summary>
        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(nameof(StatusText)); }
        }

        /// <summary>
        /// Initializes the main window and sets up event handlers.
        /// </summary>
        public MainWindow()
        {
            _devices = new MidiDeviceService(Dispatcher);
            _devices.IsInUse = IsDeviceInUse;
            _devices.DeviceRemoved += id => _ports.Invalidate(id);
            _devices.DevicesChanged += OnDevicesChanged;
            // A Bluetooth device switched back on gets fresh opens, not the ones stuck while it was off
            _devices.BeforeBluetoothUpdate = _ports.ForceRetryStuckOpens;

            // A port that stopped working (e.g. Bluetooth timeout) is dropped by the manager: reopen it
            _ports.PortFailed += _ => Dispatcher.BeginInvoke(ReconnectAll);
            // ...and when it answers again, the "not responding" warnings go away
            _ports.DeviceRecovered += _ => Dispatcher.BeginInvoke(ReconnectAll);

            // Links that failed to open (slow Bluetooth device, busy port...) are retried regularly
            _retryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _retryTimer.Tick += (_, __) => RetryFailed();
            _retryTimer.Start();

            InitializeComponent();
            DataContext = this;
            AccentSelector.ItemsSource = AccentOption.All;

            _trayIcon.OpenRequested += RestoreFromTray;
            _trayIcon.ExitRequested += Close;
            StateChanged += (_, __) =>
            {
                if (WindowState == WindowState.Minimized && _minimizeToTray)
                    Hide(); // the tray icon stays to bring it back
            };

            // Launched at Windows startup: start minimized (in the tray if enabled)
            bool startMinimized = StartupManager.IsStartupLaunch(Environment.GetCommandLineArgs());
            if (startMinimized)
                WindowState = WindowState.Minimized;

            Loaded += (_, __) =>
            {
                try
                {
                    LoadConfigAndRestore();
                    StartupManager.RefreshPathIfEnabled();
                    _ = RefreshStartupSwitchAsync();
                    if (startMinimized && _minimizeToTray)
                        Hide();

                    _devices.Start(); // connections open once the devices are enumerated
                    StatusText = "Searching devices...";
                }
                catch (Exception ex)
                {
                    AppLog.Error("Error during startup", ex);
                    StatusText = $"Error during startup: {ex.Message}";
                }
            };

            Closed += (_, __) => Dispose();
        }

        // ---------------- Settings ----------------

        /// <summary>
        /// Registers / unregisters the application to launch at Windows startup.
        /// </summary>
        private async void StartupSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            StartupNote.Visibility = Visibility.Collapsed;
            try
            {
                var result = await StartupManager.SetEnabledAsync(StartupSwitch.IsOn);
                if (result != StartupChangeResult.Done)
                {
                    StartupNote.Text = result == StartupChangeResult.DisabledByUser
                        ? "Disabled in Windows. Enable MidiLink in Settings > Apps > Startup."
                        : "Managed by your organization's policy.";
                    StartupNote.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Cannot change startup setting", ex);
                StartupNote.Text = $"Cannot change this setting: {ex.Message}";
                StartupNote.Visibility = Visibility.Visible;
            }
            await RefreshStartupSwitchAsync(); // always show the real state
        }

        /// <summary>
        /// Sets the startup switch from Windows' actual state (it can also be changed in Windows settings).
        /// </summary>
        private async Task RefreshStartupSwitchAsync()
        {
            try
            {
                bool enabled = await StartupManager.IsEnabledAsync();
                _loading = true;
                StartupSwitch.IsOn = enabled;
            }
            catch (Exception ex)
            {
                AppLog.Error("Cannot read startup setting", ex);
            }
            finally
            {
                _loading = false;
            }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e) => _ = RefreshStartupSwitchAsync();

        /// <summary>
        /// Enables / disables minimizing to the system tray.
        /// </summary>
        private void TraySwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            SetMinimizeToTray(TraySwitch.IsOn);
            SaveConfig();
        }

        private void SetMinimizeToTray(bool enabled)
        {
            _minimizeToTray = enabled;
            _trayIcon.Visible = enabled;
        }

        /// <summary>
        /// Shows the window again (hidden in the tray, minimized, or behind other windows).
        /// </summary>
        public void RestoreFromTray()
        {
            Show();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
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
            row.Filter.Changed += SaveConfig;
            Connections.Add(row);
            UpdateConnection(row);
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
                row.PropertyChanged -= Connection_PropertyChanged;
                row.Filter.Changed -= SaveConfig;
                row.Dispose(); // gives the ports back; released in the background, the UI never waits
                Connections.Remove(row);
                _devices.PruneUnused();
                SaveConfig();
                UpdateStatusCounts();
            }
        }

        /// <summary>
        /// Rescans the MIDI devices and reopens every connection from scratch (like Disable / Enable):
        /// a port can look open while not working, e.g. a Bluetooth device opened while still connecting.
        /// </summary>
        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AppLog.Info("Refresh: reopening all connections");
                _ports.ForceRetryStuckOpens(); // the user asks for it: skip the waiting delays

                // Give every port back (they close once unused), then reopen through the rescan below
                foreach (var row in Connections)
                    row.Close();
                _keyboardLease?.Dispose();
                _keyboardLease = null;

                await _devices.RescanAsync(); // raises DevicesChanged, which reopens the connections
            }
            catch (Exception ex)
            {
                AppLog.Error("Error refreshing devices", ex);
                StatusText = $"Error refreshing devices: {ex.Message}";
            }
        }

        /// <summary>
        /// Toggles global mute for all connections.
        /// </summary>
        private void DisableAllButton_Click(object sender, RoutedEventArgs e)
        {
            GlobalMute = !GlobalMute;

            // Update button text according to mute state
            DisableAllButton.Content = GlobalMute ? "Enable all" : "Disable all";

            UpdateAllConnections();
        }

        /// <summary>
        /// Handles property changes in connection rows.
        /// </summary>
        private void Connection_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not ConnectionRow row) return;

            switch (e.PropertyName)
            {
                case nameof(ConnectionRow.InId):
                case nameof(ConnectionRow.OutId):
                    _devices.PruneUnused(); // the previous device may no longer be needed in the list
                    goto case nameof(ConnectionRow.IsInactive);

                case nameof(ConnectionRow.IsInactive):
                    UpdateConnection(row);
                    SaveConfig();
                    break;

                case nameof(ConnectionRow.State):
                    UpdateStatusCounts();
                    break;
            }
        }

        // ---------------- Filters ----------------

        /// <summary>
        /// Opens the Filters flyout of a link.
        /// </summary>
        private void Filters_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement button || button.DataContext is not ConnectionRow row) return;

            // x:Shared="False": each call gives a fresh flyout, bound to this link's filters
            var flyout = (ModernWpf.Controls.Flyout)FindResource("FiltersFlyout");
            if (flyout.Content is FrameworkElement content)
                content.DataContext = row.Filter;
            flyout.ShowAt(button);
        }

        private static MidiFilterSettings? FilterOf(object sender) =>
            (sender as FrameworkElement)?.DataContext as MidiFilterSettings;

        /// <summary>
        /// Transpose buttons: Tag is the step in semitones, "0" resets.
        /// </summary>
        private void TransposeButton_Click(object sender, RoutedEventArgs e)
        {
            if (FilterOf(sender) is not MidiFilterSettings filter) return;
            if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out int step))
                filter.Transpose = step == 0 ? 0 : filter.Transpose + step;
        }

        private void AllChannels_Click(object sender, RoutedEventArgs e) => FilterOf(sender)?.SetAllChannels(true);

        private void NoChannels_Click(object sender, RoutedEventArgs e) => FilterOf(sender)?.SetAllChannels(false);

        private void AddCcRemap_Click(object sender, RoutedEventArgs e) =>
            FilterOf(sender)?.CcRemaps.Add(new CcRemap { From = 1, To = 74 }); // mod wheel -> cutoff, a common one

        private void RemoveCcRemap_Click(object sender, RoutedEventArgs e)
        {
            // DataContext is the rule; Tag is bound to the filter owning the list
            if (sender is FrameworkElement { DataContext: CcRemap remap, Tag: MidiFilterSettings filter })
                filter.CcRemaps.Remove(remap);
        }

        private void ResetFilters_Click(object sender, RoutedEventArgs e) => FilterOf(sender)?.Reset();

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
                string? themeChoice = item.Tag?.ToString();
                if (!string.IsNullOrEmpty(themeChoice))
                {
                    ApplyTheme(themeChoice);
                    SaveConfig(); // Auto-save config on theme change
                }
            }
        }

        /// <summary>
        /// Handles accent color selection from the swatches.
        /// </summary>
        private void AccentSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AccentSelector.SelectedItem is AccentOption option)
            {
                ApplyAccent(option);
                SaveConfig();
            }
        }

        // "#RRGGBB", or "" for the Windows accent color
        private string _accentColor = "";

        private void ApplyAccent(AccentOption option)
        {
            _accentColor = option.Hex ?? "";
            ThemeManager.Current.AccentColor = option.Color; // null = follow Windows
        }

        // ---------------- Devices & MIDI Routing ----------------

        /// <summary>
        /// True if a device is selected by a connection or by the keyboard.
        /// </summary>
        private bool IsDeviceInUse(string id) =>
            Connections.Any(c => c.InId == id || c.OutId == id) ||
            Equals(OutKeyboardCombo?.SelectedValue, id);

        /// <summary>
        /// Called after the initial enumeration and on every plug / unplug / refresh.
        /// </summary>
        private void OnDevicesChanged()
        {
            RemapMovedDevices();
            UpdateAllConnections();

            // Default keyboard output: first connected device of the list
            if (OutKeyboardCombo.SelectedValue == null)
            {
                var first = OutDevices.FirstOrDefault(d => d.IsAvailable);
                if (first != null)
                    OutKeyboardCombo.SelectedValue = first.Id;
            }
            _ = EnsureKeyboardPortAsync();

            SaveConfig(); // records device names, used to find a device again if its ID changes
        }

        /// <summary>
        /// A device plugged into another USB port gets a new ID: if a saved device is missing
        /// and exactly one connected device has the same name, use that one instead.
        /// </summary>
        private void RemapMovedDevices()
        {
            foreach (var row in Connections)
            {
                var newIn = FindByName(row.InId, isOut: false);
                if (newIn != null) row.InId = newIn;

                var newOut = FindByName(row.OutId, isOut: true);
                if (newOut != null) row.OutId = newOut;
            }
        }

        private string? FindByName(string? id, bool isOut)
        {
            var missing = _devices.Find(id, isOut);
            if (missing == null || missing.IsAvailable) return null;

            var candidates = (isOut ? OutDevices : InDevices)
                .Where(d => d.IsAvailable && d.Name == missing.Name)
                .ToList();
            if (candidates.Count != 1) return null;

            AppLog.Info($"Device '{missing.Name}' found under a new ID: {candidates[0].Id}");
            return candidates[0].Id;
        }

        /// <summary>
        /// Opens or closes a connection according to its settings (no-op before devices are known).
        /// </summary>
        private void UpdateConnection(ConnectionRow row)
        {
            if (!_devices.IsEnumerated || _disposed) return;
            _ = row.UpdateAsync(_ports, _devices, GlobalMute);
        }

        private readonly DispatcherTimer _retryTimer;

        /// <summary>
        /// After a port failure: every link / the keyboard using a dead port reopens it.
        /// </summary>
        private void ReconnectAll()
        {
            if (_disposed) return;
            UpdateAllConnections();
            _ = EnsureKeyboardPortAsync();
            UpdateKeyboardWarning();
        }

        /// <summary>
        /// Orange icon next to the keyboard output when that device does not answer.
        /// </summary>
        private void UpdateKeyboardWarning()
        {
            var id = OutKeyboardCombo.SelectedValue as string;
            bool show = PianoPanel.Visibility == Visibility.Visible
                        && _devices.IsAvailable(id, isOut: true)
                        && !_ports.IsResponding(id);
            KeyboardWarning.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Retries the links in error and the keyboard output (no-op for what already works).
        /// </summary>
        private void RetryFailed()
        {
            if (_disposed || !_devices.IsEnumerated) return;
            foreach (var row in Connections.Where(c => c.State == ConnectionState.Error))
                UpdateConnection(row);
            _ = EnsureKeyboardPortAsync();
        }

        private void UpdateAllConnections()
        {
            foreach (var c in Connections)
                UpdateConnection(c);
            UpdateStatusCounts();
        }

        // ---------------- Configuration ----------------

        /// <summary>
        /// Loads configuration from file and restores connections and theme.
        /// </summary>
        private void LoadConfigAndRestore()
        {
            _loading = true;
            try
            {
                AppConfig cfg = _config.Load();

                // If theme is missing, default to System
                if (string.IsNullOrEmpty(cfg.Theme))
                    cfg.Theme = "System";

                ApplyTheme(cfg.Theme);

                // Other settings
                var accent = AccentOption.FromHex(cfg.AccentColor);
                ApplyAccent(accent);
                AccentSelector.SelectedItem = accent;

                SetMinimizeToTray(cfg.MinimizeToTray);
                TraySwitch.IsOn = cfg.MinimizeToTray;

                // Sync ComboBox selection with loaded theme
                foreach (var item in ThemeSelector.Items)
                {
                    if (item is ComboBoxItem cbi && (cbi.Tag?.ToString() == cfg.Theme))
                    {
                        ThemeSelector.SelectedItem = cbi;
                        break;
                    }
                }

                // Load MIDI connections from config. Devices are shown as "not connected"
                // until the device watcher finds them.
                Connections.Clear();
                foreach (var c in cfg.Connections)
                {
                    _devices.EnsurePlaceholder(c.InId, c.InName, isOut: false);
                    _devices.EnsurePlaceholder(c.OutId, c.OutName, isOut: true);

                    var row = new ConnectionRow
                    {
                        InId = string.IsNullOrWhiteSpace(c.InId) ? null : c.InId,
                        OutId = string.IsNullOrWhiteSpace(c.OutId) ? null : c.OutId,
                        IsInactive = c.Inactive
                    };
                    if (c.Filter != null)
                        row.Filter.Load(c.Filter);
                    row.PropertyChanged += Connection_PropertyChanged;
                    row.Filter.Changed += SaveConfig;
                    Connections.Add(row);
                }

                UpdateStatusCounts();
            }
            finally
            {
                _loading = false;
            }
        }

        /// <summary>
        /// Saves configuration (snapshot taken here on the UI thread, written in the background).
        /// </summary>
        private void SaveConfig()
        {
            if (_loading || _disposed) return;

            _config.Save(new AppConfig
            {
                Connections = Connections.Select(c => new MidiConnectionConfig
                {
                    InId = c.InId ?? string.Empty,
                    OutId = c.OutId ?? string.Empty,
                    InName = _devices.Find(c.InId, isOut: false)?.Name ?? string.Empty,
                    OutName = _devices.Find(c.OutId, isOut: true)?.Name ?? string.Empty,
                    Inactive = c.IsInactive,
                    Filter = c.Filter.ToConfig()
                }).ToList(),
                Theme = CurrentTheme,
                MinimizeToTray = _minimizeToTray,
                AccentColor = _accentColor
            });
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
            int active = Connections.Count(c => c.IsActive);
            StatusText = $"Links: {Connections.Count}, connected: {active}" + (GlobalMute ? " (all disabled)" : "");
        }

        /// <summary>
        /// PropertyChanged event for INotifyPropertyChanged.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));

        /// <summary>
        /// Stops device watching, closes every port and writes pending config.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _retryTimer.Stop();

            _devices.Dispose();
            foreach (var c in Connections)
                c.Dispose();
            _keyboardLease?.Dispose();
            _keyboardLease = null;
            _ports.Dispose(); // synchronous on shutdown
            _config.Dispose();
            _trayIcon.Dispose();
        }

        private void ToggleKeyboardPanel_Checked(object sender, RoutedEventArgs e)
        {
            OutKeyboardCombo.Visibility = Visibility.Visible;
            PianoPanel.Visibility = Visibility.Visible;
            UpdateKeyboardWarning();
        }

        private void ToggleKeyboardPanel_Unchecked(object sender, RoutedEventArgs e)
        {
            OutKeyboardCombo.Visibility = Visibility.Hidden;
            PianoPanel.Visibility = Visibility.Collapsed;
            UpdateKeyboardWarning();
        }

        // --- fields for mouse / touch management ---
        private bool _isMouseDown = false;
        private Button? _currentMouseButton = null;                 // active button for mouse
        private readonly Dictionary<int, Button> _touchActive = new(); // touchId -> active button

        // --- UTILITY: find the Button under a point relative to PianoPanel ---
        private Button? GetButtonAt(Point relativePoint)
        {
            var hit = PianoPanel.InputHitTest(relativePoint) as DependencyObject;
            while (hit != null && !(hit is Button))
            {
                hit = VisualTreeHelper.GetParent(hit);
            }
            return hit as Button;
        }

        // --- UTILITY: activate / deactivate visual and send MIDI messages ---
        private void PressButton(Button btn)
        {
            if (btn.Tag is string s && int.TryParse(s, out int note))
            {
                var bd = btn.Template?.FindName("Bd", btn) as Border;
                var isBlack = IsBlackKeyFromNote(btn.Tag.ToString());
                if (bd != null)
                {
                    bd.RenderTransform = new ScaleTransform(1.0, 0.95);
                    bd.Background = isBlack ? new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20))
                                             : new SolidColorBrush(Color.FromRgb(0xd0, 0xd0, 0xd0));
                }
                // send note on
                SendNoteOn(note);
            }
        }

        private void ReleaseButton(Button btn)
        {
            if (btn.Tag is string s && int.TryParse(s, out int note))
            {
                var bd = btn.Template?.FindName("Bd", btn) as Border;
                var isBlack = IsBlackKeyFromNote(btn.Tag.ToString());
                if (bd != null)
                {
                    bd.RenderTransform = new ScaleTransform(1.0, 1.0);
                    bd.Background = isBlack ? new SolidColorBrush(Colors.Black)
                                             : new SolidColorBrush(Colors.White);
                }

                // send note off
                SendNoteOff(note);
            }
        }
        private bool IsBlackKeyFromNote(String? tag)
        {
            if (string.IsNullOrEmpty(tag))
                return false;
            if (!int.TryParse(tag, out int note)) return false;
            int pc = note % 12;
            return pc == 1 || pc == 3 || pc == 6 || pc == 8 || pc == 10;
        }

        // ---------------- Mouse handling ----------------
        private void PianoPanel_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isMouseDown = true;
            Mouse.Capture(PianoPanel); // capture mouse on panel
            UpdateMouseKey(e.GetPosition(PianoPanel));
            e.Handled = true;
        }

        private void PianoPanel_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isMouseDown) return;
            UpdateMouseKey(e.GetPosition(PianoPanel));
        }

        private void PianoPanel_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_currentMouseButton != null)
            {
                ReleaseButton(_currentMouseButton);
                _currentMouseButton = null;
            }
            _isMouseDown = false;
            Mouse.Capture(null);
            e.Handled = true;
        }

        private void UpdateMouseKey(Point pos)
        {
            var btn = GetButtonAt(pos);
            if (btn == _currentMouseButton) return; // same key => nothing to do

            // release previous key
            if (_currentMouseButton != null)
                ReleaseButton(_currentMouseButton);

            // activate new key if present
            if (btn != null)
                PressButton(btn);

            _currentMouseButton = btn;
        }

        // ---------------- Touch handling (multi-touch supported) ----------------
        private void PianoPanel_TouchDown(object sender, TouchEventArgs e)
        {
            var touchId = e.TouchDevice.Id;
            var pos = e.GetTouchPoint(PianoPanel).Position;
            var btn = GetButtonAt(pos);
            if (btn != null)
            {
                // associate this touch to this button and send note on
                _touchActive[touchId] = btn;
                PressButton(btn);

                // capture the touch to follow its movement on the panel
                PianoPanel.CaptureTouch(e.TouchDevice);
            }
            e.Handled = true;
        }

        private void PianoPanel_TouchMove(object sender, TouchEventArgs e)
        {
            var touchId = e.TouchDevice.Id;
            var pos = e.GetTouchPoint(PianoPanel).Position;
            var btn = GetButtonAt(pos);

            // if we already have a key associated to this touchId
            if (_touchActive.TryGetValue(touchId, out var previousBtn))
            {
                if (previousBtn != btn)
                {
                    // key changed: off previous, on new
                    ReleaseButton(previousBtn);
                    _touchActive.Remove(touchId);

                    if (btn != null)
                    {
                        _touchActive[touchId] = btn;
                        PressButton(btn);
                    }
                }
            }
            else
            {
                // no association yet, if we move over a button, activate it
                if (btn != null)
                {
                    _touchActive[touchId] = btn;
                    PressButton(btn);
                }
            }
            e.Handled = true;
        }

        private void PianoPanel_TouchUp(object sender, TouchEventArgs e)
        {
            var touchId = e.TouchDevice.Id;
            if (_touchActive.TryGetValue(touchId, out var btn))
            {
                ReleaseButton(btn);
                _touchActive.Remove(touchId);
            }
            try { PianoPanel.ReleaseTouchCapture(e.TouchDevice); } catch { }
            e.Handled = true;
        }

        private void SendNoteOn(int note, byte velocity = 100)
        {
            SendKeyboardMessage(new MidiNoteOnMessage(0, (byte)note, velocity)); // channel 0
        }

        private void SendNoteOff(int note)
        {
            SendKeyboardMessage(new MidiNoteOffMessage(0, (byte)note, 0));
        }

        private void SendKeyboardMessage(IMidiMessage message)
        {
            try
            {
                _keyboardLease?.Send(message);
            }
            catch (Exception ex)
            {
                // Device unplugged or not responding: never crash while playing
                AppLog.Error("Error sending keyboard MIDI", ex);
                StatusText = $"Keyboard output unavailable: {ex.Message}";
            }
        }

        private void OutKeyboardCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _devices.PruneUnused();
            _ = EnsureKeyboardPortAsync();
            UpdateKeyboardWarning();
        }

        /// <summary>
        /// Makes sure the keyboard holds a live lease on the selected output (reopens after a replug).
        /// </summary>
        private async System.Threading.Tasks.Task EnsureKeyboardPortAsync()
        {
            if (_disposed) return;

            var outId = OutKeyboardCombo.SelectedValue as string;
            if (_keyboardLease is { IsAlive: true } && _keyboardLease.DeviceId == outId)
                return;

            int version = ++_keyboardVersion;
            _keyboardLease?.Dispose();
            _keyboardLease = null;

            if (string.IsNullOrWhiteSpace(outId) || !_devices.IsAvailable(outId, isOut: true) || !_devices.IsReachable(outId, isOut: true))
                return;

            try
            {
                var lease = await _ports.AcquireOutAsync(outId);

                // Selection changed while the port was opening
                if (version != _keyboardVersion || _disposed)
                {
                    lease.Dispose();
                    return;
                }
                _keyboardLease = lease;
            }
            catch (Exception ex)
            {
                if (version == _keyboardVersion)
                    StatusText = $"Error opening keyboard output: {ex.Message}";
            }
        }

    }
}
