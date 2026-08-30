import { test, expect } from "@playwright/test";
import { setTrustMode, getTrustMode } from "../lib/sql.mjs";
import {
  clickSignIn,
  expectChooser,
  expectSignedInAs,
  signOutPortal,
  USERS,
} from "../lib/portal";

/**
 * The cutover middle state: with the portal in Dual mode the login page shows
 * the provider chooser, and BOTH paths complete a real sign-in. The mode is
 * set by SQL for arrangement (the audited UI flip is proven by the migration
 * dashboard spec) and restored afterwards so later specs start from Adfs.
 */
test.describe("portal sign-in chooser (Dual mode)", () => {
  test.beforeAll(async () => {
    await setTrustMode("portal", "Dual");
  });

  test.afterAll(async () => {
    await setTrustMode("portal", "Adfs");
  });

  test("Dual mode shows the chooser with both providers", async ({ page }) => {
    await page.goto("/");
    await clickSignIn(page);
    await expect(page).toHaveURL(/localhost:5200\/Login/);
    await expectChooser(page);
    await expect(page.locator(".page-intro")).toContainText("routes sign-in by its current trust mode");
  });

  test("chooser path A: Continue with ADFS signs the user in", async ({ page }) => {
    await page.goto("/");
    await clickSignIn(page);
    await expectChooser(page);
    await page.getByRole("link", { name: "Continue with ADFS" }).click();

    await expect(page).toHaveURL(/localhost:8090\/\?SAMLRequest=/);
    await page.fill("#login-user", USERS.clerk);
    await page.fill("#login-password", "Demo1234!");
    await page.locator("button.btn-signin").click();

    await expect(page).toHaveURL(/localhost:5200\//);
    await expectSignedInAs(page, USERS.clerk);
    // SAML assertions keep working during the dual window.
    await expect(page.locator(".card", { hasText: "Sign-in status" })).toContainText("adfs");
  });

  test("chooser path B: Continue with Okta signs the user in", async ({ page }) => {
    await page.goto("/");
    await clickSignIn(page);
    await expectChooser(page);
    await page.getByRole("link", { name: "Continue with Okta" }).click();

    await expect(page).toHaveURL(/localhost:8080\/authorize/);
    await page.fill("#username", USERS.clerk);
    await page.fill("#password", "Demo1234!");
    await page.getByRole("button", { name: "Sign in" }).click();

    await expect(page).toHaveURL(/localhost:5200\//);
    await expectSignedInAs(page, USERS.clerk);
    await expect(page.locator(".card", { hasText: "Sign-in status" })).toContainText("okta");
  });

  test("sign out returns to the anonymous portal with the mode unchanged", async ({ page }) => {
    await setTrustMode("portal", "Dual");
    await page.goto("/");
    await clickSignIn(page);
    await expectChooser(page);
    await page.getByRole("link", { name: "Continue with ADFS" }).click();
    await page.fill("#login-user", USERS.clerk);
    await page.fill("#login-password", "Demo1234!");
    await page.locator("button.btn-signin").click();
    await expectSignedInAs(page, USERS.clerk);

    await signOutPortal(page);

    // A SAML session ends on the portal's own Signed out page, and signing in
    // again lands back on the chooser: Dual is still in force.
    await expect(page.getByRole("heading", { name: "Signed out" })).toBeVisible();
    await expect(page.getByText("Your portal session has ended.")).toBeVisible();
    await clickSignIn(page);
    await expectChooser(page);
    expect(await getTrustMode("portal")).toBe("Dual");
  });
});
