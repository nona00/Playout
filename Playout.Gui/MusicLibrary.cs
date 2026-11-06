using System.Text.Json;

namespace Playout.Gui;

internal sealed record MusicTrack(string Path, string Source)
{
    public string Title => System.IO.Path.GetFileNameWithoutExtension(Path);
}

internal sealed record MusicScanResult(IReadOnlyList<MusicTrack> Tracks, IReadOnlyList<string> Errors);

internal sealed class MusicLibrary
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".mp2", ".mp1", ".wav", ".aif", ".aiff", ".ogg"
    };
    private readonly string sourcesPath;

    public MusicLibrary(string sourcesPath) => this.sourcesPath = sourcesPath;

    public IReadOnlyList<string> LoadSources()
    {
        if (!File.Exists(sourcesPath)) return [];
        try
        {
            List<string>? sources = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(sourcesPath));
            if (sources is null || sources.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("Die Quellenliste ist ungültig.");
            return sources.Select(NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Die Quellenliste kann nicht gelesen werden.", exception);
        }
    }

    public void SaveSources(IEnumerable<string> sources)
    {
        string? directory = Path.GetDirectoryName(sourcesPath);
        if (string.IsNullOrEmpty(directory)) throw new InvalidOperationException("Das Quellenverzeichnis fehlt.");
        Directory.CreateDirectory(directory);
        string temporaryPath = $"{sourcesPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(sources, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, sourcesPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public MusicScanResult Scan(IEnumerable<string> sources, CancellationToken cancellationToken)
    {
        Dictionary<string, MusicTrack> tracks = new(StringComparer.OrdinalIgnoreCase);
        List<string> errors = [];
        foreach (string source in sources)
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
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            FileAttributes attributes = File.GetAttributes(entry);
                            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                            if ((attributes & FileAttributes.Directory) != 0)
                                directories.Push(entry);
                            else if (Extensions.Contains(Path.GetExtension(entry)))
                                tracks.TryAdd(entry, new MusicTrack(entry, source));
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
        return new MusicScanResult(tracks.Values.OrderBy(track => track.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(track => track.Path, StringComparer.CurrentCultureIgnoreCase).ToArray(), errors);
    }
}
