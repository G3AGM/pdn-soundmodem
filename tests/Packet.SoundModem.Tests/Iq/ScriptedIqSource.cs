using M0LTE.Radio.Audio;

namespace Packet.SoundModem.Tests.Iq;

/// <summary>
/// An <see cref="IIqSource"/> that plays a list of segments, each with its own rate and centre,
/// and with a stated number of samples lost before it: the retunes and gaps a real transport
/// reports, on cue. A segment is never spanned by one read, and the counters move on the read
/// that returns a segment's first block, exactly as the interface says.
/// </summary>
internal sealed class ScriptedIqSource : IIqSource
{
    private readonly IReadOnlyList<Segment> _segments;
    private int _segment = -1;
    private int _offset;

    public ScriptedIqSource(IReadOnlyList<Segment> segments)
    {
        _segments = segments;
        SampleRate = segments[0].SampleRate;
        CentreFrequencyHz = segments[0].CentreHz;
    }

    public int SampleRate { get; private set; }

    public double CentreFrequencyHz { get; private set; }

    public long TuningGeneration { get; private set; }

    public long SamplesLost { get; private set; }

    public int Read(Span<float> interleaved)
    {
        if (_segment < 0 || _offset >= _segments[_segment].Iq.Length)
        {
            if (_segment + 1 >= _segments.Count)
            {
                return 0;
            }

            _segment++;
            _offset = 0;
            Segment next = _segments[_segment];
            if (_segment > 0 && (next.SampleRate != SampleRate || next.CentreHz != CentreFrequencyHz || next.Retune))
            {
                SampleRate = next.SampleRate;
                CentreFrequencyHz = next.CentreHz;
                TuningGeneration++;
            }

            SamplesLost += next.LostBefore;
        }

        float[] iq = _segments[_segment].Iq;
        int take = Math.Min(interleaved.Length & ~1, iq.Length - _offset);
        iq.AsSpan(_offset, take).CopyTo(interleaved);
        _offset += take;
        return take;
    }

    /// <summary>One stretch of IQ at one tuning.</summary>
    /// <param name="Iq">Interleaved I, Q.</param>
    /// <param name="SampleRate">Its rate.</param>
    /// <param name="CentreHz">Its centre.</param>
    /// <param name="LostBefore">Samples lost immediately before it.</param>
    /// <param name="Retune">Count a retune even if rate and centre are unchanged.</param>
    internal sealed record Segment(float[] Iq, int SampleRate, double CentreHz = 0, long LostBefore = 0, bool Retune = false);
}
