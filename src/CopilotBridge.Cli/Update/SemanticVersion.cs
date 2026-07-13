using System.Diagnostics.CodeAnalysis;

namespace CopilotBridge.Cli.Update;

/// <summary>
/// A minimal, allocation-light Semantic Versioning 2.0.0 value used to compare
/// the installed <see cref="Hosting.ProductInfo.Version"/> against GitHub
/// release tags. A dedicated type (rather than a NuGet package) keeps the
/// Native AOT dependency graph flat — version comparison is the only need, and
/// the rules are small and well specified.
/// </summary>
/// <remarks>
/// Precedence follows the SemVer 2.0.0 spec:
/// <list type="bullet">
///   <item>numeric compare of major, then minor, then patch;</item>
///   <item>a version WITH prerelease identifiers has LOWER precedence than the
///         same core version without them (stable &gt; prerelease);</item>
///   <item>prerelease identifiers compare left-to-right: numeric identifiers
///         compare numerically and rank below non-numeric ones, non-numeric
///         identifiers compare by ASCII ordinal, and a larger set of otherwise
///         equal leading identifiers ranks higher.</item>
/// </list>
/// Build metadata (after <c>+</c>) is ignored for precedence. An optional
/// leading <c>v</c>/<c>V</c> (release-tag convention) is stripped on parse.
/// Unparseable or component-overflowing input is rejected by
/// <see cref="TryParse"/> so a malformed release tag is simply skipped.
/// </remarks>
internal readonly struct SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    // Backing field; may be null on a default(SemanticVersion). Never exposed
    // directly — PreRelease coalesces so no member can NRE on a default value
    // (TryParse hands back default on every failure path).
    private readonly string[]? _preRelease;

    /// <summary>Dot-separated prerelease identifiers; empty for a stable release.</summary>
    public string[] PreRelease => _preRelease ?? [];

    private SemanticVersion(int major, int minor, int patch, string[] preRelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _preRelease = preRelease;
    }

    /// <summary>True when this version carries any prerelease identifier.</summary>
    public bool IsPreRelease => PreRelease.Length > 0;

    /// <summary>
    /// True when any prerelease identifier is <c>dev</c> (case-insensitive), e.g.
    /// the <c>0.1.0-dev</c> local-build version. Development builds never
    /// self-update — a release archive must not clobber a <c>dotnet run</c> /
    /// developer publish directory.
    /// </summary>
    public bool IsDevBuild
    {
        get
        {
            foreach (var id in PreRelease)
            {
                if (string.Equals(id, "dev", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Parse a semantic version or <c>v</c>-prefixed release tag. Returns false
    /// (and a default value) for anything that is not exactly
    /// <c>[v]MAJOR.MINOR.PATCH[-prerelease][+build]</c> with in-range integer
    /// core components and non-empty, character-legal prerelease identifiers.
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.Trim();
        if (span.Length > 0 && (span[0] == 'v' || span[0] == 'V'))
        {
            span = span[1..];
        }

        // Build metadata does not affect precedence — drop it before parsing.
        var plus = span.IndexOf('+');
        if (plus >= 0)
        {
            span = span[..plus];
        }

        string core;
        string? pre = null;
        var dash = span.IndexOf('-');
        if (dash >= 0)
        {
            core = span[..dash];
            pre = span[(dash + 1)..];
        }
        else
        {
            core = span;
        }

        var parts = core.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!TryParseComponent(parts[0], out var major)
            || !TryParseComponent(parts[1], out var minor)
            || !TryParseComponent(parts[2], out var patch))
        {
            return false;
        }

        var preIds = Array.Empty<string>();
        if (pre is not null)
        {
            if (pre.Length == 0)
            {
                return false;
            }

            preIds = pre.Split('.');
            foreach (var id in preIds)
            {
                if (!IsLegalIdentifier(id))
                {
                    return false;
                }
            }
        }

        version = new SemanticVersion(major, minor, patch, preIds);
        return true;
    }

    private static bool TryParseComponent(string s, out int value)
    {
        value = 0;
        if (s.Length == 0)
        {
            return false;
        }
        foreach (var c in s)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }
        // int.TryParse rejects values that overflow Int32 — an absurdly large
        // release-tag component is treated as unparseable and skipped.
        return int.TryParse(s, out value);
    }

    private static bool IsLegalIdentifier(string id)
    {
        if (id.Length == 0)
        {
            return false;
        }
        foreach (var c in id)
        {
            var ok = c is >= '0' and <= '9'
                or >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or '-';
            if (!ok)
            {
                return false;
            }
        }
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    private static int ComparePreRelease(string[] a, string[] b)
    {
        // Stable (no identifiers) outranks any prerelease of the same core.
        if (a.Length == 0 && b.Length == 0) return 0;
        if (a.Length == 0) return 1;
        if (b.Length == 0) return -1;

        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            var c = CompareIdentifier(a[i], b[i]);
            if (c != 0) return c;
        }
        // A larger set of otherwise-equal leading identifiers has higher precedence.
        return a.Length.CompareTo(b.Length);
    }

    private static int CompareIdentifier(string a, string b)
    {
        var na = IsNumeric(a);
        var nb = IsNumeric(b);
        if (na && nb) return CompareNumeric(a, b);
        // Numeric identifiers always have lower precedence than non-numeric ones.
        if (na) return -1;
        if (nb) return 1;
        return string.CompareOrdinal(a, b);
    }

    private static bool IsNumeric(string s)
    {
        foreach (var c in s)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }
        return s.Length > 0;
    }

    // Compare two all-digit identifiers without integer overflow: strip leading
    // zeros, then a longer number is larger, and same-length numbers compare
    // lexically. Prerelease numeric identifiers may exceed Int64 per the spec.
    private static int CompareNumeric(string a, string b)
    {
        var ta = a.TrimStart('0');
        var tb = b.TrimStart('0');
        if (ta.Length != tb.Length)
        {
            return ta.Length.CompareTo(tb.Length);
        }
        return string.CompareOrdinal(ta, tb);
    }

    public bool Equals(SemanticVersion other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SemanticVersion v && Equals(v);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Major);
        hash.Add(Minor);
        hash.Add(Patch);
        foreach (var id in PreRelease)
        {
            hash.Add(id, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }

    public override string ToString()
    {
        var core = $"{Major}.{Minor}.{Patch}";
        return PreRelease.Length == 0 ? core : $"{core}-{string.Join('.', PreRelease)}";
    }
}
