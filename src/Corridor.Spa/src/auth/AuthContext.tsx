import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from "react";
import type {
  SigninRedirectArgs,
  SignoutRedirectArgs,
  User,
  UserManager,
} from "oidc-client-ts";
import { config } from "../config";

/**
 * The slice of UserManager the app depends on. Structural on purpose: tests
 * inject a fake, production injects the real thing from createUserManager().
 */
export type AuthManager = Pick<
  UserManager,
  "getUser" | "signinRedirect" | "signoutRedirect" | "removeUser"
> & {
  events: Pick<
    UserManager["events"],
    | "addUserLoaded"
    | "removeUserLoaded"
    | "addSilentRenewError"
    | "removeSilentRenewError"
    | "addAccessTokenExpired"
    | "removeAccessTokenExpired"
  >;
};

export type AuthStatus = "loading" | "authenticated" | "anonymous";

export interface AuthState {
  status: AuthStatus;
  user: User | null;
  /** Why the user landed back on the login gate, if the session ended. */
  sessionMessage: string | null;
  signin: (loginHint?: string) => Promise<void>;
  signout: () => Promise<void>;
  /**
   * Drops a session that is no longer trustworthy (for example the portal
   * answering 401 against the access token): clears the stored user and
   * returns to the login gate with the given reason. Same reset path the
   * silent-renew and token-expired handlers use.
   */
  resetSession: (message: string) => void;
  clearSessionMessage: () => void;
}

const AuthContext = createContext<AuthState | null>(null);

interface AuthProviderProps {
  manager: AuthManager;
  children: ReactNode;
}

export function AuthProvider({ manager, children }: AuthProviderProps) {
  const [user, setUser] = useState<User | null>(null);
  const [ready, setReady] = useState(false);
  const [sessionMessage, setSessionMessage] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    manager
      .getUser()
      .then((found) => {
        if (!cancelled) {
          setUser(found);
          setReady(true);
        }
      })
      .catch(() => {
        if (!cancelled) {
          setUser(null);
          setReady(true);
        }
      });
    return () => {
      cancelled = true;
    };
  }, [manager]);

  useEffect(() => {
    const onUserLoaded = (loaded: User) => setUser(loaded);
    const onRenewError = () => {
      // Silent renew failed (refresh token expired or revoked): drop the
      // stale user and send the inspector back to the login gate with a
      // reason instead of letting API calls fail one by one.
      setUser(null);
      setSessionMessage(
        "Your session could not be refreshed. Please sign in again.",
      );
    };
    const onTokenExpired = () => {
      // automaticSilentRenew fires well before expiry; reaching this event
      // means renewal never happened, so the session is over.
      setUser(null);
      setSessionMessage("Your session expired. Please sign in again.");
    };
    manager.events.addUserLoaded(onUserLoaded);
    manager.events.addSilentRenewError(onRenewError);
    manager.events.addAccessTokenExpired(onTokenExpired);
    return () => {
      manager.events.removeUserLoaded(onUserLoaded);
      manager.events.removeSilentRenewError(onRenewError);
      manager.events.removeAccessTokenExpired(onTokenExpired);
    };
  }, [manager]);

  const signin = useCallback(
    async (loginHint?: string) => {
      const args: SigninRedirectArgs = loginHint ? { login_hint: loginHint } : {};
      await manager.signinRedirect(args);
    },
    [manager],
  );

  const signout = useCallback(async () => {
    // okta-sim's /logout requires client_id to match the registered
    // post_logout_redirect_uri before it redirects back, so it rides along
    // as an extra query parameter.
    const args: SignoutRedirectArgs = {
      extraQueryParams: { client_id: config.clientId },
    };
    await manager.signoutRedirect(args);
  }, [manager]);

  const clearSessionMessage = useCallback(() => setSessionMessage(null), []);

  const resetSession = useCallback(
    (message: string) => {
      setUser(null);
      setSessionMessage(message);
      void manager.removeUser();
    },
    [manager],
  );

  const value = useMemo<AuthState>(
    () => ({
      status: !ready ? "loading" : user ? "authenticated" : "anonymous",
      user,
      sessionMessage,
      signin,
      signout,
      resetSession,
      clearSessionMessage,
    }),
    [ready, user, sessionMessage, signin, signout, resetSession, clearSessionMessage],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext);
  if (!ctx) {
    throw new Error("useAuth must be used inside an AuthProvider");
  }
  return ctx;
}
