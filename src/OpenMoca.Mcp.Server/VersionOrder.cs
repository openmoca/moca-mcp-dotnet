namespace OpenMoca.Mcp;

/// <summary>
/// Orders package versions by Semantic Versioning precedence (package spec §4.3): major, minor and patch as numbers, a
/// pre-release before its release. A version that is not SemVer sorts below every one that is, then by ordinal.
/// </summary>
internal sealed class VersionOrder : IComparer<string>
{
    public static VersionOrder Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        var a = Parse(x);
        var b = Parse(y);
        if (a is null || b is null)
        {
            return a is null && b is null ? string.CompareOrdinal(x, y) : a is null ? -1 : 1;
        }

        for (var i = 0; i < 3; i++)
        {
            var c = CompareNumbers(a.Value.Core[i], b.Value.Core[i]);
            if (c != 0)
            {
                return c;
            }
        }

        // A pre-release comes before its release; two pre-releases compare field by field.
        if (a.Value.Pre is null || b.Value.Pre is null)
        {
            return a.Value.Pre is null ? (b.Value.Pre is null ? 0 : 1) : -1;
        }

        var pa = a.Value.Pre.Split('.');
        var pb = b.Value.Pre.Split('.');
        for (var i = 0; i < Math.Min(pa.Length, pb.Length); i++)
        {
            var na = pa[i].All(char.IsAsciiDigit);
            var nb = pb[i].All(char.IsAsciiDigit);
            var c = na && nb ? CompareNumbers(pa[i], pb[i]) : na ? -1 : nb ? 1 : string.CompareOrdinal(pa[i], pb[i]);
            if (c != 0)
            {
                return c;
            }
        }

        return pa.Length.CompareTo(pb.Length);
    }

    private static (string[] Core, string? Pre)? Parse(string? version)
    {
        if (version is null)
        {
            return null;
        }

        var plus = version.IndexOf('+', StringComparison.Ordinal);
        var bare = plus < 0 ? version : version[..plus];
        var dash = bare.IndexOf('-', StringComparison.Ordinal);
        var core = (dash < 0 ? bare : bare[..dash]).Split('.');
        if (core.Length != 3 || core.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit)))
        {
            return null;
        }

        return (core, dash < 0 ? null : bare[(dash + 1)..]);
    }

    // Numbers of any length, compared without overflow.
    private static int CompareNumbers(string a, string b)
    {
        var x = a.TrimStart('0');
        var y = b.TrimStart('0');
        return x.Length != y.Length ? x.Length.CompareTo(y.Length) : Math.Sign(string.CompareOrdinal(x, y));
    }
}
