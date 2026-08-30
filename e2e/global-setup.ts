import { bootStack } from "./lib/stack.mjs";
import { waitForSqlServer, closePool } from "./lib/sql.mjs";

/**
 * Boots the Corridor stack before the first spec: database (reused when a SQL
 * Server already answers on 1433, otherwise the compose db service), the four
 * .NET services, and the SPA dev server. Anything already healthy on a contract
 * port is reused and reported; teardown only stops what this run started.
 */
export default async function globalSetup() {
  const state = await bootStack();
  if (state.reused.length > 0) {
    console.log(`[global-setup] reused existing listeners: ${state.reused.join(", ")}`);
  }

  // Guard: the specs drive the login flows by trust mode, so the database must
  // stay reachable for the whole run, not only during boot.
  await waitForSqlServer(5000);
  await closePool();

  const started = state.started.map((entry) => `${entry.label}:${entry.port}`).join(", ");
  console.log(`[global-setup] stack ready (started this run: ${started || "nothing"})`);
}
