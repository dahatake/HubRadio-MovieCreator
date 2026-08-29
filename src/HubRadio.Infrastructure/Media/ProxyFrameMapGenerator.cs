using System.Text;
using HubRadio.Core.Identity;
using HubRadio.Core.Media;
using HubRadio.Infrastructure.Files;

namespace HubRadio.Infrastructure.Media;

public enum ProxyFrameMapFailureReason
{
    InputBindingMismatch,
    TimingEvidenceInvalid,
    ArithmeticOverflow,
}

public sealed class ProxyFrameMapException : Exception
{
    internal ProxyFrameMapException(ProxyFrameMapFailureReason reason)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
    }

    public ProxyFrameMapFailureReason Reason { get; }

    private static string GetStableMessage(ProxyFrameMapFailureReason reason) =>
        reason switch
        {
            ProxyFrameMapFailureReason.InputBindingMismatch =>
                "The source and proxy evidence do not identify one generated proxy.",
            ProxyFrameMapFailureReason.TimingEvidenceInvalid =>
                "The observed source and proxy timestamps cannot form a frame map.",
            ProxyFrameMapFailureReason.ArithmeticOverflow =>
                "The proxy frame map cannot be represented exactly.",
            _ => "Proxy frame-map generation failed.",
        };
}

public sealed class ProxyFrameMapGenerator
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public ProxyFrameMap Generate(
        PtsIndex sourcePtsIndex,
        GeneratedProxy generatedProxy)
    {
        ArgumentNullException.ThrowIfNull(sourcePtsIndex);
        ArgumentNullException.ThrowIfNull(generatedProxy);

        ValidateInputBinding(sourcePtsIndex, generatedProxy);
        try
        {
            ContentDigest sourceIndexDigest = ComputeIndexDigest(sourcePtsIndex);
            ContentDigest proxyIndexDigest = ComputeIndexDigest(
                generatedProxy.ProxyPtsIndex);
            return ProxyFrameMap.Create(
                generatedProxy.SourceAssetId,
                sourcePtsIndex.ProjectId,
                generatedProxy.SourceContentDigest,
                sourceIndexDigest,
                generatedProxy.ProxyArtifact.ContentDigest,
                proxyIndexDigest,
                sourcePtsIndex.Entries
                    .Select(static entry => entry.PresentationTime)
                    .ToArray(),
                sourcePtsIndex.PresentationEnd,
                generatedProxy.ProxyPtsIndex.Entries
                    .Select(static entry => entry.PresentationTime)
                    .ToArray());
        }
        catch (OverflowException)
        {
            throw Failure(ProxyFrameMapFailureReason.ArithmeticOverflow);
        }
        catch (ArgumentException)
        {
            throw Failure(ProxyFrameMapFailureReason.TimingEvidenceInvalid);
        }
    }

    internal static void ValidateInputBinding(
        PtsIndex sourcePtsIndex,
        GeneratedProxy generatedProxy)
    {
        ArgumentNullException.ThrowIfNull(sourcePtsIndex);
        ArgumentNullException.ThrowIfNull(generatedProxy);

        PtsIndex proxyPtsIndex = generatedProxy.ProxyPtsIndex;
        if (sourcePtsIndex.CandidateId != generatedProxy.SourceAssetId ||
            sourcePtsIndex.ContentDigest != generatedProxy.SourceContentDigest ||
            sourcePtsIndex.ProjectId != generatedProxy.ProxyArtifact.ProjectId ||
            generatedProxy.ProxyArtifact.Kind != ArtifactKind.Proxy ||
            proxyPtsIndex.CandidateId != generatedProxy.SourceAssetId ||
            proxyPtsIndex.ProjectId != sourcePtsIndex.ProjectId ||
            proxyPtsIndex.JobId != generatedProxy.ProxyProbeResult.JobId ||
            proxyPtsIndex.ContentDigest != generatedProxy.ProxyArtifact.ContentDigest ||
            proxyPtsIndex.ByteSize != generatedProxy.ProxyArtifact.ByteSize ||
            generatedProxy.ProxyProbeResult.ContentDigest !=
                generatedProxy.ProxyArtifact.ContentDigest ||
            generatedProxy.ProxyProbeResult.ByteSize !=
                generatedProxy.ProxyArtifact.ByteSize)
        {
            throw Failure(ProxyFrameMapFailureReason.InputBindingMismatch);
        }
    }

    private static ContentDigest ComputeIndexDigest(PtsIndex index) =>
        ContentDigest.Compute(StrictUtf8.GetBytes(index.CanonicalJson));

    private static ProxyFrameMapException Failure(
        ProxyFrameMapFailureReason reason) =>
        new(reason);
}