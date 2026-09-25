using System.Text.RegularExpressions;

namespace EdgeRetails.UnitTests;

public sealed class Sprint2ForensicAuditTests
{
    private static string GetSolutionRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "EdgeRetails.sln")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException("EdgeRetails.sln solution root directory not found.");
    }

    private static string GetDesktopDirectory()
    {
        return Path.Combine(GetSolutionRoot(), "src", "EdgeRetails.Desktop");
    }

    private static List<string> GetDesktopXamlFiles()
    {
        var desktopDir = GetDesktopDirectory();
        return Directory.GetFiles(desktopDir, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) &&
                        !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .ToList();
    }

    [Fact]
    public void CanvasUsage_IsStrictlyZero_AcrossDesktopProject()
    {
        var xamlFiles = GetDesktopXamlFiles();
        var canvasViolations = new List<string>();

        foreach (var file in xamlFiles)
        {
            var content = File.ReadAllText(file);
            if (Regex.IsMatch(content, @"<\s*Canvas(\s|>)", RegexOptions.IgnoreCase))
            {
                canvasViolations.Add(Path.GetFileName(file));
            }
        }

        Assert.True(canvasViolations.Count == 0,
            $"Canvas usage detected in files: {string.Join(", ", canvasViolations)}");
    }

    [Fact]
    public void ThemeDictionaries_HaveExactKeyParity_LightAndDark()
    {
        var desktopDir = GetDesktopDirectory();
        var lightPath = Path.Combine(desktopDir, "Resources", "Themes", "Light.xaml");
        var darkPath = Path.Combine(desktopDir, "Resources", "Themes", "Dark.xaml");

        Assert.True(File.Exists(lightPath), "Light.xaml theme file missing.");
        Assert.True(File.Exists(darkPath), "Dark.xaml theme file missing.");

        var lightKeys = ExtractResourceKeys(File.ReadAllText(lightPath));
        var darkKeys = ExtractResourceKeys(File.ReadAllText(darkPath));

        var missingInDark = lightKeys.Except(darkKeys).ToList();
        var missingInLight = darkKeys.Except(lightKeys).ToList();

        Assert.True(missingInDark.Count == 0,
            $"Keys present in Light.xaml but missing in Dark.xaml: {string.Join(", ", missingInDark)}");
        Assert.True(missingInLight.Count == 0,
            $"Keys present in Dark.xaml but missing in Light.xaml: {string.Join(", ", missingInLight)}");
    }

    [Fact]
    public void ThemeSensitiveBrushes_NeverUseStaticResource_InAnyXaml()
    {
        var desktopDir = GetDesktopDirectory();
        var lightPath = Path.Combine(desktopDir, "Resources", "Themes", "Light.xaml");
        var themeKeys = ExtractResourceKeys(File.ReadAllText(lightPath));

        var xamlFiles = GetDesktopXamlFiles()
            .Where(f => !f.Contains("Resources" + Path.DirectorySeparatorChar + "Themes"))
            .ToList();

        var violations = new List<string>();

        foreach (var file in xamlFiles)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                foreach (var match in Regex.Matches(line, @"\{StaticResource\s+([^\s\}]+)\}").Cast<Match>())
                {
                    var key = match.Groups[1].Value;
                    if (themeKeys.Contains(key))
                    {
                        violations.Add($"{Path.GetFileName(file)}:L{i + 1} uses {{StaticResource {key}}} instead of DynamicResource");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Theme-sensitive brush StaticResource violations found:\n{string.Join("\n", violations)}");
    }

    [Fact]
    public void AppXaml_DeclaresRequiredDataTemplates_ForAllCoreViewModels()
    {
        var desktopDir = GetDesktopDirectory();
        var appXamlPath = Path.Combine(desktopDir, "App.xaml");
        var content = File.ReadAllText(appXamlPath);

        var requiredViewModels = new[]
        {
            "LoginViewModel",
            "ShellViewModel",
            "DashboardViewModel",
            "PosViewModel",
            "PlaceholderPageViewModel"
        };

        foreach (var vm in requiredViewModels)
        {
            Assert.Contains($"DataType=\"{{x:Type viewModels:{vm}}}\"", content);
        }
    }

    [Fact]
    public void MainWindow_MeetsTargetResolutionConformance()
    {
        var desktopDir = GetDesktopDirectory();
        var mainWindowPath = Path.Combine(desktopDir, "MainWindow.xaml");
        var content = File.ReadAllText(mainWindowPath);

        Assert.Contains("Width=\"1440\"", content);
        Assert.Contains("Height=\"900\"", content);
        Assert.Contains("MinWidth=\"1024\"", content);
        Assert.Contains("MinHeight=\"640\"", content);
    }

    [Fact]
    public void NavigationIcons_DeclareAll11RequiredIcons()
    {
        var desktopDir = GetDesktopDirectory();
        var iconsPath = Path.Combine(desktopDir, "Resources", "NavigationIcons.xaml");
        var content = File.ReadAllText(iconsPath);

        var requiredIcons = new[]
        {
            "Icon.Nav.Dashboard",
            "Icon.Nav.POS",
            "Icon.Nav.SalesHistory",
            "Icon.Nav.ThakaProjects",
            "Icon.Nav.Purchases",
            "Icon.Nav.Inventory",
            "Icon.Nav.Expenses",
            "Icon.Nav.Customers",
            "Icon.Nav.Suppliers",
            "Icon.Nav.Reports",
            "Icon.Nav.Settings"
        };

        foreach (var icon in requiredIcons)
        {
            Assert.Contains($"x:Key=\"{icon}\"", content);
        }
    }

    [Fact]
    public void XamlFiles_ContainNoMojibakeCharacters()
    {
        var xamlFiles = GetDesktopXamlFiles();
        var violations = new List<string>();

        foreach (var file in xamlFiles)
        {
            var content = File.ReadAllText(file);
            if (content.Contains("Â·") || content.Contains("â‚¬") || content.Contains("Ã"))
            {
                violations.Add(Path.GetFileName(file));
            }
        }

        Assert.True(violations.Count == 0,
            $"Mojibake characters found in: {string.Join(", ", violations)}");
    }

    [Fact]
    public void AllResourceReferences_ResolveToDeclaredKeys()
    {
        var desktopDir = GetDesktopDirectory();
        var appXamlPath = Path.Combine(desktopDir, "App.xaml");
        var appContent = File.ReadAllText(appXamlPath);

        // Collect all keys defined at app level (App.xaml + all merged dictionaries)
        var appLevelKeys = ExtractResourceKeys(appContent);

        // Find all merged dictionaries in App.xaml
        var mergedDictMatches = Regex.Matches(appContent, @"Source=""([^""]+)""");
        foreach (Match match in mergedDictMatches)
        {
            var relativeSource = match.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.Combine(desktopDir, relativeSource);
            if (File.Exists(fullPath))
            {
                var dictContent = File.ReadAllText(fullPath);
                foreach (var key in ExtractResourceKeys(dictContent))
                {
                    appLevelKeys.Add(key);
                }
            }
        }

        // Also add Dark theme keys
        var darkThemePath = Path.Combine(desktopDir, "Resources", "Themes", "Dark.xaml");
        if (File.Exists(darkThemePath))
        {
            foreach (var key in ExtractResourceKeys(File.ReadAllText(darkThemePath)))
            {
                appLevelKeys.Add(key);
            }
        }

        var xamlFiles = GetDesktopXamlFiles();
        var missingReferences = new List<string>();

        foreach (var file in xamlFiles)
        {
            var content = File.ReadAllText(file);
            var localKeys = ExtractResourceKeys(content);

            var refMatches = Regex.Matches(content, @"\{(?:StaticResource|DynamicResource)\s+([^\s\}]+)\}");
            foreach (Match match in refMatches)
            {
                var key = match.Groups[1].Value;

                // Ignore built-in/type extensions
                if (key.StartsWith("x:") || key.StartsWith("System:") || key == "{x:Null}")
                {
                    continue;
                }

                if (!appLevelKeys.Contains(key) && !localKeys.Contains(key))
                {
                    missingReferences.Add($"{Path.GetFileName(file)}: missing key '{key}'");
                }
            }
        }

        Assert.True(missingReferences.Count == 0,
            $"Unresolved XAML resource references found:\n{string.Join("\n", missingReferences.Distinct())}");
    }

    [Fact]
    public void PosCatalogSelection_DoesNotMutateCartInsideSelectedItemSetter()
    {
        var desktopDir = GetDesktopDirectory();
        var viewModelPath = Path.Combine(desktopDir, "ViewModels", "PosViewModel.cs");
        var viewPath = Path.Combine(desktopDir, "Views", "PosView.xaml");

        var viewModelContent = File.ReadAllText(viewModelPath);
        var selectionProperty = Regex.Match(
            viewModelContent,
            @"public PosProductItemViewModel\? SelectedCatalogProduct\s*\{(?<body>.*?)\n    \}",
            RegexOptions.Singleline);

        Assert.True(selectionProperty.Success, "SelectedCatalogProduct property not found.");

        var body = selectionProperty.Groups["body"].Value;
        Assert.DoesNotContain("AddToCart(", body);
        Assert.DoesNotContain("_selectedCatalogProduct = null", body);
        Assert.Contains("SetProperty(ref _selectedCatalogProduct, value)", body);

        var viewContent = File.ReadAllText(viewPath);
        Assert.Contains("OnCatalogRowPreviewMouseLeftButtonDown", viewContent);
        Assert.Contains("IsReadOnly=\"True\"", viewContent);
    }

    [Fact]
    public void Tooltips_ConformToDesignTokens_AndDefinePadding()
    {
        var desktopDir = GetDesktopDirectory();
        var tooltipsPath = Path.Combine(desktopDir, "Resources", "Tooltips.xaml");
        var appXamlPath = Path.Combine(desktopDir, "App.xaml");

        Assert.True(File.Exists(tooltipsPath), "Tooltips.xaml missing.");
        var tooltipsContent = File.ReadAllText(tooltipsPath);
        var appContent = File.ReadAllText(appXamlPath);

        Assert.Contains("Resources/Tooltips.xaml", appContent);
        Assert.Contains("Property=\"Padding\"", tooltipsContent);
        Assert.Contains("Property=\"FontFamily\" Value=\"{StaticResource Font.Primary}\"", tooltipsContent);
        Assert.Contains("Property=\"FontSize\" Value=\"12\"", tooltipsContent);
        Assert.Contains("CornerRadius=\"{StaticResource Radius.Compact}\"", tooltipsContent);
    }

    [Fact]
    public void PosTerminal_StartsCleanWithEmptyCart()
    {
        var desktopDir = GetDesktopDirectory();
        var viewModelPath = Path.Combine(desktopDir, "ViewModels", "PosViewModel.cs");
        var content = File.ReadAllText(viewModelPath);

        var ctorMatch = Regex.Match(
            content,
            @"public PosViewModel\s*\([^)]*\)\s*\{(?<ctor>.*?)\n    \}",
            RegexOptions.Singleline);

        Assert.True(ctorMatch.Success, "PosViewModel primary constructor not found.");
        var ctorBody = ctorMatch.Groups["ctor"].Value;

        Assert.Contains("CartItems.Clear()", ctorBody);
        Assert.DoesNotContain("PreloadDemoCartItems()", ctorBody);
    }

    [Fact]
    public void ScrollBars_AndOverlayScrollViewer_AreDeclaredAndRegistered()
    {
        var desktopDir = GetDesktopDirectory();
        var scrollBarsPath = Path.Combine(desktopDir, "Resources", "ScrollBars.xaml");
        var appXamlPath = Path.Combine(desktopDir, "App.xaml");
        var posPath = Path.Combine(desktopDir, "Views", "PosView.xaml");

        Assert.True(File.Exists(scrollBarsPath), "ScrollBars.xaml missing.");
        var scrollBarsContent = File.ReadAllText(scrollBarsPath);
        var appContent = File.ReadAllText(appXamlPath);
        var posContent = File.ReadAllText(posPath);

        Assert.Contains("Resources/ScrollBars.xaml", appContent);
        Assert.Contains("x:Key=\"ScrollViewer.CleanOverlay\"", scrollBarsContent);
        Assert.Contains("x:Key=\"ScrollBar.Thumb\"", scrollBarsContent);
        Assert.Contains("Style=\"{StaticResource ScrollViewer.CleanOverlay}\"", posContent);
    }

    [Fact]
    public void CatalogTable_RowHeightAndColumnSpacing_AreProperlyProportioned()
    {
        var desktopDir = GetDesktopDirectory();
        var tablesPath = Path.Combine(desktopDir, "Resources", "Tables.xaml");
        var posPath = Path.Combine(desktopDir, "Views", "PosView.xaml");

        var tablesContent = File.ReadAllText(tablesPath);
        var posContent = File.ReadAllText(posPath);

        Assert.Contains("Padding=\"{TemplateBinding Padding}\"", tablesContent);
        Assert.Contains("Property=\"Height\" Value=\"52\"", posContent);
        Assert.Contains("Header=\"Product\" Width=\"1.8*\"", posContent);
        Assert.Contains("Header=\"Brand\"", posContent);
        Assert.Contains("Width=\"1.1*\"", posContent);
        Assert.Contains("Header=\"Stock\" Width=\"135\"", posContent);
        Assert.Contains("Header=\"Price\" Width=\"130\"", posContent);
    }

    private static HashSet<string> ExtractResourceKeys(string xamlContent)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var matches = Regex.Matches(xamlContent, @"x:Key\s*=\s*""([^""]+)""");
        foreach (Match match in matches)
        {
            keys.Add(match.Groups[1].Value);
        }

        return keys;
    }
}
