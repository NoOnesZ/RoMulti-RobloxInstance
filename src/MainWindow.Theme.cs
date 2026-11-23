using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace RoMulti
{
    public sealed partial class MainWindow
    {
        private const string AppFontName = "Times New Roman";
        private const string ThemeRegistryPath = @"Software\RoMulti";
        private static readonly FontFamily AppFont = new FontFamily(AppFontName);
        private static readonly SolidColorBrush ActionBlue = MakeColorBrush("#2357B8");

        private readonly List<ThemeColor> _themeColors = new List<ThemeColor>();
        private readonly SolidColorBrush BgCanvas;
        private readonly SolidColorBrush BgSurface;
        private readonly SolidColorBrush BgElevated;
        private readonly SolidColorBrush BgHover;
        private readonly SolidColorBrush BorderSubtle;
        private readonly SolidColorBrush TextPrimary;
        private readonly SolidColorBrush TextSecondary;
        private readonly SolidColorBrush AccentBlue;
        private readonly SolidColorBrush AccentBlueBg;
        private readonly SolidColorBrush TealAccent;
        private readonly SolidColorBrush TealBg;
        private readonly SolidColorBrush StatusGreen;
        private readonly SolidColorBrush StatusGreenBg;
        private readonly SolidColorBrush StatusAmber;
        private readonly SolidColorBrush StatusAmberBg;
        private readonly SolidColorBrush StatusRed;
        private readonly SolidColorBrush StatusRedBg;
        private bool _darkTheme;

        private sealed class ThemeColor : DependencyObject
        {
            private static readonly DependencyProperty CurrentColorProperty = DependencyProperty.Register(
                "CurrentColor", typeof(Color), typeof(ThemeColor));

            private readonly Color _dark;
            private readonly Color _light;
            public SolidColorBrush Brush { get; private set; }

            public ThemeColor(string dark, string light)
            {
                _dark = (Color)ColorConverter.ConvertFromString(dark);
                _light = (Color)ColorConverter.ConvertFromString(light);
                Brush = new SolidColorBrush();
                // A bound brush stays live when WPF seals styles and cell templates.
                BindingOperations.SetBinding(Brush, SolidColorBrush.ColorProperty, new Binding
                {
                    Source = this,
                    Path = new PropertyPath(CurrentColorProperty),
                    Mode = BindingMode.OneWay
                });
            }

            public void Apply(bool dark)
            {
                SetValue(CurrentColorProperty, dark ? _dark : _light);
            }
        }

        private SolidColorBrush AddThemeColor(string dark, string light)
        {
            var color = new ThemeColor(dark, light);
            _themeColors.Add(color);
            return color.Brush;
        }

        private static SolidColorBrush MakeColorBrush(string value)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            brush.Freeze();
            return brush;
        }

        private void ApplyTheme(bool dark)
        {
            _darkTheme = dark;
            foreach (var color in _themeColors)
                color.Apply(dark);

            NativeMethods.SetTitleBarTheme(new WindowInteropHelper(this).Handle, dark);
        }

        private static bool ReadDarkTheme()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(ThemeRegistryPath))
                    return key == null || !string.Equals(key.GetValue("Theme") as string, "Light", StringComparison.Ordinal);
            }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
            catch (SecurityException) { return true; }
        }

        private void ChangeTheme(bool dark)
        {
            if (_darkTheme == dark) return;
            ApplyTheme(dark);
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(ThemeRegistryPath))
                    key.SetValue("Theme", dark ? "Dark" : "Light", RegistryValueKind.String);
            }
            catch (IOException) { Log("WARN", "Could not save the theme preference."); }
            catch (UnauthorizedAccessException) { Log("WARN", "Could not save the theme preference."); }
            catch (SecurityException) { Log("WARN", "Could not save the theme preference."); }
        }

        private StackPanel BuildThemePicker()
        {
            var picker = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            picker.Children.Add(new TextBlock
            {
                Text = "Theme",
                Foreground = TextSecondary,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });
            picker.Children.Add(CreateThemeOption("Dark", true));
            picker.Children.Add(CreateThemeOption("Light", false));
            return picker;
        }

        private RadioButton CreateThemeOption(string label, bool dark)
        {
            var option = new RadioButton
            {
                Content = label,
                GroupName = "AppTheme",
                IsChecked = _darkTheme == dark,
                FontSize = 14,
                MinWidth = 60,
                MinHeight = 30,
                Padding = new Thickness(9, 4, 9, 4),
                Margin = new Thickness(4, 0, 0, 0),
                BorderThickness = new Thickness(1)
            };
            System.Windows.Automation.AutomationProperties.SetName(option, label + " theme");
            var style = new Style(typeof(RadioButton));
            style.Setters.Add(new Setter(Control.BackgroundProperty, BgElevated));
            style.Setters.Add(new Setter(Control.ForegroundProperty, TextPrimary));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, BorderSubtle));
            var selected = new Trigger { Property = RadioButton.IsCheckedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, AccentBlueBg));
            selected.Setters.Add(new Setter(Control.ForegroundProperty, AccentBlue));
            selected.Setters.Add(new Setter(Control.BorderBrushProperty, AccentBlue));
            selected.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Bold));
            style.Triggers.Add(selected);
            option.Style = style;
            ApplyRoundedButtonTemplate(option);
            option.Checked += (s, e) => ChangeTheme(dark);
            return option;
        }
    }
}
