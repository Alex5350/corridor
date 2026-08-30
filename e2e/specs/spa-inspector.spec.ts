import { test, expect, type BrowserContext } from "@playwright/test";
import { USERS } from "../lib/portal";
import { allowOktaCrossOrigin } from "../lib/cors-shim";

const SPA_BASE = "http://localhost:5173";

/**
 * The inspector's app: the FieldInsight SPA is a public OIDC client doing the
 * authorization code flow with PKCE against okta-sim. The login gate hands the
 * provider a login_hint, the code comes back to /callback, and the portal's
 * assignment API serves the list with the resulting access token. Toggling a
 * checklist item PATCHes the portal and survives a reload.
 *
 * The okta-sim simulator sends no CORS response headers, so a browser page on
 * the SPA origin cannot fetch its discovery or token endpoints directly; the
 * context installs lib/cors-shim.ts, which adds those response headers only.
 * Every request still reaches the real okta-sim and the real portal.
 */
test.describe.serial("FieldInsight SPA as the inspector", () => {
  test.beforeEach(async ({ context }: { context: BrowserContext }) => {
    await allowOktaCrossOrigin(context);
  });

  test("PKCE sign-in from the gate lands on the assignments list", async ({ page }) => {
    await page.goto(SPA_BASE + "/");
    await expect(page.getByRole("heading", { name: "FieldInsight" })).toBeVisible();
    await expect(page.getByText("Sign in with the Okta identity provider")).toBeVisible();

    // The gate prefills the demo inspector upn as the login_hint.
    await expect(page.locator("#login-hint")).toHaveValue(USERS.inspector);
    await page.getByRole("button", { name: "Sign in with Okta" }).click();

    // okta-sim honors login_hint: the code is issued without a form stop, the
    // SPA exchanges it (PKCE verifier in sessionStorage), and the signed-in
    // header shows the inspector's display name from the ID token.
    await expect(page.getByRole("heading", { name: "Assignments" })).toBeVisible({ timeout: 30_000 });
    await expect(page.locator(".header-session .who")).toHaveText("Miguel Sandoval");

    // Seeded assignments render with progress rings and status pills.
    const cards = page.locator(".assignment-card");
    await expect(cards).toHaveCount(6);
    await expect(cards.first()).toContainText("Riverside Sporting Goods");
    await expect(cards.first().locator("svg.progress-ring")).toBeVisible();
    await expect(cards.first().locator(".badge")).toBeVisible();
  });

  test("a toggled checklist item persists across a reload", async ({ page }) => {
    await page.goto(SPA_BASE + "/");
    await expect(page.getByRole("heading", { name: "FieldInsight" })).toBeVisible();
    await expect(page.locator("#login-hint")).toHaveValue(USERS.inspector);
    await page.getByRole("button", { name: "Sign in with Okta" }).click();
    await expect(page.getByRole("heading", { name: "Assignments" })).toBeVisible({ timeout: 30_000 });

    // Open the first assignment's checklist.
    await page.locator(".assignment-card").first().getByRole("link", { name: /Open checklist/ }).click();
    await expect(page.getByRole("heading", { name: "Riverside Sporting Goods" })).toBeVisible();
    const firstItem = page.locator("#checklist-item-0");
    await expect(firstItem).not.toBeChecked();

    // Toggling saves through the portal API; the live region confirms it.
    await firstItem.check();
    await expect(page.locator(".sr-live[role='status']")).toContainText("Saved: item marked done.");

    // A reload re-reads the assignment from the API: the item stays done.
    await page.reload();
    await expect(page.getByRole("heading", { name: "Riverside Sporting Goods" })).toBeVisible();
    await expect(page.locator("#checklist-item-0")).toBeChecked();

    // Put the seed state back the same way the app does.
    await page.locator("#checklist-item-0").uncheck();
    await expect(page.locator(".sr-live[role='status']")).toContainText("Saved: item reopened.");
  });

  test("the profile card shows the raw okta token claims", async ({ page }) => {
    await page.goto(SPA_BASE + "/");
    await page.getByRole("button", { name: "Sign in with Okta" }).click();
    await expect(page.getByRole("heading", { name: "Assignments" })).toBeVisible({ timeout: 30_000 });
    await page.getByRole("link", { name: "Profile" }).click();
    await expect(page.getByRole("heading", { name: "Profile" })).toBeVisible();
    const facts = page.locator(".card", { hasText: "Signed-in inspector" }).locator("dl.facts");
    await expect(facts).toContainText(USERS.inspector);
    await expect(facts).toContainText("Inspector");
    await expect(facts).toContainText("okta-sim");
  });
});
