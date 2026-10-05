using HubRadio.Core.Time;

namespace HubRadio.App.Rendering;

/// <summary>
/// FR-047.1: encoders commonly end the audio stream a few AAC frames before the last
/// video frame. A bounded tail shortfall is rendered as trailing silence; nothing is
/// retimed or stretched, and the zero-origin shared clock is still required.
/// </summary>
public static class SourceAudioTailPolicy
{
    /// <summary>250 ms (A-047-01).</summary>
    public static readonly RationalTime MaximumShortfall = new(1, 4);

    public static bool Covers(RationalTime audioEnd, RationalTime videoEnd) =>
        audioEnd + MaximumShortfall >= videoEnd;
}
