using MudBlazor;

namespace FamilyLibrarian.Web.Client.Theme;

/// <summary>
/// "Sunset Warm" — burnt-orange primary and coral accent over warm
/// ivory/cream in light mode, over neutral near-black surfaces in dark mode.
/// The app bar, nav rail, and footer use a fixed near-black "chrome" color
/// identical in both light and dark mode (matching the M3Undle rail this
/// layout is modeled on, but kept a neutral near-black rather than teal so
/// the rail stays visually distinct from the rest of the dashboard suite)
/// so that shell never changes when the content theme is toggled — only the
/// page background and text follow light/dark.
/// </summary>
public static class AppTheme
{
    private const string ChromeBackground = "#101418";
    private const string ChromeText = "#F7F3EF";

    public static MudTheme Default { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#D94B27",
            PrimaryContrastText = "#FFFFFF",
            Secondary = "#F47A55",
            SecondaryContrastText = "#FFFFFF",
            Tertiary = "#F6A66C",
            Background = "#FFF8F2",
            BackgroundGray = "#FBE9D8",
            Surface = "#FFFFFF",
            AppbarBackground = ChromeBackground,
            AppbarText = ChromeText,
            DrawerBackground = ChromeBackground,
            DrawerText = ChromeText,
            DrawerIcon = ChromeText,
            TextPrimary = "#1F2933",
            TextSecondary = "#66717C",
            ActionDefault = "#66717C",
            LinesDefault = "#EBD8C8",
            TableLines = "#EBD8C8",
            Divider = "#F0E2D6",
            Success = "#547A61",
            Warning = "#E59A3A",
            Error = "#C84336",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#F26943",
            PrimaryContrastText = "#101418",
            Secondary = "#F5A167",
            SecondaryContrastText = "#101418",
            Tertiary = "#5B3528",
            Background = "#101418",
            BackgroundGray = "#20262C",
            Surface = "#181D22",
            AppbarBackground = ChromeBackground,
            AppbarText = ChromeText,
            DrawerBackground = ChromeBackground,
            DrawerText = ChromeText,
            DrawerIcon = ChromeText,
            TextPrimary = "#F7F3EF",
            TextSecondary = "#C5C0BB",
            ActionDefault = "#C5C0BB",
            LinesDefault = "#343A40",
            TableLines = "#343A40",
            Divider = "#292F35",
            Success = "#547A61",
            Warning = "#E59A3A",
            Error = "#C84336",
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "8px",
        },
    };
}
