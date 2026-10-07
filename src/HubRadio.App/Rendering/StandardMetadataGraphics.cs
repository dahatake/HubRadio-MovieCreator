using System.Globalization;
using System.Text;
using HubRadio.App.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Rendering;
using HubRadio.Infrastructure.Subtitles;

namespace HubRadio.App.Rendering;

public enum StandardTextProvenanceKind
{
    SourceAssetMetadata,
    PlannedMetadata,
    SettingsCallToAction,
    /// <summary>FR-059: verbatim text of a displayed subtitle cue (emphasis caption).</summary>
    OriginalTranscript,
}

public sealed record StandardTextOverlayEvidence(
    string Role, string Text, string RenderedText, StandardTextProvenanceKind Provenance,
    IReadOnlyList<OriginRef> Origins, EntityId<LongFormElementIdTag>? ElementId,
    EntityId<LongFormPlanIdTag> LongFormPlanId, EntityId<StandardShortFormPlanIdTag>? ShortFormId,
    ContentDigest SettingsSnapshotDigest, TimeRange OutputRange, string TimingRule,
    int Left, int Top, int Right, int Bottom, int FontSize);

public sealed record StandardGraphicRenderFile(RenderGraphInput Input, SubtitleExportFile File,
    FfmpegAssGraphicBinding Binding, StandardTextOverlayEvidence Evidence);

internal static class StandardMetadataGraphics
{
    internal static IReadOnlyList<StandardGraphicRenderFile> Create(StandardCompositionPlan plan,
        StandardShortFormPlan? shortForm, EntityId<RenderNodeIdTag> target,
        RenderProfile profile, RationalTime duration, RationalTime firstCutDuration,
        RenderFontEvidence font, StandardRenderConfiguration? configuration,
        IReadOnlyList<StandardEmphasisTelop>? telops = null)
    {
        var files = new List<StandardGraphicRenderFile>();
        RationalTime titleMaximum = configuration?.TitleMaximumDuration ?? new RationalTime(3, 1);
        RationalTime endMaximum = configuration?.EndOverlayMaximumDuration ?? new RationalTime(5, 1);
        ContentDigest settings = StandardRenderGraphFactory.InputDigest(plan, StandardPlanInputKind.SettingsSnapshot);
        if (configuration is not null && configuration.SettingsSnapshotDigest != settings)
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.BindingMismatch);
        foreach (LongFormElementPlan element in plan.LongForm.Elements.Where(static item =>
            item.State == LongFormElementState.Included && item.Kind != LongFormElementKind.Body))
        {
            // Short forms retain the episode title, not long-form-only metadata.
            if (shortForm is not null && element.Kind != LongFormElementKind.EpisodeTitle) continue;
            if (element.Text is null || element.Origins.Count == 0
                || element.Kind is not (LongFormElementKind.EpisodeTitle or LongFormElementKind.ProgramName
                    or LongFormElementKind.EpisodeNumber or LongFormElementKind.Topic
                    or LongFormElementKind.Summary or LongFormElementKind.CallToAction))
                throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedPlanElement);
            bool tail = element.Kind is LongFormElementKind.Summary or LongFormElementKind.CallToAction;
            RationalTime maximum = tail ? endMaximum : titleMaximum;
            RationalTime bound = tail ? duration : (firstCutDuration < duration ? firstCutDuration : duration);
            RationalTime length = maximum < bound ? maximum : bound;
            TimeRange range = element.OutputRange ?? new TimeRange(tail ? duration - length : default, length);
            if (element.Kind == LongFormElementKind.Topic && element.OutputRange is null
                || range.Start < default(RationalTime) || range.End > duration)
                throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedPlanElement);
            Add(element.Kind.ToString(), element.Text, element.Origins, element.Id, range,
                element.OutputRange is null ? (tail ? "last-min(endScreenDuration,outputDuration)" : "first-min(shortHookMaximumStart,firstSourceCut)") : "explicit-plan-range",
                element.Kind == LongFormElementKind.EpisodeTitle && element.Text == plan.LongForm.Project.ContentName
                    && element.Origins.Any(static origin => origin.Kind == OriginKind.Asset)
                    ? StandardTextProvenanceKind.SourceAssetMetadata : StandardTextProvenanceKind.PlannedMetadata,
                tail);
        }

        if (shortForm is not null)
        {
            if (shortForm.CallToAction.Kind != StandardCallToActionKind.ViewLongForm
                || shortForm.CallToAction.TargetLongFormPlanId != plan.LongForm.PlanId)
                throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.BindingMismatch);
            RationalTime length = endMaximum < duration ? endMaximum : duration;
            Add("ShortCallToAction", configuration?.CallToActionText ?? "本編を見る",
                shortForm.SourceSpans.Select(static span => OriginRef.FromSourceSpan(span.Id)).ToArray(), null,
                new TimeRange(duration - length, length), "last-min(endScreenDuration,shortDuration)",
                StandardTextProvenanceKind.SettingsCallToAction, true);
        }

        // FR-059: verbatim emphasis captions in their own band between the title and end-screen bands.
        foreach (StandardEmphasisTelop telop in telops ?? [])
        {
            if (telop.OutputRange.Start < default(RationalTime) || telop.OutputRange.End > duration)
                throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedPlanElement);
            Add("EmphasisTelop", telop.Text, [telop.Origin], null, telop.OutputRange, telop.Rule,
                StandardTextProvenanceKind.OriginalTranscript, tail: false, emphasis: true);
        }

        return files.AsReadOnly();

        void Add(string role, string text, IReadOnlyList<OriginRef> origins,
            EntityId<LongFormElementIdTag>? elementId, TimeRange range, string timing,
            StandardTextProvenanceKind provenance, bool tail, bool emphasis = false)
        {
            if (files.Count >= 64 || Encoding.UTF8.GetByteCount(text) > 2048)
                throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.ResourceLimit);
            int left = profile.Width / 10;
            int right = profile.Width - left;
            // Keep metadata away from the bottom speech-subtitle band. The
            // rectangle is a renderer policy, not measured platform-safe UI.
            int top = profile.Height / 10;
            int bottom = profile.Height * 3 / 10;
            if (tail) { top = profile.Height * 6 / 10; bottom = profile.Height * 3 / 4; }
            if (emphasis) { left = profile.Width / 20; right = profile.Width - left; top = profile.Height * 32 / 100; bottom = profile.Height * 46 / 100; }
            if (files.Any(file => file.Evidence.OutputRange.Start < range.End && range.Start < file.Evidence.OutputRange.End
                && left < file.Evidence.Right && file.Evidence.Left < right && top < file.Evidence.Bottom && file.Evidence.Top < bottom))
                throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedPlanElement);
            // Reject literal ASS controls before wrapping could split the sequence.
            SubtitleWriterContract.ValidateAssText(text);
            string[] glyphs = TextElements(text);
            int size = Math.Min(emphasis ? 128 : profile.Width >= 1920 ? 64 : 48,
                Math.Min((bottom - top - 12) / 2, (right - left - 24) * 2 / Math.Max(1, glyphs.Length)));
            if (size < 20) throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedPlanElement);
            int perLine = Math.Max(1, (right - left - 24) / size);
            string rendered = string.Join("\n", glyphs.Chunk(perLine).Select(static line => string.Concat(line)));
            if (rendered.Count(static c => c == '\n') > 1)
                throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.UnsupportedPlanElement);
            var cue = new SubtitleFileCue(new Cue(EntityId<CueIdTag>.New(), CueKind.Subtitle, range, "metadata"),
                rendered, SubtitleFileCueStyle.Normal);
            var white = new AssColor(0, 255, 255, 255);
            var black = new AssColor(0, 0, 0, 0);
            // Emphasis captions: bold yellow lettering with a thick black outline, no box.
            var fill = emphasis ? new AssColor(0, 255, 221, 0) : white;
            var ass = new AssSubtitleProfile(profile.Width, profile.Height, Enum.GetValues<SubtitleFileCueStyle>()
                .Select(style => new AssStyleDefinition(style, font.Name, size, fill, fill, black, black,
                    true, false, false, false, 100, 100, 0, 0, emphasis ? AssBorderStyle.OutlineAndShadow : AssBorderStyle.OpaqueBox,
                    emphasis ? 6 : 3, 0, AssAlignment.MiddleCenter, new AssMargins(12, 12, 12), 1)).ToArray());
            SubtitleExportFile file = StandardSubtitlePlacement.WriteAtBounds(cue, ass, left, top, right, bottom);
            var input = new RenderGraphInput(EntityId<RenderInputIdTag>.New(), RenderInputKind.Graphic,
                file.ContentDigest, file.ByteSize, origins[0]);
            files.Add(new StandardGraphicRenderFile(input, file, new FfmpegAssGraphicBinding(input.Id, target, range),
                new StandardTextOverlayEvidence(role, text, rendered, provenance, Array.AsReadOnly(origins.ToArray()), elementId,
                    plan.LongForm.PlanId, shortForm?.Id, settings, range, timing, left, top, right, bottom, size)));
        }
    }

    private static string[] TextElements(string text)
    {
        // The ASS writer escapes braces and real line breaks, preserves ordinary
        // backslashes and rejects literal reserved sequences.
        var result = new List<string>();
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext()) result.Add(elements.GetTextElement());
        return result.ToArray();
    }
}