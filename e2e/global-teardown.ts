import { readState, teardownStack, STATE_PATH } from "./lib/stack.mjs";
import { resetTrustModesToAdfs, closePool } from "./lib/sql.mjs";

/**
 * Tears the stack down after the last spec: kills only the process groups this
 * run started, stops the db container when this run started it, and restores
 * the seeded trust state (every app Adfs) so the next run starts clean.
 * Reused listeners (a developer's own dev-up stack, for example) are left alone.
 */
export default async function globalTeardown() {
  try {
    const state = readState();

    try {
      await resetTrustModesToAdfs();
      console.log("[global-teardown] trust modes restored to the seeded baseline (all Adfs)");
    } catch (error) {
      console.log(`[global-teardown] could not restore trust modes: ${error?.message ?? error}`);
    }
    await closePool();

    await teardownStack(state);
    console.log("[global-teardown] done");
  } catch (error) {
    console.error(`[global-teardown] state file problem (${STATE_PATH}): ${error?.message ?? error}`);
  }
}
