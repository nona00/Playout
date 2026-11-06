using System.Text.Json;

namespace Playout.Web;

internal sealed record WebMusicTrack(string Path, string Source)
{
    public string Title => System.IO.Path.GetFileNameWithoutExtension(Path);
}

internal sealed record WebMusicScanResult(IReadOnlyList<WebMusicTrack> Tracks, IReadOnlyList<string> Errors);

internal sealed class WebMusicLibrary
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".mp2", ".mp1", ".wav", ".aif", ".aiff", ".ogg"
    };
    private readonly object sync = new();
    private readonly string path;
    private List<string> sources;

    public WebMusicLibrary(string path)
    {
        this.path = path;
        sources = Load();
    }

    public IReadOnlyList<string> GetSources()
    {
        lock (sync) return sources.ToArray();
    }

    public void AddSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new InvalidOperationException("Der Quellpfad fehlt.");
        string normalized = Normalize(source);
        lock (sync)
        {
            if (sources.Contains(normalized, StringComparer.OrdinalIgnoreCase)) return;
            Save([.. sources, normalized]);
            sources.Add(normalized);
        }
    }

    public void RemoveSource(string source)
    {
        lock (sync)
        {
            List<string> updated = sources.Where(item => !string.Equals(item, source, StringComparison.OrdinalIgnoreCase)).ToList();
            Save(updated);
            sources = updated;
        }
    }

    public WebMusicScanResult Scan(CancellationToken cancellationToken)
    {
        string[] currentSources;
        lock (sync) currentSources = sources.ToArray();
        Dictionary<string, WebMusicTrack> tracks = new(StringComparer.OrdinalIgnoreCase);
        List<string> errors = [];
        foreach (string source in currentSources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(source))
            {
                errors.Add($"Quelle nicht verfügbar: {source}");
                continue;
            }
            Stack<string> directories = new();
            directories.Push(source);
            while (directories.TryPop(out string? directory))
            {
                try
                {
                    foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            FileAttributes attributes = File.GetAttributes(entry);
                            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                            if ((attributes & FileAttributes.Directory) != 0) directories.Push(entry);
                            else if (Extensions.Contains(Path.GetExtension(entry))) tracks.TryAdd(entry, new WebMusicTrack(entry, source));
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            errors.Add($"{entry}: {exception.Message}");
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{directory}: {exception.Message}");
                }
            }
        }
        return new(tracks.Values.OrderBy(track => track.Title, StringComparer.CurrentCultureIgnoreCase).ToArray(), errors);
    }

    private List<string> Load()
    {
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path))?
                .Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Die Quellenkonfiguration der Weboberfläche ist ungültig.", exception);
        }
    }

    private void Save(IReadOnlyList<string> updated)
    {
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory)) throw new InvalidOperationException("Das Konfigurationsverzeichnis fehlt.");
        Directory.CreateDirectory(directory);
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string Normalize(string source) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
}
