using System.Text.Json;
using Playout.Gui;
using Xunit;

public sealed class MusicLibraryTests
{
    [Fact]
    public void ScansMultipleSourcesWithoutDuplicatesAndReportsMissingSources()
    {
        string root = Path.Combine(Path.GetTempPath(), "playout-music-" + Guid.NewGuid().ToString("N"));
        string nested = Path.Combine(root, "nested");
        Directory.CreateDirectory(nested);
        try
        {
            File.WriteAllText(Path.Combine(root, "Alpha.MP3"), "");
            File.WriteAllText(Path.Combine(nested, "Beta.wav"), "");
            File.WriteAllText(Path.Combine(nested, "ignore.txt"), "");
            MusicLibrary library = new(Path.Combine(root, "sources.json"));
            string missing = Path.Combine(root, "missing");

            MusicScanResult result = library.Scan([root, nested, missing], CancellationToken.None);

            Assert.Equal(new[] { "Alpha", "Beta" }, result.Tracks.Select(track => track.Title));
            Assert.Single(result.Errors);
            Assert.Contains(missing, result.Errors[0]);

            File.Delete(Path.Combine(root, "Alpha.MP3"));
            Assert.Single(library.Scan([root, nested], CancellationToken.None).Tracks);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PersistsSourcesAndRejectsBrokenJson()
    {
        string root = Path.Combine(Path.GetTempPath(), "playout-sources-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "config", "sources.json");
            MusicLibrary library = new(path);
            library.SaveSources([root]);
            Assert.Equal(new[] { MusicLibrary.NormalizePath(root) }, library.LoadSources());
            File.WriteAllText(path, "{");
            Assert.Throws<InvalidDataException>(() => library.LoadSources());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ScanHonorsCancellation()
    {
        MusicLibrary library = new(Path.Combine(Path.GetTempPath(), "unused.json"));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => library.Scan([Path.GetTempPath()], cancellation.Token));
    }
}
