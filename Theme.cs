using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace BetterHwMonitor;

/// <summary>Switches between Themes/Light.xaml and Themes/Dark.xaml and remembers the choice.</summary>
public static class Theme
{
    private static readonly string SettingsFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BetterHwMonitor", "theme.txt");

    public static bool IsDark { get; private set; }

    public static event Action? Changed;

    /// <summary>Saved choice, or the Windows app theme when nothing is saved yet.</summary>
    public static bool LoadPreference()
    {
        try
        {
            if (File.Exists(SettingsFile))
                return File.ReadAllText(SettingsFile).Trim() == "dark";
        }
        catch { }
        return WindowsUsesDarkApps();
    }

    public static void Apply(bool dark, bool save = false)
    {
        IsDark = dark;
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var theme = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Absolute),
        };
        if (dictionaries.Count > 0) dictionaries[0] = theme; else dictionaries.Add(theme);

        foreach (Window w in Application.Current.Windows)
            SetTitleBar(w, dark);

        if (save) Save(dark);
        Changed?.Invoke();
    }

    private static void Save(bool dark)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, dark ? "dark" : "light");
        }
        catch { }
    }

    private static bool WindowsUsesDarkApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    // ---------- Dark window title bar (Windows 10 20H1+ / Windows 11) ----------

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeOld = 19; // Windows 10 before 20H1

    public static void SetTitleBar(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int value = dark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkModeOld, ref value, sizeof(int));
    }
}
