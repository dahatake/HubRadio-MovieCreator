using HubRadio.Core.Identity;
using HubRadio.Core.Layout;

namespace HubRadio.Core.Rendering;

public sealed class RenderProfileIdTag
{
}

public sealed class RenderProfile : IEquatable<RenderProfile>
{
    public RenderProfile(
        EntityId<RenderProfileIdTag> id,
        string name,
        ContentDigest profileDigest,
        int width,
        int height,
        long frameRateNumerator,
        long frameRateDenominator,
        EntityId<LayoutSpecIdTag> layoutSpecId,
        EntityId<TemplateIdTag> templateId,
        ContentDigest templateVersionDigest,
        string videoCodec,
        string audioCodec,
        bool subtitleBurnIn)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(profileDigest);
        ArgumentNullException.ThrowIfNull(layoutSpecId);
        ArgumentNullException.ThrowIfNull(templateId);
        ArgumentNullException.ThrowIfNull(templateVersionDigest);

        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "Pixel width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "Pixel height must be positive.");
        }

        if (frameRateNumerator <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameRateNumerator),
                frameRateNumerator,
                "Frame-rate numerator must be positive.");
        }

        if (frameRateDenominator <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameRateDenominator),
                frameRateDenominator,
                "Frame-rate denominator must be positive.");
        }

        Id = id;
        Name = ContractText.RequireNonBlank(name, nameof(name), "A render-profile name");
        ProfileDigest = profileDigest;
        Width = width;
        Height = height;
        FrameRate = new RationalNumber(frameRateNumerator, frameRateDenominator);
        LayoutSpecId = layoutSpecId;
        TemplateId = templateId;
        TemplateVersionDigest = templateVersionDigest;
        VideoCodec = ContractText.RequireNonBlank(videoCodec, nameof(videoCodec), "A video codec name");
        AudioCodec = ContractText.RequireNonBlank(audioCodec, nameof(audioCodec), "An audio codec name");
        SubtitleBurnIn = subtitleBurnIn;
    }

    public EntityId<RenderProfileIdTag> Id { get; }

    public string Name { get; }

    public ContentDigest ProfileDigest { get; }

    public int Width { get; }

    public int Height { get; }

    public RationalNumber FrameRate { get; }

    public long FrameRateNumerator => FrameRate.Numerator;

    public long FrameRateDenominator => FrameRate.Denominator;

    public EntityId<LayoutSpecIdTag> LayoutSpecId { get; }

    public EntityId<TemplateIdTag> TemplateId { get; }

    public ContentDigest TemplateVersionDigest { get; }

    public string VideoCodec { get; }

    public string AudioCodec { get; }

    public bool SubtitleBurnIn { get; }

    public bool Equals(RenderProfile? other)
    {
        return other is not null
            && Id == other.Id
            && StringComparer.Ordinal.Equals(Name, other.Name)
            && ProfileDigest == other.ProfileDigest
            && Width == other.Width
            && Height == other.Height
            && FrameRate == other.FrameRate
            && LayoutSpecId == other.LayoutSpecId
            && TemplateId == other.TemplateId
            && TemplateVersionDigest == other.TemplateVersionDigest
            && StringComparer.Ordinal.Equals(VideoCodec, other.VideoCodec)
            && StringComparer.Ordinal.Equals(AudioCodec, other.AudioCodec)
            && SubtitleBurnIn == other.SubtitleBurnIn;
    }

    public override bool Equals(object? obj)
    {
        return obj is RenderProfile other && Equals(other);
    }

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Id);
        hash.Add(Name, StringComparer.Ordinal);
        hash.Add(ProfileDigest);
        hash.Add(Width);
        hash.Add(Height);
        hash.Add(FrameRate);
        hash.Add(LayoutSpecId);
        hash.Add(TemplateId);
        hash.Add(TemplateVersionDigest);
        hash.Add(VideoCodec, StringComparer.Ordinal);
        hash.Add(AudioCodec, StringComparer.Ordinal);
        hash.Add(SubtitleBurnIn);
        return hash.ToHashCode();
    }
}