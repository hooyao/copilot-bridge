namespace CopilotBridge.Cli.Pipeline.Routing;

/// <summary>
/// Fuzzy "nearest known model id" matcher. Used by the profile catalogs to fall
/// back to a similar model's wire contract when an inbound Copilot model id has
/// no exact profile — so a bridge build that predates a new Copilot model still
/// forwards requests for it (with the closest known model's coercion rules)
/// instead of hard-refusing. See <see cref="ModelProfileCatalog.GetNearest"/>.
/// </summary>
/// <remarks>
/// <para>Similarity is <b>Jaccard over case-insensitive character bigrams</b> of
/// the two ids: <c>|A∩B| / |A∪B|</c> where A/B are the sets of adjacent
/// character pairs. This is cheap, allocation-light, dependency-free (AOT-safe),
/// and behaves sensibly on the model-id shape: neighbours in the same family
/// score high (<c>claude-sonnet-5</c> vs <c>claude-sonnet-4.6</c> ≈ 0.76),
/// cross-family ids score low (<c>claude-sonnet-5</c> vs <c>claude-haiku-4.5</c>
/// ≈ 0.4), and an unrelated id scores near zero.</para>
/// <para>Ranking is Jaccard first, then a <b>family-then-version</b> tie-break so
/// two candidates at (near-)equal similarity resolve deterministically toward the
/// same family and the highest version — the least-restrictive, most-likely-correct
/// superset for a newer sibling. The final tiebreak is ordinal id so the result is
/// always stable.</para>
/// <para>A <b>similarity floor</b> (<see cref="DefaultMinSimilarity"/>) guards
/// against genuine typos / unrelated ids: below it, <see cref="FindNearest"/>
/// returns <c>null</c> and the caller keeps its hard error.</para>
/// </remarks>
internal static class ModelNameMatcher
{
    /// <summary>
    /// Minimum Jaccard similarity for a fuzzy match to be accepted. Below this,
    /// the candidate is treated as unrelated (typo / unknown vendor) and
    /// <see cref="FindNearest"/> returns null. Chosen so same-family neighbours
    /// (≈0.7+) and adjacent families (≈0.4) pass, but an unrelated string
    /// (≈0.2) does not. Not user-configurable today — promoting it to
    /// appsettings is a trivial follow-up.
    /// </summary>
    public const double DefaultMinSimilarity = 0.30;

    /// <summary>
    /// Returns the id in <paramref name="knownIds"/> most similar to
    /// <paramref name="requestedId"/>, or <c>null</c> if the best similarity is
    /// below <paramref name="minSimilarity"/> (or the inputs are empty).
    /// <paramref name="score"/> receives the winning Jaccard score (0 when null
    /// is returned).
    /// </summary>
    public static string? FindNearest(
        string requestedId,
        IReadOnlyList<string> knownIds,
        out double score,
        double minSimilarity = DefaultMinSimilarity)
    {
        score = 0.0;
        if (string.IsNullOrEmpty(requestedId) || knownIds.Count == 0) return null;

        var requestedBigrams = Bigrams(requestedId);
        var (reqFamily, reqMajor, reqMinor) = ParseFamilyVersion(requestedId);

        string? best = null;
        var bestScore = -1.0;
        var bestSameFamily = false;
        var bestMajor = int.MinValue;
        var bestMinor = int.MinValue;

        foreach (var candidate in knownIds)
        {
            var j = Jaccard(requestedBigrams, Bigrams(candidate));
            var (candFamily, candMajor, candMinor) = ParseFamilyVersion(candidate);
            var sameFamily = reqFamily is not null
                && string.Equals(reqFamily, candFamily, StringComparison.OrdinalIgnoreCase);

            // Rank: Jaccard (bucketed so near-ties group), then same-family,
            // then higher version, then ordinal id for a stable final tiebreak.
            // Compare against the current best; replace only on a strict win.
            if (best is null || IsBetter(
                    j, sameFamily, candMajor, candMinor, candidate,
                    bestScore, bestSameFamily, bestMajor, bestMinor, best))
            {
                best = candidate;
                bestScore = j;
                bestSameFamily = sameFamily;
                bestMajor = candMajor;
                bestMinor = candMinor;
            }
        }

        if (best is null || bestScore < minSimilarity) return null;
        score = bestScore;
        return best;
    }

    /// <summary>
    /// True if candidate A ranks strictly better than the current best B.
    /// Order: higher Jaccard (bucketed by <see cref="ScoreEpsilon"/> so
    /// near-ties fall through to the structural tie-breaks) → same-family →
    /// higher major → higher minor → ordinally-greater id (stable).
    /// </summary>
    private static bool IsBetter(
        double aScore, bool aSameFamily, int aMajor, int aMinor, string aId,
        double bScore, bool bSameFamily, int bMajor, int bMinor, string bId)
    {
        if (aScore - bScore > ScoreEpsilon) return true;
        if (bScore - aScore > ScoreEpsilon) return false;
        // Near-equal Jaccard: family-then-version tie-break.
        if (aSameFamily != bSameFamily) return aSameFamily;
        if (aMajor != bMajor) return aMajor > bMajor;
        if (aMinor != bMinor) return aMinor > bMinor;
        return string.CompareOrdinal(aId, bId) > 0;
    }

    /// <summary>
    /// Jaccard scores within this band are treated as tied, so the structural
    /// (family/version) tie-break decides. Small enough that genuinely-closer
    /// ids still win on similarity alone.
    /// </summary>
    private const double ScoreEpsilon = 0.01;

    /// <summary>Jaccard index of two bigram sets: <c>|∩| / |∪|</c>. Empty/empty = 0.</summary>
    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 0.0;
        var intersection = 0;
        foreach (var g in a)
        {
            if (b.Contains(g)) intersection++;
        }
        var union = a.Count + b.Count - intersection;
        return union == 0 ? 0.0 : (double)intersection / union;
    }

    /// <summary>
    /// Case-insensitive character bigrams of <paramref name="s"/>. A single-char
    /// id yields the one char itself so it isn't an empty set. Non-empty inputs
    /// always produce at least one element.
    /// </summary>
    private static HashSet<string> Bigrams(string s)
    {
        var lower = s.ToLowerInvariant();
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (lower.Length == 1)
        {
            set.Add(lower);
            return set;
        }
        for (var i = 0; i < lower.Length - 1; i++)
        {
            set.Add(lower.Substring(i, 2));
        }
        return set;
    }

    /// <summary>
    /// Splits a model id into (family, major, minor) for the version tie-break.
    /// Family = the first all-alphabetic hyphen segment after a known vendor
    /// prefix (<c>claude-<b>sonnet</b>-5</c> → <c>sonnet</c>; <c>gpt-5.5</c> →
    /// <c>gpt</c>). Version = the first segment shaped <c>major</c> or
    /// <c>major.minor</c> (<c>4.6</c> → (4, 6); <c>5</c> → (5, -1)). Missing
    /// pieces come back as <c>null</c> / <see cref="int.MinValue"/> so a candidate
    /// without a version never beats one that has it on the version tiebreak.
    /// </summary>
    internal static (string? Family, int Major, int Minor) ParseFamilyVersion(string id)
    {
        var parts = id.ToLowerInvariant().Split('-');
        string? family = null;
        var major = int.MinValue;
        var minor = int.MinValue;

        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.Length == 0) continue;

            if (family is null && IsAllLetters(p) && !IsVendorPrefix(p))
            {
                family = p;
            }

            if (major == int.MinValue && TryParseVersion(p, out var mj, out var mn))
            {
                major = mj;
                minor = mn;
            }
        }

        // A pure-vendor id like "gpt-5.5" has no non-vendor alpha segment; use
        // the vendor token itself as the family so gpt ids group together.
        if (family is null && parts.Length > 0 && IsAllLetters(parts[0]))
        {
            family = parts[0];
        }

        return (family, major, minor);
    }

    private static bool IsVendorPrefix(string s) =>
        s is "claude" or "gpt" or "gemini" or "mai" or "o3" or "o4";

    private static bool IsAllLetters(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (!char.IsLetter(s[i])) return false;
        }
        return s.Length > 0;
    }

    /// <summary>Parses <c>major</c> or <c>major.minor</c>; false if not numeric.</summary>
    private static bool TryParseVersion(string s, out int major, out int minor)
    {
        major = 0;
        minor = -1;
        var dot = s.IndexOf('.');
        if (dot < 0)
        {
            return int.TryParse(s, out major);
        }
        return int.TryParse(s.AsSpan(0, dot), out major)
            && int.TryParse(s.AsSpan(dot + 1), out minor);
    }
}
