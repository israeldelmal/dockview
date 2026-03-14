using System;
using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace Dockview;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Apply theme matching the current Windows system setting
        ApplyTheme(IsSystemDarkMode());

        // Listen for theme changes while the app is running
        SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category == UserPreferenceCategory.General)
                ApplyTheme(IsSystemDarkMode());
        };
    }

    private static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int i && i == 0;
        }
        catch
        {
            return false;
        }
    }

    internal static void ApplyTheme(bool isDark)
    {
        var dict = Current.Resources.MergedDictionaries
            .FirstOrDefault(d => d.Source?.OriginalString.Contains("Brushes") == true);

        if (dict is null) return;

        // Swap semantic brush values without reloading the resource dictionary
        if (isDark)
        {
            dict["AppBackground"]        = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0F, 0x0F, 0x0F));
            dict["SurfaceBackground"]    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0x1A, 0x1A, 0x1A));
            dict["OverlayBackground"]    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xD0, 0x10, 0x10, 0x10));
            dict["PrimaryText"]          = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF2, 0xF2, 0xF2));
            dict["SecondaryText"]        = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8A, 0x8A, 0x8A));
            dict["AccentBrush"]          = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0xAA, 0xFF));
            dict["HoverBackground"]      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
            dict["BorderBrush"]          = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            dict["ComboBoxBackground"]   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x99, 0x20, 0x20, 0x20));
            dict["SliderTrackBrush"]     = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x40, 0x40, 0x40));
        }
        else
        {
            dict["AppBackground"]        = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF3, 0xF3, 0xF3));
            dict["SurfaceBackground"]    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
            dict["OverlayBackground"]    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xD8, 0xF0, 0xF0, 0xF0));
            dict["PrimaryText"]          = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1A, 0x1A));
            dict["SecondaryText"]        = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x60, 0x60));
            dict["AccentBrush"]          = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0x78, 0xD4));
            dict["HoverBackground"]      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x22, 0x00, 0x00, 0x00));
            dict["BorderBrush"]          = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x44, 0x00, 0x00, 0x00));
            dict["ComboBoxBackground"]   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
            dict["SliderTrackBrush"]     = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xCC, 0xCC));
        }
    }
}
