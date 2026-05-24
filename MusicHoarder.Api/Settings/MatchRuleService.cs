using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Settings;

/// <summary>An enabled rule with its template precompiled, ready for the provider to apply.</summary>
public sealed record EnabledMatchRule(int Id, string Name, MatchRuleSourceField SourceField, CompiledMatchRule Compiled);

/// <summary>Create/update payload for a match rule (validation lives at the endpoint via <see cref="MatchRulePattern"/>).</summary>
public sealed record MatchRuleInput(string Name, string Pattern, MatchRuleSourceField SourceField, bool Enabled, int Priority);

/// <summary>
/// Runtime CRUD over <see cref="MetadataMatchRule"/> with an in-memory cache of the enabled,
/// precompiled rules (ordered by priority) for the hot enrichment path. Mutations invalidate the
/// cache so the next enrichment cycle picks them up — mirroring <see cref="RuntimeSettingsService"/>.
/// </summary>
public interface IMatchRuleService
{
    /// <summary>Cheap, sync gate for the provider's <c>CanHandle</c>: optimistic true until the cache is known-empty.</summary>
    bool HasEnabledRules { get; }

    Task<IReadOnlyList<EnabledMatchRule>> GetEnabledAsync(CancellationToken ct = default);
    Task<IReadOnlyList<MetadataMatchRule>> ListAsync(CancellationToken ct = default);
    Task<MetadataMatchRule> CreateAsync(MatchRuleInput input, CancellationToken ct = default);
    Task<MetadataMatchRule?> UpdateAsync(int id, MatchRuleInput input, CancellationToken ct = default);
    Task<bool> DeleteAsync(int id, CancellationToken ct = default);
}

public sealed class MatchRuleService : IMatchRuleService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MatchRuleService> _logger;

    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private volatile IReadOnlyList<EnabledMatchRule>? _cache;
    private volatile bool _warmed;

    public MatchRuleService(IServiceScopeFactory scopeFactory, ILogger<MatchRuleService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public bool HasEnabledRules => _cache is { Count: > 0 } || !_warmed;

    public async Task<IReadOnlyList<EnabledMatchRule>> GetEnabledAsync(CancellationToken ct = default)
    {
        if (_cache is { } cached)
            return cached;

        await _cacheLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache is { } again)
                return again;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
            var rows = await db.MetadataMatchRules.AsNoTracking()
                .Where(r => r.Enabled)
                .OrderBy(r => r.Priority).ThenBy(r => r.Id)
                .ToListAsync(ct).ConfigureAwait(false);

            var compiled = new List<EnabledMatchRule>(rows.Count);
            foreach (var row in rows)
            {
                if (MatchRulePattern.TryCompile(row.Pattern, out var c, out var error))
                    compiled.Add(new EnabledMatchRule(row.Id, row.Name, row.SourceField, c!));
                else
                    _logger.LogWarning("Skipping match rule {RuleId} '{Name}': invalid pattern ({Error})", row.Id, row.Name, error);
            }

            _cache = compiled;
            _warmed = true;
            return compiled;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public async Task<IReadOnlyList<MetadataMatchRule>> ListAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        return await db.MetadataMatchRules.AsNoTracking()
            .OrderBy(r => r.Priority).ThenBy(r => r.Id)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<MetadataMatchRule> CreateAsync(MatchRuleInput input, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var now = DateTime.UtcNow;
        var rule = new MetadataMatchRule
        {
            Name = input.Name.Trim(),
            Pattern = input.Pattern.Trim(),
            SourceField = input.SourceField,
            Enabled = input.Enabled,
            Priority = input.Priority,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.MetadataMatchRules.Add(rule);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        Invalidate();
        return rule;
    }

    public async Task<MetadataMatchRule?> UpdateAsync(int id, MatchRuleInput input, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var rule = await db.MetadataMatchRules.FirstOrDefaultAsync(r => r.Id == id, ct).ConfigureAwait(false);
        if (rule is null)
            return null;

        rule.Name = input.Name.Trim();
        rule.Pattern = input.Pattern.Trim();
        rule.SourceField = input.SourceField;
        rule.Enabled = input.Enabled;
        rule.Priority = input.Priority;
        rule.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        Invalidate();
        return rule;
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var rule = await db.MetadataMatchRules.FirstOrDefaultAsync(r => r.Id == id, ct).ConfigureAwait(false);
        if (rule is null)
            return false;

        db.MetadataMatchRules.Remove(rule);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        Invalidate();
        return true;
    }

    private void Invalidate()
    {
        _cache = null;
        _warmed = false;
    }
}
