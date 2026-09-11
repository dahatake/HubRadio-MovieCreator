using System.Globalization;
using System.Numerics;
using System.Text;
using HubRadio.App.Rendering;
using HubRadio.Core.Evaluation;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Rendering;
using HubRadio.Core.Settings;
using HubRadio.Core.Subtitles;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Rendering;
using HubRadio.Infrastructure.Subtitles;

namespace HubRadio.App.Projects;

internal static class SourceOnlyResultObservationBuilder
{
    internal static SourceOnlyResultMeasurements Capture(StandardGeneratedArtifact generated, OutputQcResult qc)
    {
        OutputQcRequest request = generated.QualityControlRequest ?? throw SourceOnlyResultCodec.Invalid();
        RenderGraph graph = generated.Graph;
        RenderManifest manifest = generated.Manifest;
        if (qc.ArtifactId != generated.Artifact.Id || qc.ProjectId != generated.Artifact.ProjectId ||
            qc.ContentDigest != generated.Artifact.ContentDigest || qc.ByteSize != generated.Artifact.ByteSize ||
            qc.RenderManifestId != manifest.Id || qc.RenderGraphId != graph.Id ||
            qc.CompositionVersionId != graph.CompositionVersionId || qc.CompositionVersionDigest != graph.CompositionVersionDigest ||
            qc.ProfileId != graph.ProfileId || qc.ProfileDigest != graph.ProfileDigest ||
            qc.LayoutSpecId != graph.LayoutSpecId || qc.LayoutSpecDigest != graph.LayoutSpecDigest ||
            qc.TemplateId != graph.Template.Id || qc.TemplateVersionDigest != graph.Template.VersionDigest ||
            qc.SubtitleLayoutProfileDigest != request.SubtitleEvidence?.LayoutProfileDigest ||
            qc.SubtitleDisplayProfileDigest != request.SubtitleEvidence?.DisplayProfileDigest ||
            qc.VideoStreamIndex != qc.VideoTiming.StreamIndex || qc.AudioStreamIndex != qc.AudioTiming?.StreamIndex ||
            (qc.AudioTiming is null) != (qc.AudioMeasurements is null) ||
            graph.Nodes.Any(n => n.Kind == RenderNodeKind.Audio) != (qc.AudioTiming is not null) ||
            qc.DurationMeasurements.ExpectedDuration != graph.Nodes.Single(n => n.Id == graph.OutputNodeId).OutputRange.Duration)
        { throw new SourceOnlyResultException(SourceOnlyResultFailureCode.BindingMismatch); }

        bool continuous = ValidateTiming(qc.VideoTiming);
        if (qc.AudioTiming is not null) _ = ValidateTiming(qc.AudioTiming);
        SourceOnlyResultCodec.Require(qc.LumaMeasurements.MeasuredFrameCount == qc.VideoTiming.ObservedFrameCount &&
            qc.LumaMeasurements.Frames.Select(f => f.PresentationTimestampTicks)
                .SequenceEqual(qc.VideoTiming.Entries.Select(e => e.PresentationTimestampTicks)) &&
            qc.DurationMeasurements.VideoDurationDelta == qc.VideoTiming.ObservedDuration - qc.DurationMeasurements.ExpectedDuration &&
            qc.DurationMeasurements.AudioDurationDelta == qc.AudioTiming?.ObservedDuration - qc.DurationMeasurements.ExpectedDuration &&
            qc.DurationMeasurements.AudioVideoStartOffset == qc.AudioTiming?.PresentationStart - qc.VideoTiming.PresentationStart &&
            qc.DurationMeasurements.AudioVideoEndOffset == qc.AudioTiming?.PresentationEnd - qc.VideoTiming.PresentationEnd);
        var result = new SourceOnlyResultMeasurements(qc.DurationMeasurements.ExpectedDuration,
            qc.VideoTiming.PresentationStart, qc.VideoTiming.PresentationEnd,
            qc.AudioTiming?.PresentationStart, qc.AudioTiming?.PresentationEnd,
            qc.VideoTiming.ObservedFrameCount, qc.AudioTiming?.ObservedFrameCount,
            qc.ProbeEvidenceDigest, qc.VideoTiming.EvidenceDigest, qc.AudioTiming?.EvidenceDigest,
            qc.SubtitleTiming?.EvidenceDigest, qc.LumaMeasurements.EvidenceDigest, qc.AudioMeasurements?.EvidenceDigest,
            qc.LumaMeasurements.MinimumFrameAverage, qc.LumaMeasurements.MaximumFrameAverage, qc.LumaMeasurements.MeanFrameAverage,
            Measured(qc.AudioMeasurements?.IntegratedLoudnessLufs), Measured(qc.AudioMeasurements?.TruePeakDbfs),
            qc.Decode.DecodedSuccessfully, MeasureCues(generated), qc.VideoTiming.TimeBase, qc.AudioTiming?.TimeBase,
            continuous, HasExactCadence(qc.VideoTiming, generated.ValidationContext.Profile.FrameRate));
        SourceOnlyResultCodec.ValidateMeasurements(result);
        return result;
    }

    internal static IReadOnlyList<SourceOnlyResultSubtitleMeasurement> MeasureCues(StandardGeneratedArtifact generated)
    {
        SourceOnlyResultCodec.Require(generated.DisplayCues.Count <= StandardSubtitleInput.MaximumCueCount);
        return Array.AsReadOnly(generated.DisplayCues.OrderBy(c => c.Cue.Range.Start).ThenBy(c => c.Cue.Id.ToString(), StringComparer.Ordinal)
            .Select(c =>
            {
                OutputQcSubtitleLayoutEvidence? layout = generated.SubtitleEvidence?.Layouts.SingleOrDefault(l => l.CueId == c.Cue.Id);
                SubtitleLayoutRect? rect = layout?.Resolution.Plan?.Placement.Rect;
                LayoutRegion? region = generated.ValidationContext.LayoutSpec.Regions.SingleOrDefault(r => r.Name == c.Cue.RegionName);
                bool safe = layout?.Resolution.Status == SubtitleLayoutResolutionStatus.Resolved && rect is not null &&
                    region?.Role == LayoutRegionRole.Subtitle && SubtitleLayoutGeometry.IsWithinOutputFrame(rect) &&
                    SubtitleLayoutGeometry.Contains(region.Rect, rect) && generated.ValidationContext.LayoutSpec.SafeZones.Count > 0 &&
                    generated.ValidationContext.LayoutSpec.SafeZones.All(z => SubtitleLayoutGeometry.Contains(z.Rect, rect));
                return MeasureCue(c, safe);
            }).ToArray());
    }

    internal static SourceOnlyResultSubtitleMeasurement MeasureCue(SubtitleFileCue cue, bool safe)
    {
        SourceOnlyResultCodec.Require(cue.Text.Length <= 32768);
        string[] lines = cue.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        int[] lengths = lines.Select(line => StringInfo.ParseCombiningCharacters(line).Length).ToArray();
        return new(cue.Cue.Id, cue.Cue.Range.Start, cue.Cue.Range.Duration, lengths.Sum(), lengths.Max(), lengths.Length, safe);
    }

    // Verify summaries against every measured entry, not caller-supplied endpoints.
    internal static bool ValidateTiming(OutputQcStreamTiming timing)
    {
        RationalTime? previousEnd = null;
        bool continuous = true;
        for (int i = 0; i < timing.Entries.Count; i++)
        {
            OutputQcTimingEntry e = timing.Entries[i];
            RationalTime start = Scale(timing.TimeBase, e.PresentationTimestampTicks);
            RationalTime end = start + Scale(timing.TimeBase, e.DurationTicks);
            SourceOnlyResultCodec.Require(e.Sequence == i && e.DurationTicks > 0 &&
                (i != 0 || start == timing.PresentationStart) && (previousEnd is null || start >= previousEnd));
            if (previousEnd is not null && start != previousEnd) continuous = false;
            previousEnd = end;
        }
        SourceOnlyResultCodec.Require(previousEnd == timing.PresentationEnd && timing.ObservedFrameCount == timing.Entries.Count);
        return continuous;
    }

    internal static bool HasExactCadence(OutputQcStreamTiming timing, RationalNumber frameRate) =>
        timing.Entries.All(e => Scale(timing.TimeBase, e.DurationTicks) == new RationalTime(frameRate.Denominator, frameRate.Numerator));

    internal static RationalTime Scale(RationalTime time, long factor)
    {
        BigInteger numerator = (BigInteger)time.Value * factor;
        BigInteger gcd = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), time.Rate);
        return new RationalTime(checked((long)(numerator / gcd)), checked((long)(time.Rate / gcd)));
    }

    private static SourceOnlyResultMeasuredValue? Measured(OutputQcMeasuredValue? v) =>
        v is null ? null : new(v.Kind, v.FiniteValue);

    internal static IReadOnlyList<AutomatedEvaluationRuleObservation> Build(
        SourceOnlyResultBinding binding, SourceOnlyResultArtifact artifact,
        DefaultSettingsProfile profile, DefaultSettingsSnapshot settings,
        ContentDigest evidenceDigest, bool provenanceValid, bool settingsValid, bool sidecarVerified,
        IReadOnlyList<SourceOnlyResultSubtitleMeasurement> cues, RationalTime expectedDuration, RationalNumber frameRate,
        bool imageVerified = false)
    {
        var rules = AutomatedEvaluationRules.Machine.ToDictionary(r => r, r =>
            AutomatedEvaluationRuleObservation.NotEvaluated(r, AutomatedEvaluationRules.GetDefinition(r).Scope == AutomatedEvaluationRuleScope.UserInterface
                ? "source_only.ui_not_measured" : "source_only.measurement_unavailable"));
        EntityId<AutomatedEvaluationArtifactIdTag> evaluationArtifactId = EntityId<AutomatedEvaluationArtifactIdTag>.Parse(artifact.ArtifactId.ToString());
        AutomatedEvaluationTarget Evidence() => AutomatedEvaluationTarget.Evidence(binding.ProjectId, artifact.VersionId,
            artifact.VersionDigest, evaluationArtifactId, artifact.ArtifactDigest);
        AutomatedEvaluationTarget Setting(string key) => AutomatedEvaluationTarget.Settings(binding.ProjectId, artifact.VersionId,
            artifact.VersionDigest, key, evaluationArtifactId, artifact.ArtifactDigest);
        AutomatedEvaluationTarget Editor(AutomatedEvaluationEditorKind editor) => AutomatedEvaluationTarget.Editor(binding.ProjectId,
            artifact.VersionId, artifact.VersionDigest, editor, evaluationArtifactId, artifact.ArtifactDigest);
        AutomatedEvaluationTarget Preview(RationalTime time) => AutomatedEvaluationTarget.Preview(binding.ProjectId, artifact.VersionId,
            artifact.VersionDigest, AutomatedEvaluationTimeBasis.Output, time < default(RationalTime) ? default : time,
            evaluationArtifactId, artifact.ArtifactDigest);
        void Pass(AutomatedEvaluationRuleId r, string reason) => rules[r] = AutomatedEvaluationRuleObservation.ObjectivePass(r, reason, evidenceDigest);
        void Fail(AutomatedEvaluationRuleId r, string reason, AutomatedEvaluationTarget target) =>
            rules[r] = AutomatedEvaluationRuleObservation.ObjectiveFail(r, reason, evidenceDigest, target);
        void Warn(AutomatedEvaluationRuleId r, string reason, AutomatedEvaluationTarget target) =>
            rules[r] = AutomatedEvaluationRuleObservation.ObjectiveWarning(r, reason, evidenceDigest, target);
        bool Number(AutomatedEvaluationRuleId rule, string key, DefaultSettingUnit unit, out Fraction number)
        {
            number = default;
            if (!settingsValid || !TryNumber(profile, settings, key, unit, out number))
            {
                Fail(rule, "source_only.required_setting_missing_or_invalid", Setting(key)); return false;
            }
            return true;
        }
        bool Nonnegative(AutomatedEvaluationRuleId rule, string key, DefaultSettingUnit unit, out Fraction n)
        {
            if (!Number(rule, key, unit, out n)) return false;
            if (n.Numerator.Sign >= 0) return true;
            Fail(rule, "source_only.required_setting_missing_or_invalid", Setting(key)); return false;
        }

        if (provenanceValid) Pass(AutomatedEvaluationRuleId.Provenance, "source_only.provenance_verified");
        else Fail(AutomatedEvaluationRuleId.Provenance, "source_only.provenance_invalid", Evidence());
        if (settingsValid) Pass(AutomatedEvaluationRuleId.SettingsBinding, "source_only.frozen_settings_bound");
        else Fail(AutomatedEvaluationRuleId.SettingsBinding, "source_only.settings_mismatch", Setting("evaluation.defaultMode"));

        SourceOnlyResultMeasurements? m = artifact.Measurements;
        if (artifact.IsVideo && m is null)
        {
            Fail(AutomatedEvaluationRuleId.FullDecode, artifact.FailureCode == SourceOnlyResultFailureCode.Canceled
                ? "source_only.qc_canceled" : "source_only.qc_failed", Evidence());
            if (artifact.QcFailureStage == OutputQcStage.AudioLoudness)
                Fail(AutomatedEvaluationRuleId.Loudness, "source_only.loudness_measurement_invalid", Evidence());
            return Array.AsReadOnly(AutomatedEvaluationRules.Machine.Select(r => rules[r]).ToArray());
        }
        if (artifact.IsVideo)
        {
            // A real OutputQc return proves its explicit container/codec/topology assertions,
            // not loudness, duration, A/V synchronization or subjective quality thresholds.
            Pass(AutomatedEvaluationRuleId.Codec, "source_only.qc_codec_and_profile_verified");
            if (m!.DecodedSuccessfully) Pass(AutomatedEvaluationRuleId.FullDecode, "source_only.full_decode_verified");
            else Fail(AutomatedEvaluationRuleId.FullDecode, "source_only.decode_failed", Evidence());
            // v2 technical identity rule, NOT a subjective tolerance. Audio padding
            // is not a video-duration defect. All terms are exact rational values.
            bool durationMatches = m.ExpectedDuration == expectedDuration && m.VideoStart == default(RationalTime) &&
                m.VideoEnd == expectedDuration && m.VideoTimingContinuous && m.VideoFrameCadenceExact &&
                (BigInteger)m.VideoFrameCount * frameRate.Denominator * expectedDuration.Rate ==
                    (BigInteger)expectedDuration.Value * frameRate.Numerator;
            if (durationMatches) Pass(AutomatedEvaluationRuleId.Duration, "source_only.video_presentation_exact_v2");
            else Fail(AutomatedEvaluationRuleId.Duration, "source_only.video_presentation_mismatch_v2", Evidence());
            if (m.AudioVideoStartOffset is RationalTime start && m.AudioVideoEndOffset is RationalTime end)
            {
                if (start == default(RationalTime) && end == default(RationalTime))
                    Pass(AutomatedEvaluationRuleId.AvSync, "source_only.av_presentation_exact_v2");
                else Warn(AutomatedEvaluationRuleId.AvSync, "timing.numeric_tolerance_unapproved", Evidence());
            }
            bool internalOnline = settings.Values.SingleOrDefault(v => v.Key == "audio.quickStartLoudnessProfile")?.Value.TextValue == "internalOnline";
            if (!internalOnline) Fail(AutomatedEvaluationRuleId.Loudness, "source_only.unsupported_loudness_profile", Setting("audio.quickStartLoudnessProfile"));
            else if (Number(AutomatedEvaluationRuleId.Loudness, "audio.internalOnlineTargetLoudness", DefaultSettingUnit.LoudnessUnitsFullScale, out Fraction target) &&
                Nonnegative(AutomatedEvaluationRuleId.Loudness, "audio.internalOnlineLoudnessTolerance", DefaultSettingUnit.Decibels, out Fraction loudnessTolerance))
            {
                if (m.Loudness?.Kind == OutputQcMeasuredValueKind.NegativeInfinity)
                    Warn(AutomatedEvaluationRuleId.Loudness, m.OutputDigitalSilence
                        ? "source_only.output_digital_silence_measured" : "source_only.integrated_loudness_not_measurable", Evidence());
                else if (m.Loudness is { Kind: OutputQcMeasuredValueKind.Finite, Value: decimal loudness } &&
                    Fraction.Abs(Fraction.From(loudness) - target) <= loudnessTolerance)
                    Pass(AutomatedEvaluationRuleId.Loudness, "source_only.loudness_within_frozen_tolerance");
                else Fail(AutomatedEvaluationRuleId.Loudness, "source_only.loudness_outside_frozen_tolerance", Editor(AutomatedEvaluationEditorKind.Audio));
            }
            if (Number(AutomatedEvaluationRuleId.TruePeak, "audio.truePeakMaximum", DefaultSettingUnit.DecibelsFullScale, out Fraction peak))
            {
                if (m.TruePeak?.Kind == OutputQcMeasuredValueKind.NegativeInfinity)
                    Pass(AutomatedEvaluationRuleId.TruePeak, "source_only.true_peak_negative_infinity_below_ceiling");
                else if (m.TruePeak is { Kind: OutputQcMeasuredValueKind.Finite, Value: decimal measuredPeak } && Fraction.From(measuredPeak) <= peak)
                    Pass(AutomatedEvaluationRuleId.TruePeak, "source_only.true_peak_within_frozen_limit");
                else Fail(AutomatedEvaluationRuleId.TruePeak, "source_only.true_peak_above_frozen_limit", Editor(AutomatedEvaluationEditorKind.Audio));
            }
        }
        else
        {
            string notVideo = artifact.IsImage ? "source_only.image_has_no_video_qc" : "source_only.sidecar_has_no_video_qc";
            foreach (AutomatedEvaluationRuleId rule in new[] { AutomatedEvaluationRuleId.FullDecode, AutomatedEvaluationRuleId.Duration,
                AutomatedEvaluationRuleId.AvSync, AutomatedEvaluationRuleId.Loudness, AutomatedEvaluationRuleId.TruePeak })
                rules[rule] = AutomatedEvaluationRuleObservation.NotEvaluated(rule, notVideo);
            if (artifact.IsImage && imageVerified) Pass(AutomatedEvaluationRuleId.Codec, "source_only.png_structure_verified_not_visual_quality");
            else if (!artifact.IsImage && sidecarVerified) Pass(AutomatedEvaluationRuleId.Codec, "source_only.sidecar_bytes_and_cues_verified");
            else Fail(AutomatedEvaluationRuleId.Codec, "source_only.non_video_bytes_invalid", Evidence());
            if (artifact.IsImage)
            {
                foreach (AutomatedEvaluationRuleId rule in SubtitleRules)
                    rules[rule] = AutomatedEvaluationRuleObservation.NotEvaluated(rule, "source_only.source_frame_has_no_subtitles");
                return Array.AsReadOnly(AutomatedEvaluationRules.Machine.Select(r => rules[r]).ToArray());
            }
        }

        if (artifact.Role == SourceOnlyResultRole.LandscapeClean)
        {
            foreach (AutomatedEvaluationRuleId rule in SubtitleRules)
                rules[rule] = AutomatedEvaluationRuleObservation.NotEvaluated(rule, "source_only.intentional_subtitle_free_rendition");
        }
        else if (cues.Count == 0)
        {
            Fail(AutomatedEvaluationRuleId.SubtitlePresence, "source_only.subtitles_missing", Editor(AutomatedEvaluationEditorKind.Subtitles));
        }
        else
        {
            if (artifact.IsVideo) Warn(AutomatedEvaluationRuleId.SubtitlePresence, "source_only.burn_in_inputs_verified_not_ocr", Editor(AutomatedEvaluationEditorKind.Subtitles));
            else if (sidecarVerified) Pass(AutomatedEvaluationRuleId.SubtitlePresence, "source_only.sidecar_subtitles_present");
            else Fail(AutomatedEvaluationRuleId.SubtitlePresence, "source_only.sidecar_bytes_invalid", Evidence());
            if (Nonnegative(AutomatedEvaluationRuleId.SubtitleReadingSpeed, "subtitles.readingSpeedMaximum", DefaultSettingUnit.CharactersPerSecond, out Fraction cps))
            {
                SourceOnlyResultSubtitleMeasurement? bad = cues.FirstOrDefault(c => !(new Fraction(c.CharacterCount, 1) / Fraction.From(c.Duration) <= cps));
                if (bad is null) Pass(AutomatedEvaluationRuleId.SubtitleReadingSpeed, "source_only.display_text_reading_speed_verified");
                else Warn(AutomatedEvaluationRuleId.SubtitleReadingSpeed, "source_only.subtitle_reading_speed_exceeded", Preview(bad.Start));
            }
            string lineKey = artifact.Role == SourceOnlyResultRole.VerticalBurnIn ? "subtitles.verticalLineLengthMaximum" : "subtitles.horizontalLineLengthMaximum";
            if (Nonnegative(AutomatedEvaluationRuleId.SubtitleLineLength, lineKey, DefaultSettingUnit.FullWidthCharacters, out Fraction lineLength))
            {
                if (cues.All(c => new Fraction(c.MaximumLineCharacters, 1) <= lineLength)) Pass(AutomatedEvaluationRuleId.SubtitleLineLength, "source_only.conservative_full_width_line_length_verified");
                else Warn(AutomatedEvaluationRuleId.SubtitleLineLength, "source_only.subtitle_line_length_exceeded", Setting(lineKey));
            }
            if (Nonnegative(AutomatedEvaluationRuleId.SubtitleLineCount, "subtitles.lineCountMaximum", DefaultSettingUnit.Count, out Fraction lineCount))
            {
                if (cues.All(c => new Fraction(c.LineCount, 1) <= lineCount)) Pass(AutomatedEvaluationRuleId.SubtitleLineCount, "source_only.subtitle_line_count_verified");
                else Warn(AutomatedEvaluationRuleId.SubtitleLineCount, "source_only.subtitle_line_count_exceeded", Editor(AutomatedEvaluationEditorKind.Subtitles));
            }
            if (Nonnegative(AutomatedEvaluationRuleId.SubtitleTiming, "subtitles.cueMinimumDuration", DefaultSettingUnit.Seconds, out Fraction min) &&
                Nonnegative(AutomatedEvaluationRuleId.SubtitleTiming, "subtitles.cueMaximumDuration", DefaultSettingUnit.Seconds, out Fraction max) &&
                Nonnegative(AutomatedEvaluationRuleId.SubtitleTiming, "subtitles.cueGapMinimum", DefaultSettingUnit.Frames, out Fraction gapFrames))
            {
                Fraction gap = gapFrames / new Fraction(frameRate.Numerator, frameRate.Denominator);
                bool invalid = cues.Any(c => c.Start + c.Duration > expectedDuration) || !(min <= max);
                bool warning = cues.Any(c => !(min <= Fraction.From(c.Duration) && Fraction.From(c.Duration) <= max));
                for (int i = 1; i < cues.Count; i++)
                {
                    RationalTime actualGap = cues[i].Start - cues[i - 1].Start - cues[i - 1].Duration;
                    invalid |= actualGap < default(RationalTime);
                    warning |= !(gap <= Fraction.From(actualGap));
                }
                if (invalid) Fail(AutomatedEvaluationRuleId.SubtitleTiming, "source_only.subtitle_timing_invalid", Editor(AutomatedEvaluationEditorKind.Subtitles));
                else if (warning) Warn(AutomatedEvaluationRuleId.SubtitleTiming, "source_only.subtitle_duration_or_gap_outside_limits", Setting("subtitles.cueMinimumDuration"));
                else Pass(AutomatedEvaluationRuleId.SubtitleTiming, "source_only.renderer_cue_timing_verified");
            }
            if (!artifact.IsVideo)
                rules[AutomatedEvaluationRuleId.SubtitleSafeZone] = AutomatedEvaluationRuleObservation.NotEvaluated(AutomatedEvaluationRuleId.SubtitleSafeZone, "source_only.sidecar_player_layout_not_measured");
            else if (cues.Any(c => !c.SafeZoneSatisfied))
                Fail(AutomatedEvaluationRuleId.SubtitleSafeZone, "source_only.subtitle_safe_zone_invalid", Editor(AutomatedEvaluationEditorKind.Layout));
            else
                Warn(AutomatedEvaluationRuleId.SubtitleSafeZone, "source_only.geometry_verified_platform_ui_unmeasured", Setting("subtitles.platformUiMaskStatus"));
        }
        return Array.AsReadOnly(AutomatedEvaluationRules.Machine.Select(r => rules[r]).ToArray());
    }

    private static readonly AutomatedEvaluationRuleId[] SubtitleRules =
    [AutomatedEvaluationRuleId.SubtitlePresence, AutomatedEvaluationRuleId.SubtitleReadingSpeed, AutomatedEvaluationRuleId.SubtitleLineLength,
        AutomatedEvaluationRuleId.SubtitleLineCount, AutomatedEvaluationRuleId.SubtitleTiming, AutomatedEvaluationRuleId.SubtitleSafeZone];

    private static bool TryNumber(DefaultSettingsProfile profile, DefaultSettingsSnapshot settings, string key, DefaultSettingUnit unit, out Fraction value)
    {
        value = default;
        DefaultSettingDefinition? definition = profile.Definitions.SingleOrDefault(d => d.Key == key);
        DefaultSettingValue? selected = settings.Values.SingleOrDefault(v => v.Key == key)?.Value;
        if (definition?.Unit != unit || selected is null) return false;
        if (selected.Kind == DefaultSettingValueKind.Integer && selected.IntegerValue is long n) { value = new Fraction(n, 1); return true; }
        if (selected.Kind == DefaultSettingValueKind.Rational && selected.RationalValue is RationalNumber r) { value = new Fraction(r.Numerator, r.Denominator); return true; }
        return false;
    }

    private readonly record struct Fraction(BigInteger Numerator, BigInteger Denominator)
    {
        internal static Fraction From(RationalTime t) => new(t.Value, t.Rate);
        internal static Fraction From(decimal d)
        {
            int[] bits = decimal.GetBits(d);
            BigInteger n = (uint)bits[0] | ((BigInteger)(uint)bits[1] << 32) | ((BigInteger)(uint)bits[2] << 64);
            return new(bits[3] < 0 ? -n : n, BigInteger.Pow(10, (bits[3] >> 16) & 0xff));
        }
        internal static Fraction Abs(Fraction f) => new(BigInteger.Abs(f.Numerator), f.Denominator);
        public static Fraction operator -(Fraction a, Fraction b) => new(a.Numerator * b.Denominator - b.Numerator * a.Denominator, a.Denominator * b.Denominator);
        public static Fraction operator /(Fraction a, Fraction b)
        {
            if (b.Numerator.Sign <= 0) throw SourceOnlyResultCodec.Invalid();
            return new(a.Numerator * b.Denominator, a.Denominator * b.Numerator);
        }
        public static bool operator <=(Fraction a, Fraction b) => a.Numerator * b.Denominator <= b.Numerator * a.Denominator;
        public static bool operator >=(Fraction a, Fraction b) => b <= a;
    }
}