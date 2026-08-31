/**
 * Marketing screenshot capture for the README: real Chromium (Playwright),
 * viewport 1366x900 at deviceScaleFactor 2, against the real Corridor stack
 * booted by the same library the e2e suite uses (see lib/stack.mjs). Seeded
 * data is reset first so every capture shows the clean demo state.
 *
 * Outputs docs/screenshots/shot-*.png. The migration dashboard capture is a
 * Chromium render of two real captures stacked (the dashboard page above the
 * audit trail page): the portal is restarted in in-memory mode for that one
 * shot so the audit trail starts empty and shows only the mid-cutover events
 * driven through the real flip buttons.
 */

import { mkdirSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright";
import {
  REPO_ROOT,
  bootStack,
  teardownStack,
  spawnGroup,
  killGroup,
  dotnetHealthy,
  portFree,
} from "../lib/stack.mjs";
import {
  setTrustMode,
  resetTrustModesToAdfs,
  resetAssignmentChecklists,
  closePool,
} from "../lib/sql.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
const OUT_DIR = path.resolve(here, "..", "..", "docs", "screenshots");
const VIEWPORT = { width: 1366, height: 900 };
const DEMO_PASSWORD = "Demo1234!";
const OFFICER = "officer@corridor.example";
const ADMIN = "admin@corridor.example";
const INSPECTOR = "inspector@corridor.example";

function newContext(browser) {
  return browser.newContext({
    viewport: VIEWPORT,
    deviceScaleFactor: 2,
  });
}

async function shoot(page, name) {
  const file = path.join(OUT_DIR, name);
  await page.screenshot({ path: file, fullPage: true });
  console.log(`[shots] wrote ${name}`);
  return file;
}

/** Signs in to the portal through whatever page the current mode routes to. */
async function portalSignIn(page, upn) {
  await page.goto("http://localhost:5200/");
  await page.locator(".header-session a.signin").click();
  if (page.url().includes(":8090")) {
    await page.fill("#login-user", upn);
    await page.fill("#login-password", DEMO_PASSWORD);
    await page.locator("button.btn-signin").click();
  } else {
    await page.fill("#username", upn);
    await page.fill("#password", DEMO_PASSWORD);
    await page.getByRole("button", { name: "Sign in" }).click();
  }
  await page.locator(".header-session .who").waitFor();
}

/** Restarts the portal in in-memory mode on the same port (audit page works). */
async function swapPortalToInMemory(state) {
  const entry = state.started.find((candidate) => candidate.label === "Corridor.Portal");
  if (entry?.pid) {
    console.log("[shots] stopping the SQL-backed portal for the in-memory capture");
    await killGroup(entry.pid);
    state.started = state.started.filter((candidate) => candidate !== entry);
  }
  const projectPath = path.join(REPO_ROOT, "src", "Corridor.Portal");
  const logPath = path.join(state.logDir, "Corridor.Portal-in-memory-5200.log");
  const child = spawnGroup(
    "dotnet",
    ["run", "--project", projectPath, "--no-launch-profile"],
    {
      cwd: projectPath,
      env: {
        ...process.env,
        ASPNETCORE_ENVIRONMENT: "Development",
        ASPNETCORE_URLS: "http://localhost:5200",
        Data__UseInMemory: "true",
      },
    },
    logPath,
  );
  state.started.push({ label: "Corridor.Portal (in-memory)", kind: "dotnet", pid: child.pid, port: 5200 });
  const deadline = Date.now() + 240_000;
  while (Date.now() < deadline && !(await dotnetHealthy(5200))) {
    await new Promise((resolve) => setTimeout(resolve, 750));
  }
  if (!(await dotnetHealthy(5200))) {
    throw new Error("The in-memory portal did not become healthy in time.");
  }
  console.log("[shots] in-memory portal ready on 5200");
}

async function main() {
  mkdirSync(OUT_DIR, { recursive: true });
  const state = await bootStack();
  const browser = await chromium.launch();
  try {
    // Clean seeded state for every capture.
    await resetTrustModesToAdfs();
    await resetAssignmentChecklists();

    // 1. adfs-sim login page, reached through the portal's Adfs-mode redirect.
    await setTrustMode("portal", "Adfs");
    {
      const context = await newContext(browser);
      const page = await context.newPage();
      await page.goto("http://localhost:5200/");
      await page.locator(".header-session a.signin").click();
      await page.locator("#login-user").waitFor();
      await page.getByRole("heading", { name: "Sign in" }).waitFor();
      await shoot(page, "shot-adfs-login.png");
      await context.close();
    }

    // 2. okta-sim admin console: directory users plus registered apps.
    {
      const context = await newContext(browser);
      const page = await context.newPage();
      await page.goto("http://localhost:8080/");
      await page.getByRole("heading", { name: "Corridor Okta simulation: admin console" }).waitFor();
      await page.getByText("Directory: users").waitFor();
      await page.getByText("Applications").waitFor();
      await shoot(page, "shot-okta-admin.png");
      await context.close();
    }

    // 3. Portal home signed in as the officer, post-migration (Okta mode).
    await setTrustMode("portal", "Okta");
    const officerContext = await newContext(browser);
    {
      const page = await officerContext.newPage();
      await portalSignIn(page, OFFICER);
      await page.getByRole("heading", { name: "Import permit program" }).waitFor();
      await page.locator(".card", { hasText: "Sign-in status" }).getByText("Identity provider").waitFor();
      await shoot(page, "shot-portal-home.png");
    }

    // 4. Permits: apply form, status filter, badges, seeded records.
    {
      const page = await officerContext.newPage();
      await page.goto("http://localhost:5200/");
      await page.locator("nav.site-nav").getByRole("link", { name: "Permits" }).click();
      await page.getByRole("heading", { name: "Import permit applications" }).waitFor();
      await page.getByRole("button", { name: "Submit application" }).waitFor();
      await page.locator("#statusFilter").waitFor();
      await page.locator("tbody .badge").first().waitFor();
      await shoot(page, "shot-permits.png");
    }
    await officerContext.close();

    // 5. SPA assignments as the inspector (fresh checklists). okta-sim's OIDC
    // endpoints carry the real "spa" CORS policy, so the browser flow needs
    // no shim here either.
    {
      const context = await newContext(browser);
      const page = await context.newPage();
      await page.goto("http://localhost:5173/");
      await page.locator("#login-hint").waitFor();
      await page.getByRole("button", { name: "Sign in with Okta" }).click();
      await page.getByRole("heading", { name: "Assignments" }).waitFor({ timeout: 30_000 });
      await page.locator(".assignment-card").nth(5).waitFor();
      // Cold-boot guard: Vite applies the stylesheet a tick after first render, so
      // waiting for the cards alone can capture an unstyled page. Require the CSS
      // to have actually landed (a style tag exists and the body is painted).
      await page.waitForFunction(
        () =>
          document.styleSheets.length > 0 &&
          getComputedStyle(document.body).backgroundColor !== "rgba(0, 0, 0, 0)",
      );
      await shoot(page, "shot-spa-assignments.png");
      await context.close();
    }

    // 6. Migration dashboard mid-cutover with the audit trail below. The
    // deliberate state: legacy flipped to Okta, portal to Dual, spa left Adfs.
    await swapPortalToInMemory(state);
    {
      const context = await newContext(browser);
      const page = await context.newPage();
      await portalSignIn(page, ADMIN);
      await page.locator("nav.site-nav").getByRole("link", { name: "Migration" }).click();
      await page.getByRole("heading", { name: "Migration dashboard" }).waitFor();

      const legacyRow = page.locator("tbody tr", { hasText: "legacy" });
      await legacyRow.getByRole("button", { name: "Flip to Dual" }).click();
      await legacyRow.locator(".badge").getByText("Dual").waitFor();
      await legacyRow.getByRole("button", { name: "Flip to Okta" }).click();
      await legacyRow.locator(".badge").getByText("Okta").waitFor();
      const portalRow = page.locator("tbody tr", { hasText: "portal" });
      await portalRow.getByRole("button", { name: "Flip to Dual" }).click();
      await portalRow.locator(".badge").getByText("Dual").waitFor();
      await page.locator("tbody tr", { hasText: "spa" }).locator(".badge").getByText("Adfs").waitFor();

      const dashboardPng = await page.screenshot({ fullPage: true });

      await page.locator("nav.site-nav").getByRole("link", { name: "Audit" }).click();
      await page.getByRole("heading", { name: "Audit trail" }).waitFor();
      await page.locator("tbody tr", { hasText: "TrustModeChanged" }).first().waitFor();
      const auditPng = await page.screenshot({ fullPage: true });

      // One Chromium render of the two captures: dashboard above, audit below.
      const composite = await context.newPage();
      await composite.setContent(
        `<body style="margin:0;background:#fff">
           <img style="display:block;width:100%" src="data:image/png;base64,${dashboardPng.toString("base64")}">
           <img style="display:block;width:100%" src="data:image/png;base64,${auditPng.toString("base64")}">
         </body>`,
      );
      await composite.locator("img").nth(1).waitFor();
      await shoot(composite, "shot-migration-dashboard.png");
      await context.close();
    }

    console.log("[shots] all captures done");
  } finally {
    await browser.close();
    await resetTrustModesToAdfs().catch(() => {});
    await closePool();
    await teardownStack(state);
    const stillBusy = [];
    for (const port of [8080, 8090, 8000, 5200, 5173]) {
      if (!(await portFree(port))) {
        stillBusy.push(port);
      }
    }
    console.log(`[shots] teardown complete${stillBusy.length ? `, ports still busy: ${stillBusy.join(", ")}` : ""}`);
  }
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
