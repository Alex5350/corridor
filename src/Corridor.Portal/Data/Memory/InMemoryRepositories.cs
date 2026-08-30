using Corridor.Portal.Models;
using Corridor.Portal.Services;

namespace Corridor.Portal.Data.Memory;

/// <summary>Backs the portal when SQL Server is not reachable: unit tests and demo boots. Synthetic data only.</summary>
public sealed class InMemoryPermitRepository : IPermitRepository
{
    private readonly object _gate = new();
    private readonly List<Permit> _permits;
    private int _nextId;

    public InMemoryPermitRepository(IEnumerable<Permit>? seed = null)
    {
        var baseTime = new DateTime(2026, 8, 25, 14, 0, 0, DateTimeKind.Utc);
        _permits = seed is null
            ?
            [
                new Permit(1, "IP-2026-0301", "Harborview Collectibles", "Merrin M-12 shotgun 12ga", 24, "Retail stock replenishment", "Approved", baseTime, "clerk@corridor.example"),
                new Permit(2, "IP-2026-0302", "Fieldstone Outfitters", "Kalvin KB-7 .22 bolt rifle", 60, "Seasonal hunting inventory", "UnderReview", baseTime.AddHours(-6), "clerk@corridor.example"),
                new Permit(3, "IP-2026-0303", "Summit Range Supply", "Ardent AR-22 rimfire", 120, "Range rental fleet", "UnderReview", baseTime.AddHours(-20), "clerk@corridor.example"),
                new Permit(4, "IP-2026-0304", "Quarry Ridge Distributors", "Halden H-9 pistol 9mm", 200, "Wholesale distribution", "Approved", baseTime.AddHours(-30), "clerk@corridor.example"),
                new Permit(5, "IP-2026-0305", "Old Mill Firearms", "Vernley single-shot .410", 30, "Collector consignment", "Rejected", baseTime.AddHours(-49), "clerk@corridor.example"),
                new Permit(6, "IP-2026-0306", "Cedar Valley Pawn", "Orlan bolt rifle .308", 15, "Store inventory", "UnderReview", baseTime.AddHours(-70), "clerk@corridor.example")
            ]
            : [.. seed];
        _nextId = _permits.Count == 0 ? 0 : _permits.Max(p => p.Id);
    }

    public Task<IReadOnlyList<Permit>> ListAsync(string? statusFilter, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IEnumerable<Permit> query = _permits;
            if (!string.IsNullOrEmpty(statusFilter))
            {
                query = query.Where(p => p.Status == statusFilter);
            }
            IReadOnlyList<Permit> result = query.OrderByDescending(p => p.SubmittedAt).ToList();
            return Task.FromResult(result);
        }
    }

    public Task<Permit> CreateAsync(NewPermit permit, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var permitNumber = PermitNumberSequence.Next(_permits.Select(p => p.PermitNumber), DateTime.UtcNow.Year);            var stored = new Permit(++_nextId, permitNumber, permit.LicenseeName, permit.ItemDescription,
                permit.Quantity, permit.Purpose, "UnderReview", DateTime.UtcNow, permit.SubmittedBy);
            _permits.Add(stored);
            return Task.FromResult(stored);
        }
    }

    public Task<IReadOnlyList<string>> ListPermitNumbersAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<string> numbers = _permits.Select(p => p.PermitNumber).ToList();
            return Task.FromResult(numbers);
        }
    }
}

public sealed class InMemoryMigrationAppRepository : IMigrationAppRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<string, MigrationApp> _apps;

    public InMemoryMigrationAppRepository(IEnumerable<MigrationApp>? seed = null)
    {
        _apps = (seed ?? DefaultApps()).ToDictionary(a => a.AppKey, a => a);
    }

    public static IEnumerable<MigrationApp> DefaultApps() =>
    [
        new MigrationApp("legacy", "TraceLink (SOAP case service)", TrustMode.Adfs, null, null),
        new MigrationApp("portal", "PermitPortal (OIDC web app)", TrustMode.Adfs, null, null),
        new MigrationApp("spa", "FieldInsight (inspector SPA)", TrustMode.Adfs, null, null)
    ];

    public Task<IReadOnlyList<MigrationApp>> ListAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<MigrationApp> apps = _apps.Values.OrderBy(a => a.AppKey, StringComparer.Ordinal).ToList();
            return Task.FromResult(apps);
        }
    }

    public Task<MigrationApp?> GetAsync(string appKey, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_apps.TryGetValue(appKey, out var app) ? app : null);
        }
    }

    public Task UpdateTrustModeAsync(string appKey, TrustMode mode, string flippedBy, DateTime flippedAtUtc, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_apps.TryGetValue(appKey, out var app))
            {
                throw new InvalidOperationException($"Unknown application key {appKey}.");
            }
            _apps[appKey] = app with { TrustMode = mode, LastFlippedAt = flippedAtUtc, FlippedBy = flippedBy };
        }
        return Task.CompletedTask;
    }
}

public sealed class InMemoryAuditEventRepository : IAuditEventRepository
{
    private readonly object _gate = new();
    private readonly List<AuditEvent> _events = [];
    private long _nextId;

    public Task RecordAsync(AuditEvent auditEvent, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _events.Add(auditEvent with { Id = ++_nextId, At = DateTime.UtcNow });
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEvent>> ListRecentAsync(int count, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<AuditEvent> events = [.. _events.OrderByDescending(e => e.Id).Take(count)];
            return Task.FromResult(events);
        }
    }
}

public sealed class InMemoryAssignmentRepository : IAssignmentRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<int, Assignment> _assignments;

    public InMemoryAssignmentRepository(IEnumerable<Assignment>? seed = null)
    {
        var dueBase = new DateTime(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc);
        _assignments = (seed ?? DefaultAssignments(dueBase)).ToDictionary(a => a.Id, a => a);
    }

    public static IEnumerable<Assignment> DefaultAssignments(DateTime dueBase) =>
    [
        new Assignment(1, "inspector@corridor.example", "Riverside Sporting Goods", "Bound-book reconciliation and inventory sampling", dueBase,
            "[{\"item\":\"Review acquisition log\",\"done\":false},{\"item\":\"Sample 10 percent of serials\",\"done\":false},{\"item\":\"Verify permit records\",\"done\":false}]"),
        new Assignment(2, "inspector@corridor.example", "Summit Range Supply", "Rental fleet maintenance and disposition records", dueBase.AddDays(5),
            "[{\"item\":\"Check rental logs\",\"done\":false},{\"item\":\"Confirm transfer paperwork\",\"done\":false}]"),
        new Assignment(3, "inspector@corridor.example", "Quarry Ridge Distributors", "Wholesale shipping documentation review", dueBase.AddDays(11),
            "[{\"item\":\"Audit outbound manifests\",\"done\":false},{\"item\":\"Spot-check three shipments\",\"done\":false},{\"item\":\"Interview records custodian\",\"done\":false}]")
    ];

    public Task<IReadOnlyList<Assignment>> ListAsync(string? inspectorUpn, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IEnumerable<Assignment> query = _assignments.Values;
            if (!string.IsNullOrEmpty(inspectorUpn))
            {
                query = query.Where(a => a.InspectorUpn == inspectorUpn);
            }
            IReadOnlyList<Assignment> result = query.OrderBy(a => a.DueAt).ToList();
            return Task.FromResult(result);
        }
    }

    public Task<Assignment?> GetAsync(int id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_assignments.TryGetValue(id, out var assignment) ? assignment : null);
        }
    }

    public Task<Assignment> SaveChecklistAsync(int id, string checklistJson, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_assignments.TryGetValue(id, out var assignment))
            {
                throw new InvalidOperationException($"Assignment {id} disappeared during checklist save.");
            }
            var updated = assignment with { ChecklistJson = checklistJson };
            _assignments[id] = updated;
            return Task.FromResult(updated);
        }
    }
}
