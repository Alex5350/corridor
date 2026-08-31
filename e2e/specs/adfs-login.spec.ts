import { test, expect } from "@playwright/test";
import { setTrustMode } from "../lib/sql.mjs";
import { signInViaAdfs, USERS } from "../lib/portal";

/**
 * Baseline pre-migration flow: with the portal in Adfs mode, signing in lands
 * on the adfs-sim forms-style login page, and submitting the demo user's
 * credentials completes the SAML POST profile back into the portal.
 */
test.describe("portal sign-in via adfs-sim (Adfs mode)", () => {
  test.beforeAll(async () => {
    // Defensive: this spec is the story start, so the portal must trust Adfs only.
    await setTrustMode("portal", "Adfs");
  });

  test("sign-in redirects to adfs-sim and returns signed in as the demo user", async ({ page }) => {
    await signInViaAdfs(page, USERS.officer);

    // The header carries the signed-in account, and the home card records
    // which identity provider issued the identity.
    await expect(page.getByRole("heading", { name: "Import permit program" })).toBeVisible();
    const facts = page.locator(".card", { hasText: "Sign-in status" }).locator("dl.facts");
    await expect(facts).toContainText("Signed in as");
    await expect(facts).toContainText(USERS.officer);
    await expect(facts).toContainText("Officer");
    await expect(facts).toContainText("adfs");

    // The persistent header chip names the issuing provider on every page.
    const badge = page.locator(".header-session .idp-badge");
    await expect(badge).toHaveText("adfs");
    await expect(badge).toHaveAttribute("title", "Session issued by");
  });

  test("the adfs-sim login page is the classic on-prem form", async ({ page }) => {
    // Passing through the portal proves the redirect chain, not just the page.
    await page.goto("/");
    await page.locator(".header-session a.signin").click();
    await expect(page).toHaveURL(/localhost:8090\/\?SAMLRequest=/);
    await expect(page.locator(".farm-name")).toHaveText("adfs-sim.corridor.local");
    await expect(page.locator(".intro")).toContainText("Use your Corridor directory account.");
    await expect(page.locator("label[for='login-user']")).toHaveText("User name");
    await expect(page.locator("label[for='login-password']")).toHaveText("Password");
    // The SAML request travels as a hidden field on the form.
    await expect(page.locator("input[name='SAMLRequest']")).toHaveValue(/.+/);
  });
});
