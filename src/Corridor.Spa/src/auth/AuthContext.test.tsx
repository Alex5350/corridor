import { describe, expect, it, vi } from "vitest";
import { act, render, screen, waitFor } from "@testing-library/react";
import type { User } from "oidc-client-ts";
import { AuthProvider, useAuth, type AuthManager } from "./AuthContext";

function fakeUser(): User {
  return {
    access_token: "at",
    id_token: "id",
    profile: { upn: "inspector@corridor.example", name: "Inspecting Iverson", role: "Inspector" },
    expired: false,
  } as unknown as User;
}

type UserLoadedHandler = (user: User) => void;
type RenewErrorHandler = (error: Error) => void;
type TokenExpiredHandler = () => void;

interface HandlerBag {
  userLoaded: UserLoadedHandler[];
  renewError: RenewErrorHandler[];
  tokenExpired: TokenExpiredHandler[];
}

/** A UserManager stand-in: records event handlers so tests can fire them. */
function fakeManager(user: User | null) {
  const handlers: HandlerBag = { userLoaded: [], renewError: [], tokenExpired: [] };
  const manager: AuthManager & { fire(name: keyof HandlerBag, arg?: unknown): void } = {
    getUser: vi.fn(async () => user),
    signinRedirect: vi.fn(async () => undefined),
    signoutRedirect: vi.fn(async () => undefined),
    removeUser: vi.fn(async () => undefined),
    events: {
      addUserLoaded: (cb: UserLoadedHandler): (() => void) => {
        handlers.userLoaded.push(cb);
        return () => {
          handlers.userLoaded = handlers.userLoaded.filter((entry) => entry !== cb);
        };
      },
      removeUserLoaded: (cb: UserLoadedHandler) => {
        handlers.userLoaded = handlers.userLoaded.filter((entry) => entry !== cb);
      },
      addSilentRenewError: (cb: RenewErrorHandler): (() => void) => {
        handlers.renewError.push(cb);
        return () => {
          handlers.renewError = handlers.renewError.filter((entry) => entry !== cb);
        };
      },
      removeSilentRenewError: (cb: RenewErrorHandler) => {
        handlers.renewError = handlers.renewError.filter((entry) => entry !== cb);
      },
      addAccessTokenExpired: (cb: TokenExpiredHandler): (() => void) => {
        handlers.tokenExpired.push(cb);
        return () => {
          handlers.tokenExpired = handlers.tokenExpired.filter((entry) => entry !== cb);
        };
      },
      removeAccessTokenExpired: (cb: TokenExpiredHandler) => {
        handlers.tokenExpired = handlers.tokenExpired.filter((entry) => entry !== cb);
      },
    },
    fire(name: keyof HandlerBag, arg?: unknown) {
      if (name === "userLoaded") {
        for (const handler of handlers.userLoaded) {
          handler(arg as User);
        }
      } else if (name === "renewError") {
        for (const handler of handlers.renewError) {
          handler(arg as Error);
        }
      } else {
        for (const handler of handlers.tokenExpired) {
          handler();
        }
      }
    },
  };
  return manager;
}

function Probe() {
  const { status, sessionMessage } = useAuth();
  return (
    <p data-testid="probe">
      {status}|{sessionMessage ?? "-"}
    </p>
  );
}

describe("AuthProvider session lifecycle", () => {
  it("starts authenticated when a stored user is present", async () => {
    const manager = fakeManager(fakeUser());
    render(
      <AuthProvider manager={manager}>
        <Probe />
      </AuthProvider>,
    );
    await waitFor(() =>
      expect(screen.getByTestId("probe")).toHaveTextContent(/^authenticated\|-$/),
    );
  });

  it("returns to the login gate with a message when silent renew fails", async () => {
    const manager = fakeManager(fakeUser());
    render(
      <AuthProvider manager={manager}>
        <Probe />
      </AuthProvider>,
    );
    await waitFor(() =>
      expect(screen.getByTestId("probe")).toHaveTextContent(/^authenticated\|-$/),
    );

    act(() => manager.fire("renewError", new Error("refresh token expired")));

    expect(screen.getByTestId("probe")).toHaveTextContent(
      /^anonymous\|Your session could not be refreshed/,
    );
  });

  it("returns to the login gate when the access token expires without renewal", async () => {
    const manager = fakeManager(fakeUser());
    render(
      <AuthProvider manager={manager}>
        <Probe />
      </AuthProvider>,
    );
    await waitFor(() =>
      expect(screen.getByTestId("probe")).toHaveTextContent(/^authenticated\|-$/),
    );

    act(() => manager.fire("tokenExpired"));

    expect(screen.getByTestId("probe")).toHaveTextContent(
      /^anonymous\|Your session expired/,
    );
  });

  it("starts anonymous when no stored user exists", async () => {
    const manager = fakeManager(null);
    render(
      <AuthProvider manager={manager}>
        <Probe />
      </AuthProvider>,
    );
    await waitFor(() =>
      expect(screen.getByTestId("probe")).toHaveTextContent(/^anonymous\|-$/),
    );
  });
});
