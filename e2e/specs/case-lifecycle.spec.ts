import { test, expect } from "@playwright/test";
import { setTrustMode } from "../lib/sql.mjs";
import { signInViaAdfs, USERS } from "../lib/portal";

/**
 * The officer's day: create a trace request through the portal's REST-to-SOAP
 * bridge, walk it through the legal status transitions, and prove the guarded
 * transition (Closed back to UnderReview) is refused with the message from the
 * trace.usp_UpdateStatus procedure surfaced in the page. Serial: the lifecycle
 * steps build on the case number created by the first test.
 */
test.describe.serial("trace case lifecycle on the Cases page", () => {
  test.beforeAll(async () => {
    // Runs after adfs-login.spec.ts alphabetically, but state is set explicitly
    // so this spec is safe in any order: portal signs in via adfs-sim here.
    await setTrustMode("portal", "Adfs");
  });

  let caseNumber = "";

  test("create a trace request and see the new case number", async ({ page }) => {
    await signInViaAdfs(page, USERS.officer);
    await page.getByRole("link", { name: "Cases" }).click();
    await expect(page.getByRole("heading", { name: "TraceLink trace cases" })).toBeVisible();

    await page.fill("#Create_LicenseeName", "Northgate Imports LLC");
    await page.fill("#Create_ItemDescription", "Bolt-action rifle, 7.62x51mm");
    await page.fill("#Create_Serial", "NG-2026-00417");
    await page.getByRole("button", { name: "Submit trace request" }).click();

    const status = page.locator(".notice[role='status']");
    await expect(status).toContainText(/Trace request recorded as case (TRC-\d+)\./);
    caseNumber = ((await status.textContent()) ?? "").match(/TRC-\d+/)![0];

    // The new case shows in the list as Received.
    const row = page.locator("tbody tr", { hasText: caseNumber });
    await expect(row).toBeVisible();
    await expect(row.locator(".badge")).toHaveText("Received");
    await expect(row).toContainText("Northgate Imports LLC");
  });

  test("walk Received to UnderReview to Traced, then Closed", async ({ page }) => {
    await signInViaAdfs(page, USERS.officer);
    await page.getByRole("link", { name: "Cases" }).click();

    const row = page.locator("tbody tr", { hasText: caseNumber });
    for (const nextStatus of ["UnderReview", "Traced", "Closed"]) {
      await page.fill("#StatusUpdate_CaseNumber", caseNumber);
      await page.selectOption("#StatusUpdate_NewStatus", nextStatus);
      await page.getByRole("button", { name: "Update status" }).click();
      await expect(page.locator(".notice[role='status']"))
        .toContainText(`Case ${caseNumber} moved to ${nextStatus}.`);
      await expect(row.locator(".badge")).toHaveText(nextStatus);
    }
  });

  test("the illegal Closed to UnderReview move is refused in the page", async ({ page }) => {
    await signInViaAdfs(page, USERS.officer);
    await page.getByRole("link", { name: "Cases" }).click();

    await page.fill("#StatusUpdate_CaseNumber", caseNumber);
    await page.selectOption("#StatusUpdate_NewStatus", "UnderReview");
    await page.getByRole("button", { name: "Update status" }).click();

    // The SOAP fault from the stored procedure's transition guard is surfaced
    // by the page, and the case itself is unchanged.
    await expect(page.locator(".notice[role='alert']"))
      .toContainText(`Illegal transition Closed to UnderReview for case ${caseNumber}`);
    const row = page.locator("tbody tr", { hasText: caseNumber });
    await expect(row.locator(".badge")).toHaveText("Closed");
  });

  test("wrong adfs credentials stay on the login page with the error shown", async ({ page }) => {
    await page.goto("/");
    await page.locator(".header-session a.signin").click();
    await expect(page).toHaveURL(/localhost:8090\/\?SAMLRequest=/);
    await page.fill("#login-user", USERS.officer);
    await page.fill("#login-password", "not-the-demo-password");
    await page.locator("button.btn-signin").click();
    await expect(page.locator(".alert")).toContainText("The user name or password is incorrect.");
    await expect(page).toHaveURL(/localhost:8090\//);
    await expect(page.locator("#login-user")).toHaveValue(USERS.officer);
  });

  test.afterAll(async () => {
    await setTrustMode("portal", "Adfs");
  });
});
