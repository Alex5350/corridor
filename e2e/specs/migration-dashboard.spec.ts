import { test, expect, type APIRequestContext } from "@playwright/test";
import {
  setTrustMode,
  recentAuditEvents,
  maxAuditId,
  directoryUsers,
  setDirectoryUserActive,
} from "../lib/sql.mjs";
import { signInViaAdfs, USERS } from "../lib/portal";

/**
 * The admin's cutover tool: the migration dashboard lists all three apps, the
 * flip button walks the legacy service Adfs to Dual to Okta through the real
 * audited path, the table reflects each flip, and the audit trail records a
 * TrustModeChanged event naming the admin. The provisioning button pushes
 * idn.Users into the live SCIM endpoint and records a DirectoryProvisioned row.
 */

const SCIM_LIST_URL = "http://localhost:8080/scim/v2/Users";
const SCIM_BEARER = { Authorization: "Bearer corridor-scim-token" };

/** Lists the live okta-sim directory (also wakes its SQL store, which backfills ScimExternalId). */
async function scimDirectory(request: APIRequestContext) {
  const response = await request.get(SCIM_LIST_URL, { headers: SCIM_BEARER });
  expect(response.status()).toBe(200);
  const body = await response.json();
  const resources: Array<{ id: string; userName: string; active: boolean }> = body.Resources ?? [];
  return new Map(resources.map((resource) => [resource.userName, resource]));
}

/** Reactivates the clerk in SQL and, when needed, in the live directory (used by cleanup). */
async function reactivateClerk(request: APIRequestContext) {
  await setDirectoryUserActive(USERS.clerk, true);
  const directory = await scimDirectory(request);
  const clerk = directory.get(USERS.clerk);
  if (clerk && !clerk.active) {
    const response = await request.patch(`${SCIM_LIST_URL}/${clerk.id}`, {
      headers: { ...SCIM_BEARER, "Content-Type": "application/scim+json" },
      data: {
        schemas: ["urn:ietf:params:scim:api:messages:2.0:PatchOp"],
        Operations: [{ op: "replace", path: "active", value: true }],
      },
    });
    expect(response.status()).toBe(200);
  }
}
test.describe("Admin migration dashboard", () => {
  test.beforeAll(async () => {
    // The admin signs in via the current Adfs trust; legacy starts from the
    // seeded baseline so the walk below is deterministic.
    await setTrustMode("portal", "Adfs");
    await setTrustMode("legacy", "Adfs");
  });

  test.afterAll(async () => {
    // Restore the seeded baseline for the apps this spec flipped.
    await setTrustMode("legacy", "Adfs");
  });

  let auditIdBeforeFlips = 0;

  test("dashboard shows the three applications and flips legacy Adfs to Dual to Okta", async ({ page }) => {
    auditIdBeforeFlips = await maxAuditId();
    await signInViaAdfs(page, USERS.admin);

    // The admin-only navigation appears after an Admin sign-in. Scoped to the
    // header nav because the home card also links to the dashboard.
    await page.locator("nav.site-nav").getByRole("link", { name: "Migration" }).click();
    await expect(page.getByRole("heading", { name: "Migration dashboard" })).toBeVisible();

    const table = page.locator("table").first();
    const expectedApps = [
      { key: "portal", name: "PermitPortal" },
      { key: "legacy", name: "TraceLink" },
      { key: "spa", name: "FieldInsight" },
    ];
    for (const app of expectedApps) {
      await expect(table.locator("tbody tr", { hasText: app.key })).toContainText(app.name);
    }

    const legacyRow = table.locator("tbody tr", { hasText: "legacy" });
    await expect(legacyRow.locator(".badge")).toHaveText("Adfs");

    // Flip 1: Adfs -> Dual.
    await legacyRow.getByRole("button", { name: "Flip to Dual" }).click();
    await expect(page.locator(".notice[role='status']")).toContainText("legacy now trusts Dual.");
    await expect(legacyRow.locator(".badge")).toHaveText("Dual");
    await expect(legacyRow).toContainText(USERS.admin);

    // Flip 2: Dual -> Okta.
    await legacyRow.getByRole("button", { name: "Flip to Okta" }).click();
    await expect(page.locator(".notice[role='status']")).toContainText("legacy now trusts Okta.");
    await expect(legacyRow.locator(".badge")).toHaveText("Okta");

    // The other rows were not touched by the legacy flips.
    await expect(table.locator("tbody tr", { hasText: "portal" }).locator(".badge")).toHaveText("Adfs");
    await expect(table.locator("tbody tr", { hasText: "spa" }).locator(".badge")).toHaveText("Adfs");
  });

  test("provisioning the directory reaches the live SCIM endpoint and audits the run", async ({ page, request }) => {
    // Wake okta-sim's SQL store first: its first query backfills ScimExternalId,
    // which puts every seeded user on the update path so the run is deterministic.
    const before = await scimDirectory(request);
    expect(before.get(USERS.clerk)?.active).toBe(true);
    const sqlRows = await directoryUsers();
    expect(sqlRows.length).toBeGreaterThanOrEqual(4);
    const updatedCount = sqlRows.length - 1; // everyone but the clerk, deactivated below

    // Arrange drift: the clerk goes inactive in SQL, so this run also has a
    // deactivation to push through the bridge. The finally clause undoes the
    // drift in both stores even when an assertion fails, so later suites (the
    // dual-trust spec signs the clerk in through okta-sim) start clean.
    let restored = false;
    try {
      await setDirectoryUserActive(USERS.clerk, false);
      const auditIdBeforeProvision = await maxAuditId();

      await signInViaAdfs(page, USERS.admin);
      await page.locator("nav.site-nav").getByRole("link", { name: "Migration" }).click();
      await page.getByRole("heading", { name: "Migration dashboard" }).waitFor();
      await page.getByRole("button", { name: "Provision directory" }).click();
      await expect(page.locator(".notice[role='status']"))
        .toHaveText(`Directory provisioned into okta-sim: created 0, updated ${updatedCount}, deactivated 1.`);

      // The live directory received it: every SQL row is served back with the same
      // SCIM id, and the clerk's resource now says active=false.
      const after = await scimDirectory(request);
      for (const row of sqlRows) {
        const resource = after.get(row.upn);
        expect(resource, `SCIM resource for ${row.upn}`).toBeDefined();
        expect(resource?.id).toBe(row.scimExternalId);
      }
      expect(after.get(USERS.clerk)?.active).toBe(false);

      // One audit row per run, with the counts and the admin as actor.
      const events = (await recentAuditEvents(10))
        .filter((event) => event.id > auditIdBeforeProvision)
        .filter((event) => event.event === "DirectoryProvisioned");
      expect(events.length).toBe(1);
      expect(events[0].appKey).toBe("oktasim");
      expect(events[0].actor).toBe(USERS.admin);
      expect(events[0].detail).toBe(`created 0, updated ${updatedCount}, deactivated 1`);

      // Restore the seeded state: reactivate the clerk in SQL and push the repair
      // through the same button (the run reports no deactivations this time).
      await setDirectoryUserActive(USERS.clerk, true);
      await page.getByRole("button", { name: "Provision directory" }).click();
      await expect(page.locator(".notice[role='status']"))
        .toHaveText(`Directory provisioned into okta-sim: created 0, updated ${sqlRows.length}, deactivated 0.`);
      restored = true;
    } finally {
      if (!restored) {
        await reactivateClerk(request);
      }
    }
    const restoredDirectory = await scimDirectory(request);
    expect(restoredDirectory.get(USERS.clerk)?.active).toBe(true);
  });

  test("the audit trail records the TrustModeChanged events for legacy", async ({ page }) => {
    await signInViaAdfs(page, USERS.admin);

    // The Admin > Audit page renders the SQL-backed audit trail; the spec
    // asserts the page and, below, the exact idn.AuditEvents rows it renders,
    // written by the flips this spec drove through the dashboard buttons
    // (checked against the pre-flip audit Id so reruns cannot lean on stale
    // rows).
    await page.locator("nav.site-nav").getByRole("link", { name: "Audit" }).click();
    await expect(page.getByRole("heading", { name: "Audit trail" })).toBeVisible();
    await expect(
      page.locator("tbody tr", { hasText: "TrustModeChanged" }).first(),
    ).toBeVisible();

    const events = (await recentAuditEvents(200))
      .filter((event) => event.id > auditIdBeforeFlips);
    const legacyFlips = events.filter(
      (event) => event.appKey === "legacy" && event.event === "TrustModeChanged");
    expect(legacyFlips.length).toBe(2);
    expect(legacyFlips.map((event) => event.detail)).toEqual(["Dual -> Okta", "Adfs -> Dual"]);
    for (const flip of legacyFlips) {
      expect(flip.actor).toBe(USERS.admin);
    }
  });

  test("non-admins never see the dashboard", async ({ page }) => {
    await signInViaAdfs(page, USERS.officer);
    await expect(page.getByRole("link", { name: "Migration" })).toHaveCount(0);
    await page.goto("/Admin/Migration");
    await expect(page.getByRole("heading", { name: "Access denied" })).toBeVisible();
  });
});
