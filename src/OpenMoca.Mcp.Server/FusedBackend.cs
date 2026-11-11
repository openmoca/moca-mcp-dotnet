using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Search;

namespace OpenMoca.Mcp;

/// <summary>
/// Dense searches over packages indexed with different models, fused by rank (reciprocal rank fusion, k = 60, as
/// hybrid search fuses): scores from different models cannot be compared, ranks can.
/// </summary>
internal sealed class FusedBackend(IReadOnlyList<ISearchBackend> backends) : ISearchBackend
{
    private const double K = 60;

    public BackendFeatures Features => BackendFeatures.Dense | BackendFeatures.FilterPushdown;

    public IReadOnlyList<Diagnostic> Diagnostics => [.. backends.SelectMany(b => b.Diagnostics)];

    public async ValueTask<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit, SearchFilters filters, CancellationToken cancellationToken)
    {
        var scores = new Dictionary<string, (SearchHit Hit, double Score)>(StringComparer.Ordinal);
        foreach (var backend in backends)
        {
            var hits = await backend.SearchAsync(query, limit, filters, cancellationToken).ConfigureAwait(false);
            for (var rank = 0; rank < hits.Count; rank++)
            {
                var hit = hits[rank];
                var key = $"{hit.Digest} {hit.Node} {hit.Locale} {hit.Span?.Start}";
                var add = 1 / (K + rank + 1);
                scores[key] = scores.TryGetValue(key, out var seen) ? (seen.Hit, seen.Score + add) : (hit, add);
            }
        }

        return [.. scores.Values
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Hit.Digest, StringComparer.Ordinal)
            .ThenBy(x => x.Hit.Node, StringComparer.Ordinal)
            .Take(limit)
            .Select(x => x.Hit with { Score = Math.Round(x.Score * 1e4, MidpointRounding.AwayFromZero) / 1e4 })];
    }
}
