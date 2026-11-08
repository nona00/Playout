using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Playout;

internal sealed record PlaylistFile(string Name);

internal sealed record PlaylistTrack(string Path)
{
    public string Title => System.IO.Path.GetFileNameWithoutExtension(Path);
}

internal sealed class PlaylistLibrary(string settingsPath)
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".m3u", ".m3u8", ".pls"
    };

    public string LoadFolder()
    {
        if (!File.Exists(settingsPath))
            return string.Empty;

        try
        {
            return JsonSerializer.Deserialize<string>(File.ReadAllText(settingsPath)) ?? string.Empty;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Der Spiellistenordner konnte nicht gelesen werden.", exception);
        }
    }

    public void SaveFolder(string? folder)
    {
        string normalized = string.IsNullOrWhiteSpace(folder) ? string.Empty : Path.GetFullPath(folder.Trim());
        string previousFolder = LoadFolder();
        SaveValue(settingsPath, normalized);
        if (!string.Equals(previousFolder, normalized, StringComparison.OrdinalIgnoreCase))
            SaveCurrentPlaylist(null);
    }

    public string LoadCurrentPlaylist()
    {
        string path = GetCurrentPlaylistSettingsPath();
        if (!File.Exists(path))
            return string.Empty;

        string? name;
        try
        {
            name = JsonSerializer.Deserialize<string>(File.ReadAllText(path));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Die aktuelle Spielliste konnte nicht gelesen werden.", exception);
        }

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(LoadFolder()))
            return string.Empty;
        if (!string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal) ||
            !SupportedExtensions.Contains(Path.GetExtension(name)))
            return string.Empty;

        return File.Exists(Path.Combine(LoadFolder(), name)) ? name : string.Empty;
    }

    public void SaveCurrentPlaylist(string? name)
    {
        string normalized = string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();
        if (normalized.Length > 0)
        {
            if (!string.Equals(Path.GetFileName(normalized), normalized, StringComparison.Ordinal) ||
                !SupportedExtensions.Contains(Path.GetExtension(normalized)))
                throw new ArgumentException("Ungültiger Spiellistenname.", nameof(name));

            string folder = LoadFolder();
            if (string.IsNullOrEmpty(folder) || !File.Exists(Path.Combine(folder, normalized)))
                throw new FileNotFoundException("Die ausgewählte Spielliste wurde nicht gefunden.", normalized);
        }

        SaveValue(GetCurrentPlaylistSettingsPath(), normalized);
    }

    public IReadOnlyList<PlaylistFile> GetPlaylists()
    {
        string folder = LoadFolder();
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return [];

        return Directory.EnumerateFiles(folder)
            .Where(file => SupportedExtensions.Contains(Path.GetExtension(file)))
            .Select(file => new PlaylistFile(Path.GetFileName(file)))
            .OrderBy(file => file.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<PlaylistTrack> LoadPlaylist(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal))
            throw new ArgumentException("Ungültiger Spiellistenname.", nameof(name));
        if (!SupportedExtensions.Contains(Path.GetExtension(name)))
            throw new InvalidDataException("Nur M3U-, M3U8- und PLS-Spiellisten werden unterstützt.");

        string folder = LoadFolder();
        if (string.IsNullOrEmpty(folder))
            throw new InvalidOperationException("Es ist kein Spiellistenordner eingerichtet.");

        string playlistPath = Path.GetFullPath(Path.Combine(folder, name));
        if (!File.Exists(playlistPath))
            throw new FileNotFoundException("Die Spielliste wurde nicht gefunden.", playlistPath);

        string[] lines = File.ReadAllLines(playlistPath);
        IEnumerable<string> entries = Path.GetExtension(playlistPath).Equals(".pls", StringComparison.OrdinalIgnoreCase)
            ? ReadPlsEntries(lines)
            : ReadM3uEntries(lines);
        string playlistDirectory = Path.GetDirectoryName(playlistPath)!;
        return entries
            .Select(entry => ResolveEntry(entry, playlistDirectory))
            .Where(path => path is not null)
            .Select(path => new PlaylistTrack(path!))
            .ToArray();
    }

    private static IEnumerable<string> ReadM3uEntries(IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            string entry = line.Trim();
            if (entry.Length > 0 && !entry.StartsWith('#'))
                yield return entry;
        }
    }

    private static IEnumerable<string> ReadPlsEntries(IEnumerable<string> lines)
    {
        SortedDictionary<int, string> entries = [];
        foreach (string line in lines)
        {
            int separator = line.IndexOf('=');
            if (separator <= 0)
                continue;

            string key = line[..separator].Trim();
            if (key.StartsWith("File", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(key.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index > 0)
                entries.TryAdd(index, line[(separator + 1)..].Trim());
        }

        return entries.Values;
    }

    private static string? ResolveEntry(string entry, string playlistDirectory)
    {
        if (string.IsNullOrWhiteSpace(entry))
            return null;

        entry = entry.Trim().Trim('"');
        if (Path.IsPathRooted(entry))
            return Path.GetFullPath(entry);
        if (Uri.TryCreate(entry, UriKind.Absolute, out Uri? uri))
            return uri.IsFile ? Path.GetFullPath(uri.LocalPath) : null;
        return Path.GetFullPath(Path.Combine(playlistDirectory, entry));
    }

    private string GetCurrentPlaylistSettingsPath() => Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(settingsPath))!, "music-current-playlist.json");

    private static void SaveValue(string path, string value)
    {
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
            throw new InvalidOperationException("Das Konfigurationsverzeichnis fehlt.");

        Directory.CreateDirectory(directory);
        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
