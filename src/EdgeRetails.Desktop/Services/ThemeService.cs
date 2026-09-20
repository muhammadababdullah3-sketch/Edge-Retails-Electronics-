using System.Windows;
using Microsoft.Win32;

namespace EdgeRetails.Desktop.Services;

public sealed class ThemeService : IThemeService
{
    private const string ThemeDictionaryMarker = "Resources/Themes/";

    public event EventHandler<AppTheme>? ThemeChanged;

    public AppTheme CurrentTheme { get; private set; } = AppTheme.Light;

    public AppTheme ResolvedTheme { get; private set; } = AppTheme.Light;

    public void ApplyTheme(AppTheme theme)
    {
        var resolved = theme == AppTheme.System
            ? ResolveSystemTheme()
            : theme;

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var currentIndex = FindThemeDictionaryIndex(dictionaries);

        if (currentIndex < 0)
        {
            throw new InvalidOperationException(
                "The application theme ResourceDictionary was not found.");
        }

        dictionaries[currentIndex] = new ResourceDictionary
        {
            Source = new Uri($"Resources/Themes/{resolved}.xaml", UriKind.Relative),
        };

        CurrentTheme = theme;
        ResolvedTheme = resolved;
        ThemeChanged?.Invoke(this, resolved);
    }

    private static int FindThemeDictionaryIndex(
        IList<ResourceDictionary> dictionaries)
    {
        for (var index = 0; index < dictionaries.Count; index++)
        {
            var source = dictionaries[index].Source?.OriginalString;
            if (source?.Contains(
                    ThemeDictionaryMarker,
                    StringComparison.OrdinalIgnoreCase) == true)
            {
                return index;
            }
        }

        return -1;
    }

    private static AppTheme ResolveSystemTheme()
    {
        const string key =
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        var value = Registry.GetValue(key, "AppsUseLightTheme", 1);

        return value is int lightThemeEnabled && lightThemeEnabled == 0
            ? AppTheme.Dark
            : AppTheme.Light;
    }
}
