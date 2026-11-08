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

    public bool Mix(float[] target, int frames, int channels, int left, int right, double outputRate) =>
        Mix(target, frames, channels, left, right, outputRate, out _);

    public bool Mix(float[] target, int frames, int channels, int left, int right, double outputRate, out float level)
    {
        double sumSquares = 0;
        int measuredFrames = 0;
        bool completed = true;
        if (!primed)
        {
            var first = readFrame();
            if (first is null)
            {
                level = 0;
                return false;
            }
            current = first.Value;
            next = readFrame();
            primed = true;
        }
        for (int frame = 0; frame < frames; frame++)
        {
            float t = (float)fraction;
            float leftSample = next is { } following ? current.Left + (following.Left - current.Left) * t : current.Left;
            float rightSample = next is { } followingRight ? current.Right + (followingRight.Right - current.Right) * t : current.Right;
            target[frame * channels + left] += leftSample;
            target[frame * channels + right] += rightSample;
            sumSquares += leftSample * leftSample + rightSample * rightSample;
            measuredFrames++;
            fraction += sourceRate / outputRate;
            while (fraction >= 1)
            {
                fraction -= 1;
                if (next is null)
                {
                    completed = false;
                    break;
                }
                current = next.Value;
                next = readFrame();
            }
            if (!completed) break;
        }
        level = measuredFrames == 0 ? 0 : (float)Math.Sqrt(sumSquares / (measuredFrames * 2));
        return completed;
    }
}
