import { test, expect } from "@playwright/test";
import { setTrustMode, getTrustMode } from "../lib/sql.mjs";
import { signInViaOkta, signOutPortal, USERS } from "../lib/portal";

/**
 * Post-migration flow: after the cutover completes, the portal's sign-in page
 * challenges the OIDC handler against okta-sim, and the old SAML path is
 * refused. The mode flip is arranged by SQL here (the audited UI path is the
 * migration dashboard spec's job) and restored to Adfs afterwards.
 */
test.describe("portal sign-in via okta-sim (Okta mode)", () => {
  test.beforeAll(async () => {
    await setTrustMode("portal", "Okta");
  });

  test.afterAll(async () => {
    // Later specs (spa-inspector is mode-independent, but the next full run
    // starts from the seeded baseline) expect the portal back in Adfs mode.
    await setTrustMode("portal", "Adfs");
  });

  test("sign-in goes to the okta-sim login form and returns signed in", async ({ page }) => {
    await signInViaOkta(page, USERS.officer);

    await expect(page.getByRole("heading", { name: "Import permit program" })).toBeVisible();
    const facts = page.locator(".card", { hasText: "Sign-in status" }).locator("dl.facts");
    await expect(facts).toContainText(USERS.officer);
    await expect(facts).toContainText("Officer");
    await expect(facts).toContainText("okta");
  });

  test("the okta-sim login form rejects a wrong password", async ({ page }) => {
    await page.goto("/");
    await page.locator(".header-session a.signin").click();
    await expect(page).toHaveURL(/localhost:8080\/authorize/);
    await page.fill("#username", USERS.officer);
    await page.fill("#password", "still-not-the-demo-password");
    await page.getByRole("button", { name: "Sign in" }).click();
    await expect(page).toHaveURL(/localhost:8080\/authorize/);
    await expect(page.getByText("Sign-in failed: unknown user or wrong demo password.")).toBeVisible();
  });

  test("adfs-sim alone cannot mint a portal sign-in once the portal stopped asking", async ({ page }) => {
    // Walking up to the legacy provider and submitting valid credentials gets
    // nowhere: with no AuthnRequest from the portal there is nothing to answer,
    // and after cutover the portal never sends one.
    await page.goto("http://localhost:8090/");
    await page.fill("#login-user", USERS.officer);
    await page.fill("#login-password", "Demo1234!");
    await page.locator("button.btn-signin").click();
    await expect(page.locator(".alert"))
      .toContainText("The sign-in request from the application could not be read.");
    await expect(page).toHaveURL(/localhost:8090\//);
  });

  test("sign out and the trust mode is untouched by the login cycle", async ({ page }) => {
    await signInViaOkta(page, USERS.admin);
    await signOutPortal(page);

    // The okta session ends through okta-sim's logout endpoint; back on the
    // portal the browser is anonymous again and the mode never moved.
    await page.goto("/");
    await expect(page.locator(".header-session a.signin")).toBeVisible();
    await expect(page.locator(".card", { hasText: "Sign-in status" })).toContainText("Not signed in");
    expect(await getTrustMode("portal")).toBe("Okta");
  });
});
