import { defineConfig, devices } from "@playwright/test";

/**
 * Chromium only by default; the suite is self-bootstrapping: no webServer. The
 * global setup boots the database, the four .NET services, and the SPA dev
 * server (reusing anything already healthy), and the global teardown stops
 * exactly what it started. Specs run sequentially in one worker because they
 * share the database's per-app TrustMode state; each spec sets the mode it
 * needs up front and restores the baseline afterwards, so file order is not a
 * correctness requirement, only the documented narrative order.
 */
export default defineConfig({
  testDir: "./specs",
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [["list"]],
  outputDir: "./test-results",
  globalSetup: "./global-setup.ts",
  globalTeardown: "./global-teardown.ts",
  use: {
    baseURL: "http://localhost:5200",
    trace: "retain-on-failure",
    screenshot: "off",
    video: "off",
  },
  projects: [
    {
      name: "chromium",
      use: { ...devices["Desktop Chrome"] },
    },
  ],
});
