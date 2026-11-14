using Microsoft.Win32;
using System.Diagnostics;
using System.Globalization;
using System.Windows;

namespace RemoteMic.Windows;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\RemoteMic.Windows.TheShadow178";
    private const string LanguageSetting = "Language";
    private const string ThemeSetting = "Theme";
    private Mutex? _singleInstanceMutex;

    public static string CurrentLanguage { get; private set; } = "zh-Hans";
    public static string CurrentTheme { get; private set; } = "Light";

    protected override void OnStartup(StartupEventArgs e)
    {
        WaitForPreviousInstance(e.Args);
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            SingleInstanceMutexName,
            out var isFirstInstance);
        if (!isFirstInstance)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        var savedLanguage = RemoteMic.Windows.Properties.SettingsStore.Read(LanguageSetting);
        var language = savedLanguage ??
            (CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                ? "zh-Hans"
                : "en");
        ApplyLanguage(language, persist: false);

        var savedTheme = RemoteMic.Windows.Properties.SettingsStore.Read(ThemeSetting);
        ApplyTheme(savedTheme ?? DetectSystemTheme(), persist: false);
        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        var background = e.Args.Any(argument =>
            argument.Equals("--background", StringComparison.OrdinalIgnoreCase));
        if (background)
        {
            // Creating a real HWND is required for Raw Input and the tray icon.
            // Start minimized and hide immediately so sign-in startup remains
            // out of the taskbar.
            window.ShowInTaskbar = false;
            window.WindowState = WindowState.Minimized;
            window.Show();
            window.Hide();
            window.WindowState = WindowState.Normal;
            window.ShowInTaskbar = true;
        }
        else
        {
            window.Show();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        base.OnExit(e);
    }

    public static string Text(string key)
    {
        return Current.TryFindResource(key) as string ?? key;
    }

    public static void ApplyLanguage(string language, bool persist = true)
    {
        language = language.Equals("en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh-Hans";
        ReplaceDictionary("Resources/Strings.", $"Resources/Strings.{language}.xaml");
        CurrentLanguage = language;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        if (persist)
        {
            RemoteMic.Windows.Properties.SettingsStore.Write(LanguageSetting, language);
        }
    }

    public static void ApplyTheme(string theme, bool persist = true)
    {
        theme = theme.Equals("Dark", StringComparison.OrdinalIgnoreCase) ? "Dark" : "Light";
        ReplaceDictionary("Themes/", $"Themes/{theme}.xaml");
        CurrentTheme = theme;
        if (persist)
        {
            RemoteMic.Windows.Properties.SettingsStore.Write(ThemeSetting, theme);
        }
    }

    private static void ReplaceDictionary(string sourcePrefix, string newSource)
    {
        var dictionaries = Current.Resources.MergedDictionaries;
        var old = dictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase) == true);
        var replacement = new ResourceDictionary { Source = new Uri(newSource, UriKind.Relative) };
        if (old is null)
        {
            dictionaries.Add(replacement);
            return;
        }

        dictionaries[dictionaries.IndexOf(old)] = replacement;
    }

    private static string DetectSystemTheme()
    {
        try
        {
            using var personalization = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return personalization?.GetValue("AppsUseLightTheme") is int value && value == 0
                ? "Dark"
                : "Light";
        }
        catch
        {
            return "Light";
        }
    }

    private static void WaitForPreviousInstance(IReadOnlyList<string> arguments)
    {
        var marker = arguments
            .Select((value, index) => (value, index))
            .FirstOrDefault(item => item.value.Equals(
                "--wait-for-pid",
                StringComparison.OrdinalIgnoreCase));
        if (marker.value is null || marker.index + 1 >= arguments.Count ||
            !int.TryParse(arguments[marker.index + 1], out var processId) ||
            processId == Environment.ProcessId)
        {
            return;
        }

        try
        {
            using var previous = Process.GetProcessById(processId);
            previous.WaitForExit(10_000);
        }
        catch (ArgumentException)
        {
            // The previous process already exited.
        }
    }
}
