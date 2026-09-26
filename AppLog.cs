/*
 * MidiLink - AppLog.cs
 * Copyright (c) 2025 melho
 * Licensed under GPL v3
 * Description: Minimal thread-safe file logger (%LocalAppData%\MidiLink\midilink.log).
 */
using System;
using System.IO;

namespace MidiLink
{
    /// <summary>
    /// Appends timestamped lines to a log file. Never throws.
    /// </summary>
    public static class AppLog
    {
        private const long MaxSize = 1_000_000; // rotate to .old beyond ~1 MB

        private static readonly object _lock = new();

        /// <summary>
        /// Full path of the log file.
        /// </summary>
        public static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MidiLink",
            "midilink.log");

        public static void Info(string message) => Write("INFO ", message);

        public static void Error(string message, Exception? ex = null) =>
            Write("ERROR", ex == null ? message : $"{message}: {ex}");

        private static void Write(string level, string message)
        {
            try
            {
                lock (_lock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);

                    var file = new FileInfo(LogPath);
                    if (file.Exists && file.Length > MaxSize)
                        File.Move(LogPath, LogPath + ".old", overwrite: true);

                    File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // Logging must never break the application
            }
        }
    }
}
