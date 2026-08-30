using Corridor.Portal.Models;

namespace Corridor.Portal.Data;

public interface IPermitRepository
{
    Task<IReadOnlyList<Permit>> ListAsync(string? statusFilter, CancellationToken ct = default);

    /// <summary>Returns the stored permit, with the sequence number the repository assigned.</summary>
    Task<Permit> CreateAsync(NewPermit permit, CancellationToken ct = default);

    /// <summary>Existing permit numbers, used to compute the next IP-YYYY-NNNN sequence value.</summary>
    Task<IReadOnlyList<string>> ListPermitNumbersAsync(CancellationToken ct = default);
}

public interface IMigrationAppRepository
{
    Task<IReadOnlyList<MigrationApp>> ListAsync(CancellationToken ct = default);

    Task<MigrationApp?> GetAsync(string appKey, CancellationToken ct = default);

    Task UpdateTrustModeAsync(string appKey, TrustMode mode, string flippedBy, DateTime flippedAtUtc, CancellationToken ct = default);
}

public interface IAuditEventRepository
{
    Task RecordAsync(AuditEvent auditEvent, CancellationToken ct = default);

    Task<IReadOnlyList<AuditEvent>> ListRecentAsync(int count, CancellationToken ct = default);
}

public interface IAssignmentRepository
{
    Task<IReadOnlyList<Assignment>> ListAsync(string? inspectorUpn, CancellationToken ct = default);

    Task<Assignment?> GetAsync(int id, CancellationToken ct = default);

    Task<Assignment> SaveChecklistAsync(int id, string checklistJson, CancellationToken ct = default);
}
