using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Flow.Launcher.Plugin.BrowserBookmark.Helper;
using Flow.Launcher.Plugin.BrowserBookmark.Models;
using Microsoft.Data.Sqlite;

namespace Flow.Launcher.Plugin.BrowserBookmark;

public abstract class FirefoxBookmarkLoaderBase : IBookmarkLoader
{
    private static readonly string ClassName = nameof(FirefoxBookmarkLoaderBase);

    private readonly string _faviconCacheDir;

    protected FirefoxBookmarkLoaderBase()
    {
        _faviconCacheDir = Main._faviconCacheDir;
    }

    public abstract List<Bookmark> GetBookmarks();

    // Updated query - removed favicon_id column
    private const string QueryAllBookmarks = """
        SELECT moz_places.url, moz_bookmarks.title
        FROM moz_places
            INNER JOIN moz_bookmarks ON (
                moz_bookmarks.fk NOT NULL AND moz_bookmarks.title NOT NULL AND moz_bookmarks.fk = moz_places.id
            )
        ORDER BY moz_places.visit_count DESC
        """;

    protected List<Bookmark> GetBookmarksFromPath(string placesPath)
    {
        // Variable to store bookmark list
        var bookmarks = new List<Bookmark>();

        // Return empty list if places.sqlite file doesn't exist
        if (string.IsNullOrEmpty(placesPath) || !File.Exists(placesPath))
            return bookmarks;

        // Try to register file monitoring
        try
        {
            Main.RegisterBookmarkFile(placesPath);
        }
        catch (Exception ex)
        {
            Main.Context.API.LogException(ClassName, $"Failed to register Firefox bookmark file monitoring: {placesPath}", ex);
            return bookmarks;
        }

        var tempDbPath = Path.Combine(_faviconCacheDir, $"tempplaces_{Guid.NewGuid()}.sqlite");

        try
        {
            // Use a copy to avoid lock issues with the original file
            File.Copy(placesPath, tempDbPath, true);

            // Create the connection string and init the connection
            using var dbConnection = new SqliteConnection($"Data Source={tempDbPath};Mode=ReadOnly");

            // Open connection to the database file and execute the query
            dbConnection.Open();
            var reader = new SqliteCommand(QueryAllBookmarks, dbConnection).ExecuteReader();

            // Get results in List<Bookmark> format
            bookmarks = reader
                .Select(
                    x => new Bookmark(
                        x["title"] is DBNull ? string.Empty : x["title"].ToString(),
                        x["url"].ToString(),
                        "Firefox"
                    )
                )
                .ToList();

            // Load favicons after loading bookmarks
            if (Main._settings.EnableFavicons)
            {
                var faviconDbPath = Path.Combine(Path.GetDirectoryName(placesPath), "favicons.sqlite");
                if (File.Exists(faviconDbPath))
                {
                    Main.Context.API.StopwatchLogInfo(ClassName, $"Load {bookmarks.Count} favicons cost", () =>
                    {
                        LoadFaviconsFromDb(faviconDbPath, bookmarks);
                    });
                }
            }

            // Close the connection so that we can delete the temporary file
            // https://github.com/dotnet/efcore/issues/26580
            SqliteConnection.ClearPool(dbConnection);
            dbConnection.Close();
        }
        catch (Exception ex)
        {
            Main.Context.API.LogException(ClassName, $"Failed to load Firefox bookmarks: {placesPath}", ex);
        }

        // Delete temporary file
        try
        {
            if (File.Exists(tempDbPath))
            {
                File.Delete(tempDbPath);
            }
        }
        catch (Exception ex)
        {
            Main.Context.API.LogException(ClassName, $"Failed to delete temporary favicon DB: {tempDbPath}", ex);
        }

        return bookmarks;
    }

    // Per favicons database: its last write time and the icon file found for each site, so a reload triggered by
    // history changes can reuse icons without copying and scanning an unchanged database again
    private static readonly ConcurrentDictionary<string, (DateTime WriteTime, Dictionary<string, string> Icons)> FaviconLookups =
        new(StringComparer.OrdinalIgnoreCase);

    private void LoadFaviconsFromDb(string dbPath, List<Bookmark> bookmarks)
    {
        // Bookmarks on the same site share an icon, so each site is looked up once
        var bookmarksBySite = bookmarks
            .Select(bookmark => (Bookmark: bookmark, Site: Uri.TryCreate(bookmark.Url, UriKind.Absolute, out var uri) ? uri.Host : null))
            .Where(entry => !string.IsNullOrEmpty(entry.Site))
            .GroupBy(entry => entry.Site, entry => entry.Bookmark, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (bookmarksBySite.Count == 0)
            return;

        var writeTime = File.GetLastWriteTimeUtc(dbPath);
        if (FaviconLookups.TryGetValue(dbPath, out var cached) && cached.WriteTime == writeTime &&
            bookmarksBySite.All(site => cached.Icons.ContainsKey(site.Key)))
        {
            AssignFavicons(bookmarksBySite, cached.Icons);
            return;
        }

        FaviconHelper.LoadFaviconsFromDb(_faviconCacheDir, dbPath, tempDbPath =>
        {
            var icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Use read-only connection to avoid locking issues
            // Do not use pooling so that we do not need to clear pool: https://github.com/dotnet/efcore/issues/26580
            using var connection = new SqliteConnection($"Data Source={tempDbPath};Mode=ReadOnly;Pooling=false");
            connection.Open();

            var iconIds = FindWidestIconPerSite(connection, bookmarksBySite.Select(site => site.Key));

            // Read and convert only icons that are not cached on disk yet
            var pending = new List<(string Site, string FaviconPath, byte[] Data)>();
            using (var dataCommand = connection.CreateCommand())
            {
                dataCommand.CommandText = "SELECT data FROM moz_icons WHERE id = @id";
                var idParameter = dataCommand.Parameters.Add("@id", SqliteType.Integer);

                foreach (var site in bookmarksBySite)
                {
                    icons[site.Key] = null;
                    if (!iconIds.TryGetValue(site.Key, out var iconId))
                        continue;

                    var faviconPath = Path.Combine(_faviconCacheDir, $"firefox_{site.Key}_{iconId}.webp");
                    if (File.Exists(faviconPath))
                    {
                        icons[site.Key] = faviconPath;
                        continue;
                    }

                    idParameter.Value = iconId;
                    if (dataCommand.ExecuteScalar() is byte[] { Length: > 0 } data)
                        pending.Add((site.Key, faviconPath, data));
                }
            }

            Parallel.ForEach(pending, icon =>
            {
                try
                {
                    var imageData = icon.Data;

                    // Check if the image data is compressed (GZip)
                    if (imageData.Length > 2 && imageData[0] == 0x1f && imageData[1] == 0x8b)
                    {
                        using var inputStream = new MemoryStream(imageData);
                        using var gZipStream = new GZipStream(inputStream, CompressionMode.Decompress);
                        using var outputStream = new MemoryStream();
                        gZipStream.CopyTo(outputStream);
                        imageData = outputStream.ToArray();
                    }

                    // Convert the image data to WebP format
                    var webpData = FaviconHelper.TryConvertToWebp(imageData);
                    if (webpData != null)
                        FaviconHelper.SaveBitmapData(webpData, icon.FaviconPath);
                }
                catch (Exception ex)
                {
                    Main.Context.API.LogException(ClassName, $"Failed to extract Firefox favicon: {icon.FaviconPath}", ex);
                }
            });

            foreach (var (site, faviconPath, _) in pending)
            {
                if (File.Exists(faviconPath))
                    icons[site] = faviconPath;
            }

            AssignFavicons(bookmarksBySite, icons);
            FaviconLookups[dbPath] = (writeTime, icons);
        });
    }

    /// <summary>
    /// Maps each site to the widest icon (vector icons count as widest) of a page whose URL contains the site, the same
    /// match as a <c>LIKE '%site%'</c> query per bookmark, but reading the icon pages once for all sites.
    /// </summary>
    public static Dictionary<string, long> FindWidestIconPerSite(SqliteConnection connection, IEnumerable<string> sites)
    {
        var pages = new List<(string Url, long IconId, long Width)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT p.page_url, i.id, i.width
                FROM moz_icons i
                JOIN moz_icons_to_pages ip ON i.id = ip.icon_id
                JOIN moz_pages_w_icons p ON ip.page_id = p.id
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0))
                    continue;
                pages.Add((reader.GetString(0), reader.GetInt64(1), reader.IsDBNull(2) ? 0 : reader.GetInt64(2)));
            }
        }

        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var site in sites)
        {
            (long IconId, long Width)? widest = null;
            foreach (var page in pages)
            {
                if (page.Url.Contains(site, StringComparison.OrdinalIgnoreCase) && (widest == null || page.Width > widest.Value.Width))
                    widest = (page.IconId, page.Width);
            }
            if (widest != null)
                result[site] = widest.Value.IconId;
        }

        return result;
    }

    private static void AssignFavicons(IEnumerable<IGrouping<string, Bookmark>> bookmarksBySite, IReadOnlyDictionary<string, string> icons)
    {
        foreach (var site in bookmarksBySite)
        {
            if (!icons.TryGetValue(site.Key, out var faviconPath) || faviconPath == null)
                continue;
            foreach (var bookmark in site)
                bookmark.FaviconPath = faviconPath;
        }
    }
}

public class FirefoxBookmarkLoader : FirefoxBookmarkLoaderBase
{
    /// <summary>
    /// Searches the places.sqlite db and returns all bookmarks
    /// </summary>
    public override List<Bookmark> GetBookmarks()
    {
        var bookmarks = new List<Bookmark>();
        bookmarks.AddRange(GetBookmarksFromPath(PlacesPath));
        bookmarks.AddRange(GetBookmarksFromPath(MsixPlacesPath));
        return bookmarks;
    }

    /// <summary>
    /// Path to places.sqlite of Msi installer
    /// E.g. C:\Users\{UserName}\AppData\Roaming\Mozilla\Firefox
    /// <see href="https://support.mozilla.org/en-US/kb/profiles-where-firefox-stores-user-data#w_finding-your-profile-without-opening-firefox"/>
    /// </summary>
    private static string PlacesPath
    {
        get
        {
            var profileFolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Mozilla\Firefox");
            return GetProfileIniPath(profileFolderPath);
        }
    }

    /// <summary>
    /// Path to places.sqlite of MSIX installer
    /// E.g. C:\Users\{UserName}\AppData\Local\Packages\Mozilla.Firefox_n80bbvh6b1yt2\LocalCache\Roaming\Mozilla\Firefox
    /// <see href="https://support.mozilla.org/en-US/kb/profiles-where-firefox-stores-user-data#w_finding-your-profile-without-opening-firefox"/>
    /// </summary>
    public static string MsixPlacesPath
    {
        get
        {
            var platformPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var packagesPath = Path.Combine(platformPath, "Packages");
            try
            {
                // Search for folder with Mozilla.Firefox prefix
                var firefoxPackageFolder = Directory.EnumerateDirectories(packagesPath, "Mozilla.Firefox*",
                    SearchOption.TopDirectoryOnly).FirstOrDefault();

                // Msix FireFox not installed
                if (firefoxPackageFolder == null) return string.Empty;

                var profileFolderPath = Path.Combine(firefoxPackageFolder, @"LocalCache\Roaming\Mozilla\Firefox");
                return GetProfileIniPath(profileFolderPath);
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    private static string GetProfileIniPath(string profileFolderPath)
    {
        var profileIni = Path.Combine(profileFolderPath, @"profiles.ini");
        if (!File.Exists(profileIni))
            return string.Empty;

        // get firefox default profile directory from profiles.ini
        using var sReader = new StreamReader(profileIni);
        var ini = sReader.ReadToEnd();

        var lines = ini.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).ToList();

        var defaultProfileFolderNameRaw = lines.FirstOrDefault(x => x.Contains("Default=") && x != "Default=1") ?? string.Empty;

        if (string.IsNullOrEmpty(defaultProfileFolderNameRaw))
            return string.Empty;

        var defaultProfileFolderName = defaultProfileFolderNameRaw.Split('=').Last();

        var indexOfDefaultProfileAttributePath = lines.IndexOf("Path=" + defaultProfileFolderName);

        /*
            Current profiles.ini structure example as of Firefox version 69.0.1

            [Install736426B0AF4A39CB]
            Default=Profiles/7789f565.default-release   <== this is the default profile this plugin will get the bookmarks from. When opened Firefox will load the default profile
            Locked=1

            [Profile2]
            Name=dummyprofile
            IsRelative=0
            Path=C:\t6h2yuq8.dummyprofile  <== Note this is a custom location path for the profile user can set, we need to cater for this in code.

            [Profile1]
            Name=default
            IsRelative=1
            Path=Profiles/cydum7q4.default
            Default=1

            [Profile0]
            Name=default-release
            IsRelative=1
            Path=Profiles/7789f565.default-release

            [General]
            StartWithLastProfile=1
            Version=2
        */
        // Seen in the example above, the IsRelative attribute is always above the Path attribute

        var relativePath = Path.Combine(defaultProfileFolderName, "places.sqlite");
        var absolutePath = Path.Combine(profileFolderPath, relativePath);

        // If the index is out of range, it means that the default profile is in a custom location or the file is malformed
        // If the profile is in a custom location, we need to check 
        if (indexOfDefaultProfileAttributePath - 1 < 0 ||
            indexOfDefaultProfileAttributePath - 1 >= lines.Count)
        {
            return Directory.Exists(absolutePath) ? absolutePath : relativePath;
        }

        var relativeAttribute = lines[indexOfDefaultProfileAttributePath - 1];

        // See above, the profile is located in a custom location, path is not relative, so IsRelative=0
        return (relativeAttribute == "0" || relativeAttribute == "IsRelative=0")
            ? relativePath : absolutePath;
    }
}

public static class Extensions
{
    public static IEnumerable<T> Select<T>(this SqliteDataReader reader, Func<SqliteDataReader, T> projection)
    {
        while (reader.Read())
        {
            yield return projection(reader);
        }
    }
}
