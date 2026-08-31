import { StrictMode } from "react";
import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import type { UserManager } from "oidc-client-ts";
import { CallbackPage } from "./CallbackPage";

/**
 * jsdom's Location methods are non-configurable, but the window.location
 * property itself is, so the redirect target can be captured as a mock.
 */
function stubLocationReplace(): { replace: ReturnType<typeof vi.fn>; restore: () => void } {
  const original = window.location;
  const replace = vi.fn();
  Object.defineProperty(window, "location", {
    value: { ...original, replace },
    writable: true,
    configurable: true,
  });
  return {
    replace,
    restore: () => {
      Object.defineProperty(window, "location", {
        value: original,
        writable: true,
        configurable: true,
      });
    },
  };
}

describe("CallbackPage", () => {
  it("issues exactly one token exchange despite StrictMode double-invoking the effect", async () => {
    const { replace, restore } = stubLocationReplace();
    try {
      let resolveExchange: () => void = () => undefined;
      const signinCallback = vi.fn(
        () =>
          new Promise<void>((resolve) => {
            resolveExchange = resolve;
          }),
      );
      const manager = { signinCallback } as unknown as UserManager;

      render(
        <StrictMode>
          <CallbackPage manager={manager} />
        </StrictMode>,
      );

      // Both StrictMode passes ran; only the first may start the exchange
      // (a second POST /token would race the single-use authorization code).
      expect(await screen.findByRole("status")).toHaveTextContent(
        /Validating the sign-in response/,
      );
      expect(signinCallback).toHaveBeenCalledTimes(1);

      resolveExchange();
      await waitFor(() => expect(replace).toHaveBeenCalledWith("/"));
      expect(signinCallback).toHaveBeenCalledTimes(1);
    } finally {
      restore();
    }
  });

  it("shows the error panel when the exchange fails", async () => {
    const signinCallback = vi.fn(async () => {
      throw new Error("invalid_grant: code already redeemed");
    });
    const manager = { signinCallback } as unknown as UserManager;

    render(<CallbackPage manager={manager} />);

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent(/invalid_grant/);
    expect(screen.getByRole("link", { name: "Back to sign in" })).toBeInTheDocument();
  });
});
