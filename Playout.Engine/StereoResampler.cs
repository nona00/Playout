internal sealed class StereoResampler
{
    private readonly Func<(float Left, float Right)?> readFrame;
    private readonly double sourceRate;
    private double fraction;
    private (float Left, float Right) current;
    private (float Left, float Right)? next;
    private bool primed;

    public StereoResampler(double sourceRate, Func<(float Left, float Right)?> readFrame)
    {
        if (!double.IsFinite(sourceRate) || sourceRate <= 0) throw new ArgumentOutOfRangeException(nameof(sourceRate));
        this.sourceRate = sourceRate;
        this.readFrame = readFrame;
    }

    public bool Mix(float[] target, int frames, int channels, int left, int right, double outputRate)
    {
        if (!primed)
        {
            var first = readFrame();
            if (first is null) return false;
            current = first.Value;
            next = readFrame();
            primed = true;
        }
        for (int frame = 0; frame < frames; frame++)
        {
            float t = (float)fraction;
            target[frame * channels + left] += next is { } following ? current.Left + (following.Left - current.Left) * t : current.Left;
            target[frame * channels + right] += next is { } followingRight ? current.Right + (followingRight.Right - current.Right) * t : current.Right;
            fraction += sourceRate / outputRate;
            while (fraction >= 1)
            {
                fraction -= 1;
                if (next is null) return false;
                current = next.Value;
                next = readFrame();
            }
        }
        return true;
    }
}
