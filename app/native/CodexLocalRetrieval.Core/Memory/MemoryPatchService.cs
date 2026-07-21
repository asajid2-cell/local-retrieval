using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexLocalRetrieval.Core.Memory;

// Turns analyst candidates into a REVIEWABLE patch, and applies an approved patch to the card set.
// This is where the four robustness guards live:
//  1. The app never writes canonical files straight from model output — a build yields a patch; the
//     canonical card set only changes on Apply.
//  3. No card is canonical without a source anchor + source-grounded truth_evidence (fake-certainty guard).
//  + Multi-agent agreement raises EXTRACTION confidence only, never TRUTH evidence (two agents reading
//     the same transcript is not independent evidence).
//  + Supersede never deletes — a retired card stays as status=superseded for audit/rollback.
public sealed class MemoryPatchService
{
    private static readonly string[] TruthOrder =
    {
        TruthEvidence.Weak, TruthEvidence.SingleSource, TruthEvidence.RepeatedInSources,
        TruthEvidence.ResultBacked, TruthEvidence.UserConfirmed,
    };

    public static int TruthRank(string t)
    {
        var i = Array.IndexOf(TruthOrder, t);
        return i < 0 ? 0 : i;
    }

    // Canonical eligibility: a real source anchor AND source-grounded truth (repeated / result-backed /
    // user-confirmed). No anchor => can never be canonical.
    public static bool CanBeCanonical(MemoryCard c)
        => c.HasSourceAnchor && TruthRank(c.TruthEvidence) >= TruthRank(TruthEvidence.RepeatedInSources);

    // Promote a working card to canonical IF (and only if) it is now eligible. Returns whether it moved.
    public bool TryPromote(MemoryCard c)
    {
        if (c.Lane == Lanes.Canonical) return false;
        if (!CanBeCanonical(c)) return false;
        c.Lane = Lanes.Canonical;
        return true;
    }

    // Merge candidates (possibly from several analysts) and classify each into a patch against `existing`.
    public MemoryPatch BuildPatch(string collectionId, IReadOnlyList<MemoryCard> existing,
        IReadOnlyList<MemoryCard> candidates, string createdBy, string patchId, string nowIso)
    {
        var patch = new MemoryPatch
        {
            Id = patchId, CollectionId = collectionId, CreatedAt = nowIso, CreatedBy = createdBy,
        };
        var existingById = existing.ToDictionary(c => c.Id, c => c, StringComparer.Ordinal);

        foreach (var group in candidates.GroupBy(c => c.Id, StringComparer.Ordinal))
        {
            var merged = MergeGroup(group.ToList(), patchId, nowIso, patch);
            if (existingById.TryGetValue(merged.Id, out var prior))
            {
                // Carry forward provenance that a fresh extraction wouldn't know about.
                merged.Supersedes = prior.Supersedes;
                if (!CardsEquivalent(prior, merged)) patch.Updates.Add(merged);
            }
            else
            {
                patch.Adds.Add(merged);
            }
        }

        patch.SourceChatIds = candidates
            .SelectMany(c => c.Sources.Select(s => s.SessionId))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct().ToList();
        patch.Summary = $"{patch.Adds.Count} added, {patch.Updates.Count} updated" +
                        (patch.Conflicts.Count > 0 ? $", {patch.Conflicts.Count} disputed" : "");
        return patch;
    }

    private MemoryCard MergeGroup(List<MemoryCard> group, string patchId, string nowIso, MemoryPatch patch)
    {
        var first = group[0];
        var analystCount = group.Select(c => c.CreatedBy).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        // Union anchors (by quote hash) and topics across the group.
        var anchors = group.SelectMany(c => c.Sources)
            .GroupBy(s => s.QuoteHash + "|" + s.MsgStartIndex)
            .Select(g => g.First()).ToList();
        var topics = group.SelectMany(c => c.Topics).Distinct().ToList();

        // TRUTH = the WEAKEST claim in the group (agreement must not manufacture truth).
        var truth = group.OrderBy(c => TruthRank(c.TruthEvidence)).First().TruthEvidence;
        // EXTRACTION confidence rises ONLY with independent-analyst agreement.
        var extraction = analystCount >= 3 ? ExtractionConfidence.High
            : analystCount == 2 ? ExtractionConfidence.Medium
            : ExtractionConfidence.Low;

        var merged = new MemoryCard
        {
            Id = first.Id,
            Title = first.Title,
            Type = first.Type,
            Status = CardStatuses.Active,
            TruthEvidence = truth,
            ExtractionConfidence = extraction,
            Importance = group.Max(c => c.Importance),
            Durability = group.Max(c => c.Durability),
            Actionability = group.Max(c => c.Actionability),
            CreatedBy = analystCount > 1 ? "multi" : first.CreatedBy,
            CreatedAt = nowIso,
            DerivedFromPatchId = patchId,
            Sensitivity = group.Any(c => c.Sensitivity == Sensitivities.ContainsRedactedSecret)
                ? Sensitivities.ContainsRedactedSecret : Sensitivities.Normal,
            Topics = topics,
            Sources = anchors,
            Body = first.Body,
        };

        // If independent analysts disagreed on how strong the truth is, flag it (still classify by weakest).
        if (analystCount >= 2 && group.Select(c => c.TruthEvidence).Distinct().Count() > 1)
            patch.Conflicts.Add($"{merged.Id}: analysts disagreed on truth_evidence " +
                $"({string.Join(" vs ", group.Select(c => $"{c.CreatedBy}={c.TruthEvidence}").Distinct())}) — taking the weakest.");

        Classify(merged);
        if (merged.Type == CardTypes.QuestionForHuman || merged.Type == CardTypes.OpenLoop)
            if (!patch.OpenQuestions.Contains(merged.Title)) patch.OpenQuestions.Add(merged.Title);
        return merged;
    }

    // Final lane decision — overrides any analyst lane hint. No anchor => working + manual_unverified.
    public static void Classify(MemoryCard c)
    {
        if (!c.HasSourceAnchor)
        {
            c.Lane = Lanes.Working;
            c.SourceCoverage = SourceCoverage.ManualUnverified;
            return;
        }
        c.SourceCoverage = SourceCoverage.ExactSpan;
        c.Lane = CanBeCanonical(c) ? Lanes.Canonical : Lanes.Working;
    }

    // Apply an approved patch to the card set. Adds/updates by id; supersedes flip status (KEEP the
    // card — never delete). Returns the new full card set.
    public List<MemoryCard> Apply(IReadOnlyList<MemoryCard> existing, MemoryPatch patch)
    {
        var byId = existing.ToDictionary(c => c.Id, c => c, StringComparer.Ordinal);

        foreach (var sup in patch.Supersedes)
        {
            if (byId.TryGetValue(sup.Id, out var prior))
            {
                prior.Status = CardStatuses.Superseded;
                prior.SupersededReason = string.IsNullOrWhiteSpace(sup.SupersededReason) ? "Superseded by a newer card." : sup.SupersededReason;
                prior.SupersededBy = sup.SupersededBy;
            }
        }
        foreach (var add in patch.Adds) byId[add.Id] = add;
        foreach (var upd in patch.Updates) byId[upd.Id] = upd;

        return byId.Values.OrderBy(c => c.Lane).ThenByDescending(c => c.Importance).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
    }

    // Build a patch that supersedes an existing card with a replacement (and a reason). Supersede-not-delete.
    public MemoryPatch SupersedePatch(string collectionId, MemoryCard existingCard, MemoryCard? replacement,
        string reason, string patchId, string nowIso)
    {
        var patch = new MemoryPatch { Id = patchId, CollectionId = collectionId, CreatedAt = nowIso, CreatedBy = "user" };
        var marker = new MemoryCard { Id = existingCard.Id, SupersededReason = reason, SupersededBy = replacement?.Id ?? "" };
        patch.Supersedes.Add(marker);
        if (replacement != null)
        {
            replacement.DerivedFromPatchId = patchId;
            replacement.Supersedes = new List<string>(existingCard.Supersedes) { existingCard.Id };
            Classify(replacement);
            patch.Adds.Add(replacement);
        }
        patch.Summary = $"Superseded {existingCard.Id}" + (replacement != null ? $" with {replacement.Id}" : "");
        return patch;
    }

    private static bool CardsEquivalent(MemoryCard a, MemoryCard b)
        => a.Title == b.Title && a.Body == b.Body && a.Lane == b.Lane && a.Type == b.Type
           && a.TruthEvidence == b.TruthEvidence && a.ExtractionConfidence == b.ExtractionConfidence
           && a.Importance == b.Importance && a.Status == b.Status;
}
