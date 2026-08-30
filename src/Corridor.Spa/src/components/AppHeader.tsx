import { useAuth } from "../auth/AuthContext";
import { Link, type Route } from "../router";

interface AppHeaderProps {
  route: Route;
  onNavigate: (to: string) => void;
}

/** Blue federal header bar: brand, primary nav, session controls. */
export function AppHeader({ route, onNavigate }: AppHeaderProps) {
  const { user, signout } = useAuth();
  const who = user?.profile;
  const displayName =
    (typeof who?.name === "string" && who.name) ||
    (typeof who?.preferred_username === "string" && who.preferred_username) ||
    "Signed in";

  return (
    <header className="site-header">
      <div className="shell header-inner">
        <p className="brand">
          <Link to="/" className="brand-link" onNavigate={onNavigate}>
            FieldInsight
          </Link>
          <span className="brand-sub">Corridor inspector assignments</span>
        </p>
        <nav className="site-nav" aria-label="Primary">
          <Link
            to="/"
            className="nav-link"
            ariaCurrent={route.name === "assignments" || route.name === "assignment"}
            onNavigate={onNavigate}
          >
            Assignments
          </Link>
          <Link
            to="/profile"
            className="nav-link"
            ariaCurrent={route.name === "profile"}
            onNavigate={onNavigate}
          >
            Profile
          </Link>
        </nav>
        <div className="header-session">
          <span className="who">{displayName}</span>
          <button type="button" className="link-button" onClick={() => void signout()}>
            Sign out
          </button>
        </div>
      </div>
    </header>
  );
}
