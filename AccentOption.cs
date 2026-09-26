/*
 * MidiLink - AccentOption.cs
 * Copyright (c) 2025 melho
 * Licensed under GPL v3
 * Description: Accent colors offered in the settings menu.
 */
using System;
using System.Collections.Generic;
using System.Windows.Media;
using Windows.UI.ViewManagement;

namespace MidiLink
{
    /// <summary>
    /// One swatch of the accent color picker. <see cref="Hex"/> is null for "use the Windows accent color".
    /// </summary>
    public sealed class AccentOption
    {
        private AccentOption(string name, string? hex, Color color)
        {
            Name = name;
            Hex = hex;
            Swatch = new SolidColorBrush(color);
            Swatch.Freeze();
        }

        public string Name { get; }
        public string? Hex { get; }
        public SolidColorBrush Swatch { get; }
        public bool IsSystem => Hex == null;

        /// <summary>
        /// Color to give to ModernWpf's ThemeManager (null = follow Windows).
        /// </summary>
        public Color? Color => Hex == null ? null : (Color)ColorConverter.ConvertFromString(Hex);

        /// <summary>
        /// "System" first, then colors from the Windows accent palette.
        /// </summary>
        public static IReadOnlyList<AccentOption> All { get; } = new List<AccentOption>
        {
            new("System (Windows accent color)", null, SystemAccentColor()),
            Preset("Blue", "#0078D4"),
            Preset("Indigo", "#6B69D6"),
            Preset("Purple", "#8764B8"),
            Preset("Orchid", "#B146C2"),
            Preset("Pink", "#E3008C"),
            Preset("Raspberry", "#C30052"),
            Preset("Red", "#E81123"),
            Preset("Orange", "#F7630C"),
            Preset("Gold", "#FFB900"),
            Preset("Green", "#10893E"),
            Preset("Teal", "#00B294"),
            Preset("Steel", "#2D7D9A"),
            Preset("Gray", "#767676"),
        };

        /// <summary>
        /// The option matching a saved value ("" or unknown = System).
        /// </summary>
        public static AccentOption FromHex(string? hex)
        {
            foreach (var option in All)
                if (option.Hex != null && string.Equals(option.Hex, hex, StringComparison.OrdinalIgnoreCase))
                    return option;
            return All[0];
        }

        private static AccentOption Preset(string name, string hex) =>
            new(name, hex, (Color)ColorConverter.ConvertFromString(hex));

        private static Color SystemAccentColor()
        {
            try
            {
                var c = new UISettings().GetColorValue(UIColorType.Accent);
                return System.Windows.Media.Color.FromArgb(c.A, c.R, c.G, c.B);
            }
            catch
            {
                return Colors.SteelBlue;
            }
        }
    }
}
