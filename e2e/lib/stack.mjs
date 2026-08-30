/**
 * Boots and tears down the whole Corridor stack for the e2e suite, mirroring the
 * approach of tests/Corridor.IntegrationTests/Infrastructure/CorridorStackFixture.cs:
 *
 * - Database: reuse a SQL Server already listening on localhost:1433 (the compose
 *   db from docker-compose.yml, exactly like the fixture's TryConnectToComposeDatabase),
 *   otherwise start the compose db service ourselves
 *   (`docker compose --profile ci up -d --wait db`). The db/sql scripts are
 *   idempotent, so they are simply re-applied on every boot.
 * - Services: `dotnet run --project ... --no-launch-profile` with explicit
 *   ASPNETCORE_URLS and ConnectionStrings__Corridor, then a /healthz wait loop.
 * - SPA: `npm run dev` (vite, strict port 5173), waited on with an HTTP GET /.
 * - Reuse: anything already healthy on a contract port is reused and reported,
 *   never killed by teardown. Teardown kills only the processes it started.
 */

import { spawn } from "node:child_process";
import { createServer } from "node:net";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));

/** Walks up from this file to the directory containing db/sql/001_schemas.sql. */
function findRepoRoot() {
  let dir = here;
  for (let i = 0; i < 6; i += 1) {
    if (existsSync(path.join(dir, "db", "sql", "001_schemas.sql"))) {
      return dir;
    }
    dir = path.dirname(dir);
  }
  throw new Error("Could not locate the repository root above e2e/lib.");
}

export const REPO_ROOT = findRepoRoot();

export const PORTS = {
  okta: 8080,
  adfs: 8090,
  legacy: 8000,
  portal: 5200,
  spa: 5173,
  db: 1433,
};

export const SA_PASSWORD = "CorridorDev1!";
export const DEMO_PASSWORD = "Demo1234!";

/** The connection the .NET services and the SQL helper share. */
export const DB_CONNECTION_STRING =
  `Server=localhost,${PORTS.db};Database=Corridor;User Id=sa;Password=${SA_PASSWORD};TrustServerCertificate=True`;

const DOTNET_SERVICES = [
  { name: "Corridor.OktaSim", port: PORTS.okta },
  { name: "Corridor.AdfsSim", port: PORTS.adfs },
  { name: "Corridor.Legacy", port: PORTS.legacy },
  { name: "Corridor.Portal", port: PORTS.portal },
];

const SPA_DIR = path.join(REPO_ROOT, "src", "Corridor.Spa");

export const STATE_PATH = path.join(here, "..", ".stack-state.json");

/** True when the port can be bound, i.e. nothing is listening on it. */
export function portFree(port) {
  return new Promise((resolve) => {
    const probe = createServer();
    probe.once("error", () => resolve(false));
    probe.once("listening", () => probe.close(() => resolve(true)));
    probe.listen(port, "127.0.0.1");
  });
}

async function fetchWithTimeout(url, ms) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), ms);
  try {
    return await fetch(url, { signal: controller.signal, redirect: "manual" });
  } finally {
    clearTimeout(timer);
  }
}

/** healthz probe: JSON body containing "ok" (all four .NET services expose it). */
export async function dotnetHealthy(port) {
  try {
    const response = await fetchWithTimeout(`http://localhost:${port}/healthz`, 3000);
    if (!response.ok) {
      return false;
    }
    const body = await response.text();
    return body.includes("ok");
  } catch {
    return false;
  }
}

/** The vite dev server has no healthz; its index page answering is readiness. */
export async function spaHealthy() {
  try {
    const response = await fetchWithTimeout(`http://localhost:${PORTS.spa}/`, 3000);
    return response.status === 200;
  } catch {
    return false;
  }
}

function run(command, args, options) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { stdio: ["ignore", "pipe", "pipe"], ...options });
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (chunk) => { stdout += chunk.toString(); });
    child.stderr.on("data", (chunk) => { stderr += chunk.toString(); });
    child.on("error", reject);
    child.on("exit", (code) => {
      if (code === 0) {
        resolve(stdout);
      } else {
        reject(new Error(`${command} ${args.join(" ")} exited ${code}\n${stdout}\n${stderr}`));
      }
    });
  });
}

/**
 * Starts a long-lived child in its own process group and pipes its output to a
 * log file under the OS temp dir, so failures can be diagnosed after the fact.
 * The negative pid (process group) is stored: killing it takes the whole tree
 * (`dotnet run` spawns the app, `npm run dev` spawns vite).
 */
/**
 * Starts a long-lived child in its own process group and pipes its output to a
 * log file under the OS temp dir, so failures can be diagnosed after the fact.
 * The negative pid (process group) is the kill handle: killing it takes the
 * whole tree (`dotnet run` spawns the app, `npm run dev` spawns vite).
 */
export function spawnGroup(command, args, options, logPath) {
  const child = spawn(command, args, {
    stdio: ["ignore", "pipe", "pipe"],
    detached: true,
    ...options,
  });
  mkdirSync(path.dirname(logPath), { recursive: true });
  const log = (chunk) => {
    try {
      writeFileSync(logPath, chunk.toString(), { flag: "a" });
    } catch {
      // Logging is best effort only.
    }
  };
  child.stdout.on("data", log);
  child.stderr.on("data", log);
  return child;
}

async function waitFor(what, check, deadlineMs, logPath) {
  const deadline = Date.now() + deadlineMs;
  let lastError = "no attempt made";
  while (Date.now() < deadline) {
    try {
      if (await check()) {
        return;
      }
      lastError = "health probe answered negative";
    } catch (error) {
      lastError = error?.message ?? String(error);
    }
    await sleep(750);
  }
  let logTail = "(no log file)";
  try {
    logTail = readFileSync(logPath, "utf8").slice(-4000);
  } catch {
    // Keep the placeholder.
  }
  throw new Error(`${what} did not become healthy in time. Last error: ${lastError}\nLog tail:\n${logTail}`);
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/**
 * Boots everything the specs need. Returns the state record that teardown
 * consumes; saveState/writeState keep it across the setup/teardown process
 * boundary.
 */
export async function bootStack() {
  const logDir = path.join(process.env.TMPDIR || "/tmp", "corridor-e2e-logs");
  mkdirSync(logDir, { recursive: true });
  const state = {
    logDir,
    started: [], // { label, kind, pid, port }
    reused: [], // labels of things that were already healthy
    dbStartedByUs: false,
  };

  // 1. Reuse detection: healthy listeners are adopted, half-dead listeners abort.
  const pending = [];
  for (const service of DOTNET_SERVICES) {
    if (await dotnetHealthy(service.port)) {
      state.reused.push(service.name);
      console.log(`[stack] ${service.name} already healthy on ${service.port}, reusing it`);
    } else if (!(await portFree(service.port))) {
      throw new Error(
        `Port ${service.port} is taken but ${service.name} is not healthy; another Corridor stack is probably mid-boot. Stop it or let it finish.`);
    } else {
      pending.push(service);
    }
  }
  const spaNeeded = !(await spaHealthy());

  // 2. Database: reuse or start the compose db, then apply the idempotent scripts.
  const { waitForSqlServer, applySqlScripts, closePool } = await import("./sql.mjs");
  const dbListening = await (async () => {
    // A direct login probe is the source of truth; a raw port check is the guard.
    try {
      await waitForSqlServer(1500);
      return true;
    } catch {
      return false;
    }
  })();
  if (!dbListening) {
    if (!(await portFree(PORTS.db))) {
      throw new Error(`Port ${PORTS.db} is taken but SQL Server does not answer a login; refusing to fight it.`);
    }
    console.log("[stack] starting the compose db service (docker compose --profile ci up -d --wait db)");
    await run("docker", ["compose", "--profile", "ci", "up", "-d", "--wait", "db"], { cwd: REPO_ROOT });
    state.dbStartedByUs = true;
    await waitFor("SQL Server (compose db)", () => waitForSqlServer(2000).then(() => true, () => false), 120_000);
  } else {
    console.log("[stack] SQL Server already accepting logins on 1433, reusing it");
  }
  console.log("[stack] applying db/sql scripts (idempotent)");
  await applySqlScripts();
  await closePool();

  // 3. Start the .NET services that were not already healthy.
  for (const service of pending) {
    const projectPath = path.join(REPO_ROOT, "src", service.name);
    const logPath = path.join(logDir, `${service.name}-${service.port}.log`);
    const child = spawnGroup(
      "dotnet",
      ["run", "--project", projectPath, "--no-launch-profile"],
      {
        cwd: projectPath,
        env: {
          ...process.env,
          ASPNETCORE_ENVIRONMENT: "Development",
          ASPNETCORE_URLS: `http://localhost:${service.port}`,
          ConnectionStrings__Corridor: DB_CONNECTION_STRING,
        },
      },
      logPath,
    );
    state.started.push({ label: service.name, kind: "dotnet", pid: child.pid, port: service.port });
    console.log(`[stack] started ${service.name} (pid ${child.pid}) on ${service.port}`);
  }
  for (const service of pending) {
    await waitFor(
      service.name,
      () => dotnetHealthy(service.port),
      300_000,
      path.join(logDir, `${service.name}-${service.port}.log`),
    );
  }

  // 4. SPA dev server.
  if (spaNeeded) {
    if (!(await portFree(PORTS.spa))) {
      throw new Error(`Port ${PORTS.spa} is taken but the SPA dev server is not answering.`);
    }
    const logPath = path.join(logDir, "Corridor.Spa-5173.log");
    const child = spawnGroup(
      process.platform === "win32" ? "npm.cmd" : "npm",
      ["run", "dev"],
      { cwd: SPA_DIR, env: { ...process.env } },
      logPath,
    );
    state.started.push({ label: "Corridor.Spa", kind: "npm", pid: child.pid, port: PORTS.spa });
    console.log(`[stack] started the SPA dev server (pid ${child.pid}) on ${PORTS.spa}`);
    await waitFor("SPA dev server", () => spaHealthy(), 120_000, logPath);
  } else {
    console.log(`[stack] SPA already answering on ${PORTS.spa}, reusing it`);
    state.reused.push("Corridor.Spa");
  }

  writeState(state);
  return state;
}

export function writeState(state) {
  writeFileSync(STATE_PATH, JSON.stringify(state, null, 2));
}

export function readState() {
  return JSON.parse(readFileSync(STATE_PATH, "utf8"));
}

/** SIGTERM then SIGKILL to a whole process group; safe to call on dead pids. */
export async function killGroup(pid) {
  // SIGTERM to the process group, then SIGKILL to whatever survives 5 seconds.
  try {
    process.kill(-pid, "SIGTERM");
  } catch {
    // Already gone.
  }
  const deadline = Date.now() + 5000;
  while (Date.now() < deadline) {
    try {
      process.kill(-pid, 0);
      await sleep(250);
    } catch {
      return;
    }
  }
  try {
    process.kill(-pid, "SIGKILL");
  } catch {
    // Already gone.
  }
}

/**
 * Kills everything this run started (never anything it reused) and stops the
 * db container when this run started it. Safe to call twice.
 */
export async function teardownStack(state) {
  // Port sweep inputs first: only the ports of processes we started must be
  // free afterwards, so capture them before the list is cleared.
  const watched = (state.started ?? []).map((entry) => entry.port);
  for (const entry of state.started ?? []) {
    if (entry.pid) {
      console.log(`[stack] stopping ${entry.label} (pid ${entry.pid})`);
      await killGroup(entry.pid);
    }
  }
  state.started = [];

  if (state.dbStartedByUs) {
    console.log("[stack] stopping the compose db container this run started");
    try {
      // `down` scoped to the ci profile: removes the db container and the
      // compose network this run created. The named volume survives, so the
      // seeded data is warm for the next boot.
      await run("docker", ["compose", "--profile", "ci", "down", "--remove-orphans"], { cwd: REPO_ROOT });
    } catch (error) {
      console.log(`[stack] compose db teardown reported: ${error.message.split("\n")[0]}`);
    }
  }

  const deadline = Date.now() + 10_000;
  while (Date.now() < deadline) {
    const busy = [];
    for (const port of watched) {
      if (!(await portFree(port))) {
        busy.push(port);
      }
    }
    if (busy.length === 0) {
      break;
    }
    await sleep(300);
  }
  writeState(state);
}
