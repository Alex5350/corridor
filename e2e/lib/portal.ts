import { expect, type Page } from "@playwright/test";

/**
 * Browser-driven portal sign-in flows shared by the specs. Every helper ends
 * with the signed-in portal header assertion, so a broken flow fails at the
 * step that broke rather than deep inside a spec.
 */

export const DEMO_PASSWORD = "Demo1234!";

export const USERS = {
  admin: "admin@corridor.example",
  inspector: "inspector@corridor.example",
  officer: "officer@corridor.example",
  clerk: "clerk@corridor.example",
} as const;

/** The portal home page is public; sign-in always starts from the header link. */
export async function clickSignIn(page: Page) {
  await page.locator(".header-session a.signin").click();
}

/** The signed-in account shown in the portal header (the cookie principal name). */
export async function expectSignedInAs(page: Page, upn: string) {
  await expect(page.locator(".header-session .who")).toHaveText(upn);
}

/**
 * Portal (Adfs mode): the login page redirects to the adfs-sim SSO endpoint
 * with a generated AuthnRequest (which adfs-sim answers with its forms login
 * at /); submitting the form posts a signed SAML response back to the portal
 * ACS, which issues the portal cookie.
 */
export async function signInViaAdfs(page: Page, upn: string) {
  await page.goto("/");
  await clickSignIn(page);
  await expect(page).toHaveURL(/localhost:8090\/\?SAMLRequest=/);
  await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();
  await expect(page.locator(".farm-name")).toHaveText("adfs-sim.corridor.local");
  await page.fill("#login-user", upn);
  await page.fill("#login-password", DEMO_PASSWORD);
  await page.locator("button.btn-signin").click();
  await expect(page).toHaveURL(/localhost:5200\//);
  await expectSignedInAs(page, upn);
}

/**
 * Portal (Okta mode): the login page challenges the OIDC handler, which parks
 * the browser on okta-sim's authorization login form; submitting it completes
 * the code flow and lands back on the portal signed in.
 */
export async function signInViaOkta(page: Page, upn: string) {
  await page.goto("/");
  await clickSignIn(page);
  await expect(page).toHaveURL(/localhost:8080\/authorize/);
  await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();
  await page.fill("#username", upn);
  await page.fill("#password", DEMO_PASSWORD);
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page).toHaveURL(/localhost:5200\//);
  await expectSignedInAs(page, upn);
}

/** Portal (Dual mode): the chooser with both provider links. */
export async function expectChooser(page: Page) {
  await expect(page.getByRole("heading", { name: "Dual trust is active" })).toBeVisible();
  await expect(page.getByRole("link", { name: "Continue with ADFS" })).toBeVisible();
  await expect(page.getByRole("link", { name: "Continue with Okta" })).toBeVisible();
}

/**
 * Signs out and waits for the session to actually end. SAML sessions land on
 * the portal's own Signed out page; okta sessions are routed through okta-sim's
 * logout endpoint first (the portal passes an id_token_hint), so the helper
 * accepts either landing and leaves the rest to the caller.
 */
export async function signOutPortal(page: Page) {
  await page.locator(".header-session button", { hasText: "Sign out" }).click();
  await expect
    .poll(async () => page.url(), { timeout: 15_000 })
    .toMatch(/localhost:(5200\/Logout|8080\/logout)/);
}
