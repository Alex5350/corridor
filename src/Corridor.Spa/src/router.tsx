import { useCallback, useEffect, useState, type MouseEvent, type ReactNode } from "react";

/**
 * Minimal history-API router. No router dependency: the app has four routes,
 * so pathname matching plus pushState is all it needs. Real hrefs stay on the
 * links so open-in-new-tab and keyboard navigation keep working.
 */

export type Route =
  | { name: "assignments" }
  | { name: "assignment"; id: number }
  | { name: "profile" }
  | { name: "callback" }
  | { name: "not-found" };

export function matchRoute(pathname: string): Route {
  if (pathname === "/") {
    return { name: "assignments" };
  }
  const assignmentMatch = /^\/assignment\/(\d+)\/?$/.exec(pathname);
  if (assignmentMatch) {
    return { name: "assignment", id: Number(assignmentMatch[1]) };
  }
  if (pathname === "/profile") {
    return { name: "profile" };
  }
  if (pathname === "/callback") {
    return { name: "callback" };
  }
  return { name: "not-found" };
}

export function useRoute(): { route: Route; navigate: (to: string) => void } {
  const [pathname, setPathname] = useState(() => window.location.pathname);

  useEffect(() => {
    const onPopState = () => setPathname(window.location.pathname);
    window.addEventListener("popstate", onPopState);
    return () => window.removeEventListener("popstate", onPopState);
  }, []);

  const navigate = useCallback((to: string) => {
    window.history.pushState({}, "", to);
    setPathname(to);
    window.scrollTo(0, 0);
  }, []);

  return { route: matchRoute(pathname), navigate };
}

interface LinkProps {
  to: string;
  children: ReactNode;
  className?: string;
  ariaCurrent?: boolean;
  onNavigate?: (to: string) => void;
}

/** An anchor that intercepts left clicks for SPA navigation. */
export function Link({ to, children, className, ariaCurrent, onNavigate }: LinkProps) {
  const handleClick = (event: MouseEvent<HTMLAnchorElement>) => {
    if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) {
      return;
    }
    event.preventDefault();
    onNavigate?.(to);
  };
  return (
    <a
      href={to}
      className={className}
      aria-current={ariaCurrent ? "page" : undefined}
      onClick={handleClick}
    >
      {children}
    </a>
  );
}
