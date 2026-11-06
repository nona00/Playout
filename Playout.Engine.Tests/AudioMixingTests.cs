using Xunit;

public sealed class AudioMixingTests
{
    [Fact]
    public void MultipleSourcesMixIntoTheSameStereoChannels()
    {
        StereoResampler first = Source(48000, (0.25f, 0.5f));
        StereoResampler second = Source(48000, (0.5f, -0.25f));
        float[] output = new float[6];

        first.Mix(output, 1, 6, 2, 5, 48000);
        second.Mix(output, 1, 6, 2, 5, 48000);

        Assert.Equal(new[] { 0f, 0f, 0.75f, 0f, 0f, 0.25f }, output);
    }

    [Fact]
    public void ResamplingKeepsItsPositionAcrossBuffers()
    {
        StereoResampler source = Source(2, (0f, 0f), (1f, -1f));
        float[] first = new float[4];
        float[] second = new float[4];

        Assert.True(source.Mix(first, 2, 2, 0, 1, 4));
        Assert.False(source.Mix(second, 2, 2, 0, 1, 4));

        Assert.Equal(new[] { 0f, 0f, 0.5f, -0.5f }, first);
        Assert.Equal(new[] { 1f, -1f, 1f, -1f }, second);
    }

    [Fact]
    public void DownsamplingSkipsInputFrames()
    {
        StereoResampler source = Source(4, (0f, 0f), (1f, 1f), (2f, 2f), (3f, 3f));
        float[] output = new float[4];

        Assert.False(source.Mix(output, 2, 2, 0, 1, 2));
        Assert.Equal(new[] { 0f, 0f, 2f, 2f }, output);
    }

    [Fact]
    public void RoutingAllowsSharedChannelsAndDisconnectedOutputs()
    {
        AudioRoutingConfiguration routing = AudioRoutingConfiguration.CreateDefault();
        AudioDeviceMapping mapping = new(AudioBackend.Wasapi, "interface", 0, 1);
        routing = routing with
        {
            Outputs = routing.Outputs!.Select((output, index) => index < 2 ? output with { Device = mapping } : output).ToArray()
        };

        Assert.Empty(AudioRoutingValidator.Validate(routing));
    }

    private static StereoResampler Source(double rate, params (float Left, float Right)[] frames)
    {
        int index = 0;
        return new StereoResampler(rate, () => index < frames.Length ? frames[index++] : null);
    }
}
