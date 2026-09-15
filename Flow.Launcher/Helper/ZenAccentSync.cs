using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.DependencyInjection;
using Flow.Launcher.Core.Resource;
using Flow.Launcher.Infrastructure;

namespace Flow.Launcher.Helper;

/// <summary>
/// Follows Zen Browser for themes that ask for it: <c>ZenAccentSync</c> uses the active workspace color as the accent
/// (result glow, plugin pill and flash, selection and progress line), and <c>ZenColorSchemeSync</c> uses Zen's own
/// light/dark choice when Zen forces one. Checked when the launcher opens; Zen's files are only read again after
/// Zen has written them, so there is no watcher or timer.
/// </summary>
public static class ZenAccentSync
{
    private static readonly string ClassName = nameof(ZenAccentSync);

    private const string AccentFlagKey = "ZenAccentSync";
    private const string ColorSchemeFlagKey = "ZenColorSchemeSync";
    private const double MaxGlowLightness = 0.62;

    private static readonly string[] AccentKeys =
    [
        "GlowStop1", "GlowStop2", "GlowStop3", "GlowStop4", "GlowStop5", "GlowStop6",
        "PluginActivationPillBrush", "PluginActivationFlashBrush", "AccentBrush"
    ];

    // Glow gradient: the accent mixed with black at these amounts, darkest first, with these alphas
    private static readonly (double Amount, byte Alpha)[] GlowStops =
        [(0.05, 0x33), (0.15, 0x66), (0.30, 0x99), (0.50, 0xCC), (0.75, 0xE6), (1.00, 0xFF)];

    private static readonly Regex ActiveWorkspaceRegex =
        new(@"user_pref\(""zen\.workspaces\.active"",\s*""([^""]+)""\)", RegexOptions.Compiled);

    // Firefox toolbar theme: 0 = dark, 1 = light, 2 (or unset) = follow the system
    private static readonly Regex ToolbarThemeRegex =
        new(@"user_pref\(""browser\.theme\.toolbar-theme"",\s*(\d+)\)", RegexOptions.Compiled);

    private static readonly SemaphoreSlim RefreshLock = new(1, 1);
    private static DateTime _prefsWriteTime;
    private static DateTime _sessionsWriteTime;
    private static bool _lastSyncAccent;
    private static bool _lastSyncColorScheme;
    private static Color? _appliedAccent;
    private static string _appliedColorScheme;

    /// <summary>
    /// Call on the UI thread when the launcher is shown.
    /// </summary>
    public static void Refresh()
    {
        var app = Application.Current;
        if (app == null)
            return;

        var syncAccent = app.TryFindResource(AccentFlagKey) is true;
        var syncColorScheme = app.TryFindResource(ColorSchemeFlagKey) is true;
        if (!syncAccent)
            ClearAccent();
        if (!syncColorScheme)
            ApplyColorScheme(null);
        if (!syncAccent && !syncColorScheme)
            return;

        _ = Task.Run(() => RefreshInBackgroundAsync(syncAccent, syncColorScheme));
    }

    private static async Task RefreshInBackgroundAsync(bool syncAccent, bool syncColorScheme)
    {
        if (!await RefreshLock.WaitAsync(0))
            return;

        try
        {
            var profile = FindZenProfileDirectory();
            if (profile == null)
                return;

            var prefsPath = Path.Combine(profile, "prefs.js");
            var sessionsPath = Path.Combine(profile, "zen-sessions.jsonlz4");
            if (!File.Exists(prefsPath))
                return;

            var prefsWriteTime = File.GetLastWriteTimeUtc(prefsPath);
            var sessionsWriteTime = File.Exists(sessionsPath) ? File.GetLastWriteTimeUtc(sessionsPath) : default;
            if (prefsWriteTime == _prefsWriteTime && sessionsWriteTime == _sessionsWriteTime &&
                syncAccent == _lastSyncAccent && syncColorScheme == _lastSyncColorScheme)
                return;

            var prefs = ReadSharedText(prefsPath);
            var accent = syncAccent && sessionsWriteTime != default ? ReadActiveWorkspaceColor(prefs, sessionsPath) : null;
            var colorScheme = syncColorScheme ? ReadColorScheme(prefs) : null;

            _prefsWriteTime = prefsWriteTime;
            _sessionsWriteTime = sessionsWriteTime;
            _lastSyncAccent = syncAccent;
            _lastSyncColorScheme = syncColorScheme;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (accent is { } color && color != _appliedAccent)
                    ApplyAccent(color);
                if (syncColorScheme)
                    ApplyColorScheme(colorScheme);
            });
        }
        catch (Exception e)
        {
            App.API.LogException(ClassName, "Failed to read Zen Browser settings", e);
        }
        finally
        {
            RefreshLock.Release();
        }
    }

    // Zen keeps its files open for writing; share access so reading never blocks it
    private static string ReadSharedText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string ReadColorScheme(string prefs)
    {
        var match = ToolbarThemeRegex.Match(prefs);
        return !match.Success ? null : match.Groups[1].Value switch
        {
            "0" => "Dark",
            "1" => "Light",
            _ => null
        };
    }

    private static void ApplyColorScheme(string colorScheme)
    {
        if (colorScheme == _appliedColorScheme)
            return;

        var resources = Application.Current.Resources;
        if (colorScheme == null)
            resources.Remove(Theme.ColorSchemeOverrideKey);
        else
            resources[Theme.ColorSchemeOverrideKey] = colorScheme;
        _appliedColorScheme = colorScheme;

        _ = Ioc.Default.GetRequiredService<Theme>().RefreshFrameAsync();
    }

    private static string FindZenProfileDirectory()
    {
        var zenDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "zen");
        var profilesIni = Path.Combine(zenDirectory, "profiles.ini");
        if (!File.Exists(profilesIni))
            return null;

        string installDefault = null, markedDefault = null, firstProfile = null;
        string section = null, path = null;
        bool isRelative = true, isDefault = false;

        void EndProfileSection()
        {
            if (section?.StartsWith("Profile", StringComparison.OrdinalIgnoreCase) != true || path == null)
                return;
            var full = isRelative ? Path.Combine(zenDirectory, path) : path;
            firstProfile ??= full;
            if (isDefault)
                markedDefault ??= full;
        }

        foreach (var rawLine in File.ReadLines(profilesIni))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                EndProfileSection();
                section = line[1..^1];
                path = null;
                isRelative = true;
                isDefault = false;
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
                continue;
            var key = line[..separator];
            var value = line[(separator + 1)..];

            if (section?.StartsWith("Install", StringComparison.OrdinalIgnoreCase) == true && key == "Default")
                installDefault ??= Path.Combine(zenDirectory, value);
            else if (key == "Path")
                path = value;
            else if (key == "IsRelative")
                isRelative = value == "1";
            else if (key == "Default")
                isDefault = value == "1";
        }
        EndProfileSection();

        var profile = installDefault ?? markedDefault ?? firstProfile;
        return profile != null && Directory.Exists(profile) ? profile : null;
    }

    private static Color? ReadActiveWorkspaceColor(string prefs, string sessionsPath)
    {
        var activeMatch = ActiveWorkspaceRegex.Match(prefs);
        if (!activeMatch.Success)
            return null;
        var activeWorkspace = activeMatch.Groups[1].Value;

        byte[] sessions;
        using (var stream = new FileStream(sessionsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            sessions = new byte[stream.Length];
            stream.ReadExactly(sessions);
        }

        using var document = JsonDocument.Parse(MozLz4.Decompress(sessions));
        if (!document.RootElement.TryGetProperty("spaces", out var spaces) || spaces.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var space in spaces.EnumerateArray())
        {
            if (!space.TryGetProperty("uuid", out var uuid) || uuid.GetString() != activeWorkspace)
                continue;
            if (!space.TryGetProperty("theme", out var theme) ||
                !theme.TryGetProperty("gradientColors", out var gradient) ||
                gradient.ValueKind != JsonValueKind.Array)
                return null;

            var colors = gradient.EnumerateArray().ToList();
            var primary = colors.FirstOrDefault(c => c.TryGetProperty("isPrimary", out var p) && p.ValueKind == JsonValueKind.True);
            if (primary.ValueKind == JsonValueKind.Undefined)
                primary = colors.FirstOrDefault();
            if (primary.ValueKind == JsonValueKind.Undefined ||
                !primary.TryGetProperty("c", out var rgb) || rgb.GetArrayLength() < 3)
                return null;

            return Color.FromRgb(ToByte(rgb[0]), ToByte(rgb[1]), ToByte(rgb[2]));
        }

        return null;
    }

    private static byte ToByte(JsonElement value) => (byte)Math.Clamp(Math.Round(value.GetDouble()), 0, 255);

    private static void ApplyAccent(Color accent)
    {
        var resources = Application.Current.Resources;
        var glowPeak = LimitLightness(accent, MaxGlowLightness);

        for (var i = 0; i < GlowStops.Length; i++)
        {
            var (amount, alpha) = GlowStops[i];
            var mixed = Contrast(Scale(glowPeak, amount));
            resources[$"GlowStop{i + 1}"] = Color.FromArgb(alpha, mixed.R, mixed.G, mixed.B);
        }

        // Zen's search mode pill: accent mixed half and half with 20% white
        var pillAlpha = 0.5 + 0.5 * 0.2;
        byte Pill(byte channel) => (byte)Math.Round((channel * 0.5 + 255 * 0.2 * 0.5) / pillAlpha);
        resources["PluginActivationPillBrush"] = Frozen(new SolidColorBrush(
            Color.FromArgb((byte)Math.Round(pillAlpha * 255), Pill(glowPeak.R), Pill(glowPeak.G), Pill(glowPeak.B))));

        var flash = new RadialGradientBrush
        {
            Center = new Point(0.95, 0.5),
            GradientOrigin = new Point(0.95, 0.5),
            RadiusX = 0.45,
            RadiusY = 1.6
        };
        flash.GradientStops.Add(new GradientStop(Color.FromArgb(0xA6, glowPeak.R, glowPeak.G, glowPeak.B), 0));
        flash.GradientStops.Add(new GradientStop(Color.FromArgb(0x40, glowPeak.R, glowPeak.G, glowPeak.B), 0.45));
        flash.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, glowPeak.R, glowPeak.G, glowPeak.B), 1));
        resources["PluginActivationFlashBrush"] = Frozen(flash);

        resources["AccentBrush"] = Frozen(new SolidColorBrush(glowPeak));

        _appliedAccent = accent;
    }

    private static void ClearAccent()
    {
        if (_appliedAccent == null || Application.Current == null)
            return;

        foreach (var key in AccentKeys)
            Application.Current.Resources.Remove(key);

        _appliedAccent = null;
        _prefsWriteTime = default;
        _sessionsWriteTime = default;
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static Color Scale(Color color, double amount) =>
        Color.FromRgb((byte)Math.Round(color.R * amount), (byte)Math.Round(color.G * amount), (byte)Math.Round(color.B * amount));

    // Nebula's glow uses contrast(120%)
    private static Color Contrast(Color color)
    {
        static byte Channel(byte value) => (byte)Math.Clamp(Math.Round(((value / 255.0 - 0.5) * 1.2 + 0.5) * 255), 0, 255);
        return Color.FromRgb(Channel(color.R), Channel(color.G), Channel(color.B));
    }

    // Very light workspace colors would wash the glow out; keep hue and saturation, cap lightness
    private static Color LimitLightness(Color color, double maxLightness)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        var lightness = (max + min) / 2;
        if (lightness <= maxLightness)
            return color;

        var delta = max - min;
        if (delta == 0)
        {
            var gray = (byte)Math.Round(maxLightness * 255);
            return Color.FromRgb(gray, gray, gray);
        }

        var saturation = lightness > 0.5 ? delta / (2 - max - min) : delta / (max + min);
        double hue;
        if (max == r) hue = (g - b) / delta + (g < b ? 6 : 0);
        else if (max == g) hue = (b - r) / delta + 2;
        else hue = (r - g) / delta + 4;
        hue /= 6;

        var q = maxLightness < 0.5 ? maxLightness * (1 + saturation) : maxLightness + saturation - maxLightness * saturation;
        var p = 2 * maxLightness - q;

        static double HueToChannel(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }

        return Color.FromRgb(
            (byte)Math.Round(HueToChannel(p, q, hue + 1.0 / 3) * 255),
            (byte)Math.Round(HueToChannel(p, q, hue) * 255),
            (byte)Math.Round(HueToChannel(p, q, hue - 1.0 / 3) * 255));
    }
}
