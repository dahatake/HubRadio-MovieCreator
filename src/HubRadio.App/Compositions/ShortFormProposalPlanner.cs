using HubRadio.Core.Provenance;

namespace HubRadio.App.Compositions;

/// <summary>FR-023: the ten short-form composition types.</summary>
public enum ShortFormCompositionType
{
    Educational,
    Surprise,
    MisconceptionCorrection,
    FailureStory,
    Comparison,
    Ranking,
    QuestionAndAnswer,
    Story,
    Quiz,
    DemoFirst,
}

/// <summary>One ranked item whose position is grounded in the source video or input material.</summary>
public sealed record ShortFormRankingEvidence(int Rank, string Label, OriginRef Origin);

/// <summary>Grounded input for short-form proposals. Origins are the source spans/assets the proposal may cite.</summary>
public sealed record ShortFormProposalInput(
    IReadOnlyList<OriginRef> SourceOrigins,
    IReadOnlyList<ShortFormRankingEvidence> RankingEvidence);

public sealed record ShortFormProposal(
    ShortFormCompositionType Type,
    IReadOnlyList<OriginRef> Origins,
    IReadOnlyList<ShortFormRankingEvidence> RankingItems);

/// <summary>
/// FR-023: proposes short-form composition types. The Ranking type is only proposed when at least two distinct,
/// positive ranks each carry an origin; a ranking is never invented.
/// </summary>
public static class ShortFormProposalPlanner
{
    public static IReadOnlyList<ShortFormProposal> Propose(ShortFormProposalInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.SourceOrigins.Count == 0)
        {
            return [];
        }

        ShortFormRankingEvidence[] ranking = GroundedRanking(input.RankingEvidence);
        var proposals = new List<ShortFormProposal>();
        foreach (ShortFormCompositionType type in Enum.GetValues<ShortFormCompositionType>())
        {
            if (type == ShortFormCompositionType.Ranking)
            {
                if (ranking.Length >= 2)
                {
                    proposals.Add(new ShortFormProposal(type, ranking.Select(static item => item.Origin).ToArray(), ranking));
                }

                continue;
            }

            proposals.Add(new ShortFormProposal(type, input.SourceOrigins, []));
        }

        return proposals;
    }

    private static ShortFormRankingEvidence[] GroundedRanking(IReadOnlyList<ShortFormRankingEvidence> evidence)
    {
        ShortFormRankingEvidence[] valid = evidence
            .Where(static item => item is not null && item.Rank > 0 && item.Origin is not null
                && !string.IsNullOrWhiteSpace(item.Label))
            .GroupBy(static item => item.Rank)
            .Where(static group => group.Count() == 1)
            .Select(static group => group.Single())
            .OrderBy(static item => item.Rank)
            .ToArray();
        return valid;
    }
}
