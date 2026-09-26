/*
 * MidiLink - TrayIcon.cs
 * Copyright (c) 2025 melho
 * Licensed under GPL v3
 * Description: System tray icon (notification area) with Open / Exit menu.
 */
using System;
using System.Drawing;
using Forms = System.Windows.Forms;

namespace MidiLink
{
    /// <summary>
    /// Icon in the Windows notification area. Double-click or "Open" restores the window.
    /// </summary>
    public sealed class TrayIcon : IDisposable
    {
        private readonly Forms.NotifyIcon _icon;

        /// <summary>
        /// Raised when the user asks to show the window.
        /// </summary>
        public event Action? OpenRequested;

        /// <summary>
        /// Raised when the user asks to quit the application.
        /// </summary>
        public event Action? ExitRequested;

        public TrayIcon()
        {
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Open", null, (_, __) => OpenRequested?.Invoke());
            menu.Items.Add("Exit", null, (_, __) => ExitRequested?.Invoke());

            _icon = new Forms.NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = "MIDILINK",
                ContextMenuStrip = menu,
                Visible = false
            };
            _icon.MouseClick += (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left)
                    OpenRequested?.Invoke();
            };
        }

        /// <summary>
        /// Shows or hides the icon in the notification area.
        /// </summary>
        public bool Visible
        {
            get => _icon.Visible;
            set => _icon.Visible = value;
        }

        private static Icon LoadAppIcon()
        {
            try
            {
                // Same icon as the executable (ApplicationIcon in the .csproj)
                if (Environment.ProcessPath is string exe)
                    return Icon.ExtractAssociatedIcon(exe) ?? SystemIcons.Application;
            }
            catch (Exception ex)
            {
                AppLog.Error("Cannot load tray icon", ex);
            }
            return SystemIcons.Application;
        }

        public void Dispose()
        {
            _icon.Visible = false; // otherwise a ghost icon stays until the mouse hovers it
            _icon.ContextMenuStrip?.Dispose();
            _icon.Dispose();
        }
    }
}
