using Playout;
using Xunit;

public sealed class PlaylistLibraryTests
{
    [Fact]
    public void ReadsM3uEntriesAndResolvesRelativePaths()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string music = Path.Combine(root, "music");
            string playlists = Path.Combine(root, "playlists");
            Directory.CreateDirectory(music);
            Directory.CreateDirectory(playlists);
            string firstTrack = Path.Combine(music, "First.mp3");
            File.WriteAllText(firstTrack, string.Empty);
            File.WriteAllText(Path.Combine(playlists, "Mix.m3u"), "#EXTM3U\n#EXTINF:-1,First\n../music/First.mp3\n");
            PlaylistLibrary library = new(Path.Combine(root, "settings.json"));
            library.SaveFolder(playlists);

            PlaylistFile playlist = Assert.Single(library.GetPlaylists());
            IReadOnlyList<PlaylistTrack> tracks = library.LoadPlaylist(playlist.Name);

            Assert.Equal("Mix.m3u", playlist.Name);
            Assert.Equal(firstTrack, Assert.Single(tracks).Path);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SharesCurrentPlaylistBetweenInstancesAndClearsItWhenFolderChanges()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string playlists = Path.Combine(root, "playlists");
            string otherPlaylists = Path.Combine(root, "other-playlists");
            Directory.CreateDirectory(playlists);
            Directory.CreateDirectory(otherPlaylists);
            File.WriteAllText(Path.Combine(playlists, "Mix.m3u"), "song.mp3\n");
            string settingsPath = Path.Combine(root, "settings.json");
            PlaylistLibrary guiLibrary = new(settingsPath);
            guiLibrary.SaveFolder(playlists);
            guiLibrary.SaveCurrentPlaylist("Mix.m3u");

            PlaylistLibrary webLibrary = new(settingsPath);

            Assert.Equal("Mix.m3u", webLibrary.LoadCurrentPlaylist());
            webLibrary.SaveFolder(otherPlaylists);
            Assert.Equal(string.Empty, guiLibrary.LoadCurrentPlaylist());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ReadsPlsEntriesInFileNumberOrderAndIgnoresMetadata()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string playlists = Path.Combine(root, "playlists");
            Directory.CreateDirectory(playlists);
            File.WriteAllText(Path.Combine(playlists, "Mix.pls"), "[playlist]\nTitle1=First\nFile2=second.mp3\nLength2=12\nFile1=first.mp3\nNumberOfEntries=2\nVersion=2\n");
            PlaylistLibrary library = new(Path.Combine(root, "settings.json"));
            library.SaveFolder(playlists);

            IReadOnlyList<PlaylistTrack> tracks = library.LoadPlaylist("Mix.pls");

            Assert.Equal(new[] { "first.mp3", "second.mp3" }, tracks.Select(track => Path.GetFileName(track.Path)));
            Assert.Equal(new[] { "first", "second" }, tracks.Select(track => track.Title));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "playout-playlists-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
