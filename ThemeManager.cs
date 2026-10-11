using Microsoft.Win32;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace NeClipboardWin;

internal static class ThemeManager
{
    internal static bool? QaLightThemeOverride { get; set; }
    private const string PersonalizeKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize";

    private static readonly IReadOnlyDictionary<string, string> DarkPalette = new Dictionary<string, string>
    {
        ["AppBackgroundBrush"] = "#101416",
        ["SurfaceBrush"] = "#161D22",
        ["SurfaceRaisedBrush"] = "#1B242B",
        ["SurfaceMutedBrush"] = "#12191E",
        ["BorderBrush"] = "#2C3943",
        ["BorderStrongBrush"] = "#3B4B57",
        ["TextBrush"] = "#F3F7F8",
        ["MutedBrush"] = "#AAB8C2",
        ["SubtleTextBrush"] = "#99A9B5",
        ["AccentBrush"] = "#75E2D0",
        ["AccentSoftBrush"] = "#193A35",
        ["AccentTextBrush"] = "#062D29",
        ["AccentBorderBrush"] = "#3B887D",
        ["HoverBrush"] = "#243039",
        ["PressedBrush"] = "#11191D",
        ["SelectionBrush"] = "#1B3936",
        ["DangerBrush"] = "#FFC2C5",
        ["DangerBorderBrush"] = "#805158",
        ["IconSurfaceBrush"] = "#202B34",
        ["PinnedBrush"] = "#E4CE92"
    };

    private static readonly IReadOnlyDictionary<string, string> LightPalette = new Dictionary<string, string>
    {
        ["AppBackgroundBrush"] = "#F6F8FB",
        ["SurfaceBrush"] = "#FFFFFF",
        ["SurfaceRaisedBrush"] = "#F1F5F8",
        ["SurfaceMutedBrush"] = "#F8FAFC",
        ["BorderBrush"] = "#D7E0E8",
        ["BorderStrongBrush"] = "#B8C7D2",
        ["TextBrush"] = "#17232D",
        ["MutedBrush"] = "#586A78",
        ["SubtleTextBrush"] = "#586A78",
        ["AccentBrush"] = "#087770",
        ["AccentSoftBrush"] = "#DDF5F0",
        ["AccentTextBrush"] = "#FFFFFF",
        ["AccentBorderBrush"] = "#68BDB2",
        ["HoverBrush"] = "#EAF1F5",
        ["PressedBrush"] = "#DCE8EE",
        ["SelectionBrush"] = "#E1F4F0",
        ["DangerBrush"] = "#B23842",
        ["DangerBorderBrush"] = "#D99CA1",
        ["IconSurfaceBrush"] = "#E8F0F4",
        ["PinnedBrush"] = "#80631E"
    };

    internal static bool UsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath, writable: false);
        return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
    }

    internal static void ApplyApplicationPalette(bool? useLightTheme = null)
    {
        if (SystemParameters.HighContrast)
        {
            ApplyHighContrastPalette();
            return;
        }
        var palette = (useLightTheme ?? QaLightThemeOverride ?? UsesLightTheme()) ? LightPalette : DarkPalette;
        foreach (var (key, value) in palette)
        {
            var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value));
            brush.Freeze();
            System.Windows.Application.Current.Resources[key] = brush;
        }
    }

    internal static void ApplyWindowTheme(Window window)
    {
        ApplyApplicationPalette();
        var source = PresentationSource.FromVisual(window) as HwndSource;
        if (source is not null)
            NativeMethods.SetImmersiveDarkTitleBar(source.Handle, !SystemParameters.HighContrast && !(QaLightThemeOverride ?? UsesLightTheme()));
    }

    internal static void Bind(FrameworkElement element, DependencyProperty property, string resourceKey) =>
        element.SetResourceReference(property, resourceKey);

    private static void ApplyHighContrastPalette()
    {
        var resources = System.Windows.Application.Current.Resources;
        resources["AppBackgroundBrush"] = System.Windows.SystemColors.WindowBrush;
        resources["SurfaceBrush"] = System.Windows.SystemColors.WindowBrush;
        resources["SurfaceRaisedBrush"] = System.Windows.SystemColors.WindowBrush;
        resources["SurfaceMutedBrush"] = System.Windows.SystemColors.WindowBrush;
        resources["BorderBrush"] = System.Windows.SystemColors.WindowTextBrush;
        resources["BorderStrongBrush"] = System.Windows.SystemColors.WindowTextBrush;
        resources["TextBrush"] = System.Windows.SystemColors.WindowTextBrush;
        resources["MutedBrush"] = System.Windows.SystemColors.WindowTextBrush;
        resources["SubtleTextBrush"] = System.Windows.SystemColors.WindowTextBrush;
        resources["AccentBrush"] = System.Windows.SystemColors.HighlightBrush;
        resources["AccentSoftBrush"] = System.Windows.SystemColors.WindowBrush;
        resources["AccentTextBrush"] = System.Windows.SystemColors.HighlightTextBrush;
        resources["AccentBorderBrush"] = System.Windows.SystemColors.HighlightBrush;
        resources["HoverBrush"] = System.Windows.SystemColors.WindowBrush;
        resources["PressedBrush"] = System.Windows.SystemColors.WindowBrush;
        resources["SelectionBrush"] = System.Windows.SystemColors.WindowBrush;
        resources["DangerBrush"] = System.Windows.SystemColors.WindowTextBrush;
        resources["DangerBorderBrush"] = System.Windows.SystemColors.WindowTextBrush;
        resources["IconSurfaceBrush"] = System.Windows.SystemColors.WindowBrush;
        resources["PinnedBrush"] = System.Windows.SystemColors.WindowTextBrush;
    }
}
