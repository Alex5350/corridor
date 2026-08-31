import { useMemo } from "react";
import type { UserManager } from "oidc-client-ts";
import { useAuth } from "./auth/AuthContext";
import { createApi } from "./api/client";
import { AppHeader } from "./components/AppHeader";
import { LoginGate } from "./views/LoginGate";
import { CallbackPage } from "./views/CallbackPage";
import { AssignmentsView } from "./views/AssignmentsView";
import { AssignmentDetailView } from "./views/AssignmentDetailView";
import { ProfileView } from "./views/ProfileView";
import { NotFoundView } from "./views/NotFoundView";
import { useRoute } from "./router";

interface AppProps {
  manager: UserManager;
}

/**
 * Application shell: routes first (the OIDC callback must run outside the
 * auth gate), then the gate (loading, anonymous, or authenticated), then the
 * header + view + footer.
 */
export function App({ manager }: AppProps) {
  const { route, navigate } = useRoute();
  const { status, user, sessionMessage, resetSession } = useAuth();

  const api = useMemo(
    () =>
      createApi(() => user?.access_token ?? null, {
        // The portal answering 401 means the session is dead: reset it and
        // land back on the login gate with a reason, not a dead-end error.
        onUnauthorized: () =>
          resetSession("Your session was rejected by the portal. Please sign in again."),
      }),
    [user, resetSession],
  );

  if (route.name === "callback") {
    return <CallbackPage manager={manager} />;
  }

  if (status === "loading") {
    return (
      <main className="shell" id="main">
        <p role="status">Loading your session...</p>
      </main>
    );
  }

  if (!user) {
    return (
      <>
        <HeaderBar />
        <LoginGate message={sessionMessage} />
        <SiteFooter />
      </>
    );
  }

  return (
    <>
      <a className="skip-link" href="#main">
        Skip to content
      </a>
      <AppHeader route={route} onNavigate={navigate} />
      <main className="shell" id="main">
        {route.name === "assignments" ? (
          <AssignmentsView api={api} onNavigate={navigate} />
        ) : null}
        {route.name === "assignment" ? (
          <AssignmentDetailView api={api} assignmentId={route.id} onNavigate={navigate} />
        ) : null}
        {route.name === "profile" ? <ProfileView onNavigate={navigate} /> : null}
        {route.name === "not-found" ? <NotFoundView onNavigate={navigate} /> : null}
      </main>
      <SiteFooter />
    </>
  );
}

/** Minimal header for the anonymous gate (no nav until signed in). */
function HeaderBar() {
  return (
    <header className="site-header">
      <div className="shell header-inner">
        <p className="brand">
          <span className="brand-link">FieldInsight</span>
          <span className="brand-sub">Corridor inspector assignments</span>
        </p>
      </div>
    </header>
  );
}

function SiteFooter() {
  return (
    <footer className="site-footer">
      <div className="shell">
        FieldInsight, part of the Corridor identity migration demo. Synthetic
        data only; no connection to any real agency.
      </div>
    </footer>
  );
}
