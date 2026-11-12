namespace FlowEncode.Infrastructure;

// Same per-step estimate as deew 5.1's CLI:
// https://github.com/pcrlnd/deew/blob/5.1.0/src/ui.rs#L213-L228
// Use the raw stage percent, never FlowEncode's weighted overall progress.
internal sealed class DeewStageTimeEstimate(TimeProvider clock)
{
    private string? _stage;
    private long _started;
    private double? _percent;

    public void Update(string stage, double? percent)
    {
        if (_stage != stage)
        {
            _stage = stage;
            _started = clock.GetTimestamp();
        }
        _percent = percent;
    }

    public TimeSpan? Remaining
    {
        get
        {
            if (_stage is not ("ffmpeg" or "decode" or "measure" or "encode")
                || _percent is not double percent || !double.IsFinite(percent) || percent <= 0 || percent >= 100) return null;

            var elapsed = clock.GetElapsedTime(_started).TotalSeconds;
            if (elapsed < 5) return null;

            var fraction = percent / 100;
            var seconds = Math.Round(elapsed * (1 - fraction) / fraction, MidpointRounding.AwayFromZero);
            // Upstream shows "??" above 9:59:59; hide an unusable estimate here.
            return double.IsFinite(seconds) && seconds <= 35999
                ? TimeSpan.FromSeconds(seconds)
                : null;
        }
    }
}
