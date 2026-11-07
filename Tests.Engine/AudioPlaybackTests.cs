using System.Text;
using Xunit;

public sealed class AudioPlaybackTests
{
    [Fact]
    public void DecodesAndMixesAFileWithoutAnOutputDevice()
    {
        if (!OperatingSystem.IsWindows()) return;

        BassNativeLibraries.Register();
        Assert.NotEqual(0, AudioNative.Init(0, 44100, 0, IntPtr.Zero, IntPtr.Zero));
        string path = Path.Combine(Path.GetTempPath(), $"playout-{Guid.NewGuid():N}.wav");
        try
        {
            using (BinaryWriter writer = new(File.Create(path), Encoding.ASCII))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(40);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(44100);
                writer.Write(88200);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(4);
                writer.Write((short)16384);
                writer.Write((short)-16384);
            }

            using PlaybackDeck deck = new("Deck 1");
            deck.Load(path);
            deck.Play();
            float[] output = new float[8];
            deck.Mix(output, 2, 4, 1, 3, 44100);

            Assert.Equal(0.5f, output[1], 3);
            Assert.Equal(0.5f, output[3], 3);
            Assert.Equal(-0.5f, output[5], 3);
            Assert.Equal(-0.5f, output[7], 3);
            Assert.Equal("Ended", deck.GetStatus().State);
            deck.Stop();
            Assert.Equal("Loaded", deck.GetStatus().State);
        }
        finally
        {
            File.Delete(path);
            AudioNative.Free();
        }
    }
}
