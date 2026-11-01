using HubRadio.App.Rendering;
using HubRadio.Core.Time;

namespace HubRadio.App.Tests;

/// <summary>
/// 原因調査 hang-probe: 発話入り動画（映像 9.000 s・音声 8.355 s）の source-only Job が
/// composition 段階で source_only.unsupported_source_timing（PipelineFailure、Job 側は job.execution_failed）になる。
/// SourceOnlyPipeline.ValidateSourceTiming が SourceAudioTailPolicy.Covers（音声末尾の不足は 250 ms まで）を要求するため。
/// これは A-047-01 の意図した拒否であり、素材（音声が映像より 250 ms 超短い）の問題として固定する。
/// </summary>
[TestClass]
public sealed class SourceAudioTailPolicyTests
{
    private static RationalTime Ms(long milliseconds) => new(milliseconds, 1000);

    [TestMethod]
    public void Covers_AudioEqualOrLonger_IsAccepted()
    {
        Assert.IsTrue(SourceAudioTailPolicy.Covers(Ms(9000), Ms(9000)));
        Assert.IsTrue(SourceAudioTailPolicy.Covers(Ms(9100), Ms(9000)));
    }

    [TestMethod]
    public void Covers_ShortfallUpToMaximum_IsAcceptedAndBeyondIsRejected()
    {
        Assert.IsTrue(SourceAudioTailPolicy.Covers(Ms(8750), Ms(9000)));
        Assert.IsFalse(SourceAudioTailPolicy.Covers(Ms(8749), Ms(9000)));
    }

    [TestMethod]
    public void Covers_SpeechSampleWithAudio8355MsAgainstVideo9000Ms_IsRejected()
    {
        // 保全された失敗素材（hp-speech.mp4）の実測値。不足 645 ms > 250 ms。
        Assert.IsFalse(SourceAudioTailPolicy.Covers(Ms(8355), Ms(9000)));
    }
}
