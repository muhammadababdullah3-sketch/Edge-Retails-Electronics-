namespace EdgeRetails.Desktop.Services;

public interface IThemeService
{
    event EventHandler<AppTheme>? ThemeChanged;

    AppTheme CurrentTheme { get; }

    AppTheme ResolvedTheme { get; }

    void ApplyTheme(AppTheme theme);
}
