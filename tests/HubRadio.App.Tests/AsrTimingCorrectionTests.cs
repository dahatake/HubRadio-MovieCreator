using HubRadio.App.Analysis;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class AsrTimingCorrectionTests
{
    private static readonly (double Start, double End)[] Silences = [(1.46, 2.17), (4.2, 4.65)];

    // FR-057.5 AC-032.5: a word edge inside a measured silence moves to the silence boundary.
    [TestMethod]
    [TestCategory("FR-057")]
    [DataRow(1.08, 2.28, 1.08, 1.46)]   // silence inside, 0.11 s after it → trailing noise, ends where the silence starts
    [DataRow(2.0, 2.6, 2.17, 2.6)]      // word start inside the silence → starts where the silence ends
    [DataRow(1.5, 2.0, 1.5, 2.0)]       // word entirely inside a silence: the recognizer heard something, keep
    [DataRow(4.0, 5.0, 4.65, 5.0)]      // silence inside, 0.2 s before it → leading noise, starts where the silence ends
    [DataRow(3.9, 4.95, 3.9, 4.95)]     // silence inside with speech on both sides: keep
    [DataRow(1.40, 1.9, 1.40, 1.9)]     // would shorten below 0.1 s: keep
    [DataRow(3.0, 3.5, 3.0, 3.5)]       // no silence: keep
    public void WordEdgesMoveToSilenceBoundaries(double start, double end, double expectedStart, double expectedEnd)
    {
        (double correctedStart, double correctedEnd) = AsrTimingCorrection.Correct(start, end, Silences);

        Assert.AreEqual(expectedStart, correctedStart, 1e-9);
        Assert.AreEqual(expectedEnd, correctedEnd, 1e-9);
    }

    // FR-057.5 AC-032.5: the pinned evidence is canonical and fail-closed.
    [TestMethod]
    [TestCategory("FR-057")]
    public void EvidenceRoundTripsAndRejectsDamage()
    {
        string json = HubRadio.Core.Serialization.CanonicalJson.Canonicalize(
            AsrTimingCorrection.Write("detector.v1", [(16000L, 32000L), (40000L, 48000L)]));

        IReadOnlyList<(double Start, double End)> read = AsrTimingCorrection.Read(json, "detector.v1", 160000, 16000);
        CollectionAssert.AreEqual(new[] { (1.0, 2.0), (2.5, 3.0) }, read.ToArray());
        Assert.ThrowsExactly<FormatException>(() => AsrTimingCorrection.Read(json, "detector.v2", 160000, 16000));
        Assert.ThrowsExactly<FormatException>(() => AsrTimingCorrection.Read(json, "detector.v1", 40000, 16000));
        Assert.ThrowsExactly<FormatException>(() => AsrTimingCorrection.Read(
            json.Replace("[\"40000\",\"48000\"]", "[\"30000\",\"48000\"]", StringComparison.Ordinal), "detector.v1", 160000, 16000));
        Assert.ThrowsExactly<FormatException>(() => AsrTimingCorrection.Read(
            json.Replace("}", ",\"extra\":1}", StringComparison.Ordinal), "detector.v1", 160000, 16000));
    }
}
