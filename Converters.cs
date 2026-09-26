/*
 * MidiLink - Converters.cs
 * Copyright (c) 2025 melho
 * Licensed under GPL v3
 * Description: Small XAML value converters.
 */
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MidiLink
{
    /// <summary>
    /// Visible when the value is 0 (e.g. an "empty list" hint bound to Count), collapsed otherwise.
    /// </summary>
    public class ZeroToVisibleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is int count && count == 0 ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
