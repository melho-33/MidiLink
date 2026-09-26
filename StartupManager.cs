/*
 * MidiLink - StartupManager.cs
 * Copyright (c) 2025 melho
 * Licensed under GPL v3
 * Description: Launch at Windows startup (StartupTask for the Store package, "Run" registry key otherwise).
 */
using Microsoft.Win32;
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;

namespace MidiLink
{
    /// <summary>
    /// Result of a request to change the startup setting.
    /// </summary>
    public enum StartupChangeResult
    {
        Done,
        /// <summary>The user disabled it in Windows (Task Manager / Settings > Apps > Startup): only they can re-enable it.</summary>
        DisabledByUser,
        /// <summary>Blocked by a group policy.</summary>
        DisabledByPolicy
    }

    /// <summary>
    /// Registers the application to start with the user session.
    /// - Store (MSIX) version: StartupTask declared in Package.appxmanifest. In MSIX, writes to the
    ///   HKCU "Run" key are redirected to a private copy of the registry, so it would have no effect.
    /// - Unpackaged version: HKCU\...\Run registry key.
    /// Windows is the source of truth: nothing is stored in config.json.
    /// </summary>
    public static class StartupManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "MidiLink";

        // Must match uap5:StartupTask TaskId in MIDILINK.Package\Package.appxmanifest
        private const string TaskId = "MidiLinkStartup";

        /// <summary>
        /// Command-line argument added when launched at startup (unpackaged version): the app starts minimized.
        /// </summary>
        public const string MinimizedArg = "--minimized";

        private static readonly Lazy<bool> _isPackaged = new(() =>
        {
            try { return Package.Current != null; }
            catch { return false; } // throws when the app has no package identity
        });

        /// <summary>
        /// True when running as the Store / MSIX package.
        /// </summary>
        public static bool IsPackaged => _isPackaged.Value;

        /// <summary>
        /// True if this launch was triggered by Windows startup (the app should start minimized).
        /// </summary>
        public static bool IsStartupLaunch(string[] args)
        {
            if (args.Contains(MinimizedArg, StringComparer.OrdinalIgnoreCase))
                return true;

            if (!IsPackaged) return false;
            try
            {
                return AppInstance.GetActivatedEventArgs()?.Kind == ActivationKind.StartupTask;
            }
            catch (Exception ex)
            {
                AppLog.Error("Cannot read activation kind", ex);
                return false;
            }
        }

        /// <summary>
        /// True if the application is set to start with Windows.
        /// </summary>
        public static async Task<bool> IsEnabledAsync()
        {
            if (IsPackaged)
            {
                var task = await StartupTask.GetAsync(TaskId);
                return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            }

            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }

        /// <summary>
        /// Enables or disables the launch at startup. Throws if the registry cannot be written.
        /// </summary>
        public static async Task<StartupChangeResult> SetEnabledAsync(bool enabled)
        {
            if (IsPackaged)
            {
                var task = await StartupTask.GetAsync(TaskId);
                if (!enabled)
                {
                    if (task.State == StartupTaskState.EnabledByPolicy) return StartupChangeResult.DisabledByPolicy;
                    task.Disable();
                    return StartupChangeResult.Done;
                }

                // May show a Windows confirmation; refused if the user disabled it in Windows settings
                var state = await task.RequestEnableAsync();
                return state switch
                {
                    StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy => StartupChangeResult.Done,
                    StartupTaskState.DisabledByPolicy => StartupChangeResult.DisabledByPolicy,
                    _ => StartupChangeResult.DisabledByUser
                };
            }

            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
                key.SetValue(ValueName, Command);
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            return StartupChangeResult.Done;
        }

        /// <summary>
        /// Unpackaged version: if enabled, points the entry to the current executable (in case the app was moved).
        /// </summary>
        public static void RefreshPathIfEnabled()
        {
            if (IsPackaged) return; // the package location is managed by Windows
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                if (key?.GetValue(ValueName) is string current && current != Command)
                    key.SetValue(ValueName, Command);
            }
            catch (Exception ex)
            {
                AppLog.Error("Cannot update startup entry", ex);
            }
        }

        private static string Command => $"\"{Environment.ProcessPath}\" {MinimizedArg}";
    }
}
