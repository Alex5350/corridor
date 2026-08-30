import { cleanup } from "@testing-library/react";
import { afterEach } from "vitest";
import "@testing-library/jest-dom/vitest";

// vitest runs without globals, so cleanup has to be wired explicitly.
afterEach(() => {
  cleanup();
});
