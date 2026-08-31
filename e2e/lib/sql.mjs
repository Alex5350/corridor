/**
 * Direct SQL access for setup and assertions, the TypeScript mirror of the
 * integration suite's Infrastructure/Sql.cs: trust-mode flips for arranging
 * specs, plus the idempotent db/sql bootstrap (azure-sql-edge ships no sqlcmd,
 * so the GO-separated batches are executed over TDS, exactly like the fixture).
 */

import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import mssql from "mssql";

const here = path.dirname(fileURLToPath(import.meta.url));

const BASE_CONFIG = {
  server: "localhost",
  port: 1433,
  user: "sa",
  password: "CorridorDev1!",
  options: {
    encrypt: true,
    trustServerCertificate: true,
  },
  connectionTimeout: 8000,
  requestTimeout: 120_000,
};

/** Shared pool for reads and writes; the database exists once the scripts ran. */
const pools = new Map();

async function getPool(database = "Corridor") {
  if (!pools.has(database)) {
    const pool = new mssql.ConnectionPool({
      ...BASE_CONFIG,
      database,
      pool: { max: 3, min: 0, idleTimeoutMillis: 15_000 },
    });
    const promise = pool.connect().then(() => pool);
    pools.set(database, { promise });
    // A later failure (server stopped under us) must not pin a broken promise.
    promise.catch(() => pools.delete(database));
  }
  return pools.get(database).promise;
}

/** Retries until a real login answers, or throws. Used for db readiness. */
export async function waitForSqlServer(timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  let lastError;
  while (Date.now() < deadline) {
    try {
      const pool = await getPool("master");
      await pool.request().query("SELECT 1");
      return;
    } catch (error) {
      lastError = error;
      pools.delete("master");
      await new Promise((resolve) => setTimeout(resolve, 500));
    }
  }
  throw new Error(`SQL Server never answered a login: ${lastError?.message ?? lastError}`);
}

/**
 * Applies db/sql/001_schemas.sql, 002_trace_procs.sql, and seed/003_seed.sql in
 * order. Splits batches on GO lines like the fixture's GoSeparator and runs
 * them all on ONE pinned connection (pool max 1): the scripts switch to the
 * Corridor database with a USE statement, which must persist across batches.
 */
export async function applySqlScripts() {
  const repoRoot = path.resolve(here, "..", "..");
  const scripts = [
    path.join(repoRoot, "db", "sql", "001_schemas.sql"),
    path.join(repoRoot, "db", "sql", "002_trace_procs.sql"),
    path.join(repoRoot, "db", "sql", "seed", "003_seed.sql"),
  ];
  const pool = new mssql.ConnectionPool({
    ...BASE_CONFIG,
    database: "master",
    pool: { max: 1, min: 0 },
  });
  await pool.connect();
  try {
    for (const script of scripts) {
      const text = readFileSync(script, "utf8");
      const batches = text
        .split(/^GO\s*$/im)
        .map((batch) => batch.trim())
        .filter((batch) => batch.length > 0);
      for (const batch of batches) {
        await pool.request().batch(batch);
      }
    }
  } finally {
    await pool.close();
  }
}

export async function closePool() {
  for (const { promise } of pools.values()) {
    try {
      const pool = await promise;
      await pool.close();
    } catch {
      // Best effort only.
    }
  }
  pools.clear();
}

async function scalar(sqlText, params) {
  const pool = await getPool();
  const request = pool.request();
  for (const [name, value] of Object.entries(params ?? {})) {
    request.input(name, mssql.NVarChar, value);
  }
  const result = await request.query(sqlText);
  return result.recordset[0]?.result ?? null;
}

/** Reads an app row's current TrustMode (portal, legacy, or spa). */
export async function getTrustMode(appKey) {
  return scalar("SELECT TrustMode AS result FROM idn.MigrationApps WHERE AppKey = @appKey", { appKey });
}

/**
 * Setup-only trust-mode write: a bare UPDATE, deliberately without an audit
 * row (the migration-dashboard spec proves the audited UI path). This mirrors
 * Sql.SetTrustModeAsync in the integration suite.
 */
export async function setTrustMode(appKey, mode) {
  const pool = await getPool();
  const request = pool.request();
  request.input("appKey", mssql.NVarChar, appKey);
  request.input("mode", mssql.NVarChar, mode);
  await request.query("UPDATE idn.MigrationApps SET TrustMode = @mode WHERE AppKey = @appKey");
}

/** Returns the seeded baseline (all apps Adfs), used by suite teardown. */
export async function resetTrustModesToAdfs() {
  await setTrustMode("portal", "Adfs");
  await setTrustMode("legacy", "Adfs");
  await setTrustMode("spa", "Adfs");
}

/** Runs one administrative statement (no parameters) against the Corridor database. */
async function runSql(sqlText) {
  const pool = await getPool();
  await pool.request().query(sqlText);
}

/** Clears the audit trail so captures show only the events this run drives. */
export async function resetAuditEvents() {
  await runSql("DELETE FROM idn.AuditEvents");
}

/** Restores the seeded checklist state (every item open) for clean captures. */
export async function resetAssignmentChecklists() {
  const pool = await getPool();
  await pool.request().query(
    "UPDATE idn.Assignments SET ChecklistJson = REPLACE(ChecklistJson, N'\"done\":true', N'\"done\":false')");
}

/** Newest-first audit rows for assertions (Id included for delta checks). */
export async function recentAuditEvents(limit = 10) {
  const pool = await getPool();
  const result = await pool.request()
    .input("limit", mssql.Int, limit)
    .query(
      "SELECT TOP (@limit) Id, At, Actor, AppKey, Event, Detail FROM idn.AuditEvents ORDER BY At DESC, Id DESC");
  return result.recordset.map((row) => ({
    id: Number(row.Id),
    at: row.At instanceof Date ? row.At.toISOString() : String(row.At),
    actor: row.Actor,
    appKey: row.AppKey,
    event: row.Event,
    detail: row.Detail,
  }));
}

/** The highest audit Id right now, for proving that new rows were written. */
export async function maxAuditId() {
  return (await scalar("SELECT ISNULL(MAX(Id), 0) AS result FROM idn.AuditEvents", {})) ?? 0;
}

/** Directory rows as the provisioning dashboard reads them (idn.Users). */
export async function directoryUsers() {
  const pool = await getPool();
  const result = await pool.request()
    .query("SELECT Upn, DisplayName, Role, ScimExternalId, Active FROM idn.Users ORDER BY Upn");
  return result.recordset.map((row) => ({
    upn: row.Upn,
    displayName: row.DisplayName,
    role: row.Role,
    scimExternalId: row.ScimExternalId,
    active: Boolean(row.Active),
  }));
}

/** Setup-only directory write: flips the Active flag without touching the audit trail. */
export async function setDirectoryUserActive(upn, active) {
  const pool = await getPool();
  const request = pool.request();
  request.input("upn", mssql.NVarChar, upn);
  request.input("active", mssql.Bit, active);
  await request.query("UPDATE idn.Users SET Active = @active WHERE Upn = @upn");
}
