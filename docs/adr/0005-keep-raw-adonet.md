# ADR 0005: keep the raw ADO.NET data layer untouched

Status: Accepted

## Context

TraceLink's data access is raw ADO.NET: `SqlConnection`, `SqlCommand`, `SqlParameter`,
`SqlDataReader`, talking only to stored procedures (`db/sql/002_trace_procs.sql`). In
real estates this layer is old, scary-looking, and the first thing an eager modernization
wants to rewrite. But this migration is about identity, not data access. Rewriting the
data layer at the same time doubles the blast radius of every cutover step and
invalidates the regression baseline: if a trace case query changes behavior, nobody can
tell whether the identity work or the ORM rewrite caused it. The temptation was real
(EF Core sits one NuGet away); the discipline is the point.

## Decision

The identity migration does not touch the data layer at all:

- `TraceCaseRepository` (`src/Corridor.Legacy/DataAccess/TraceCaseRepository.cs`) keeps
  calling `trace.usp_SearchCases`, `trace.usp_GetCase`, `trace.usp_CreateTraceRequest`,
  and `trace.usp_UpdateStatus` exactly as before, through `SqlParameter` parameters only.
- Business invariants stay in SQL where they already lived: `usp_UpdateStatus` enforces
  the legal status transitions (Received -> UnderReview -> Traced -> Closed, with
  Rejected exits from Received and UnderReview) and raises error state 40001 on an
  illegal move, which `SqlFaultMapper` translates to a `cor:` SOAP fault.
- The only schema additions are new objects in a separate `idn` schema
  (`MigrationApps`, `AuditEvents`, `Assignments`, `Users`, in `db/sql/001_schemas.sql`);
  the `trace` and `perm` schemas gain nothing but seed rows.
- Unit tests keep this honest without a database: `IDbConnectionFactory` is the seam test
  doubles replace (`tests/Corridor.Legacy.Tests/TestDoubles/`, including
  `SqlExceptionFactory` for the fault-mapping paths).

## Consequences

- The regression suite compares identical SQL behavior before and after each phase; any
  behavioral delta is attributable to identity work by construction.
- The demo shows the realistic version of this migration: the scary legacy asset crosses
  to modern identity without anyone opening its data layer, which is precisely the
  scenario agencies ask about.
- The audit-grade rulebook (transition guards) stays in stored procedures, where change
  control for it already lives; `StatusTransitionTests` pin the behavior.
- The cost is living with ADO.NET verbosity and hand-rolled mapping
  (`TraceCaseMapper.cs`, `DbParameterExtensions.cs`); that is the point, it is the
  preserved legacy.
