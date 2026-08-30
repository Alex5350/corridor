import { Link } from "../router";

/** Plain 404 for any unknown path. */
export function NotFoundView({ onNavigate }: { onNavigate: (to: string) => void }) {
  return (
    <>
      <h1>Page not found</h1>
      <div className="notice">
        <p>There is nothing at this address. Synthetic demo; no content was harmed.</p>
      </div>
      <p>
        <Link to="/" className="button" onNavigate={onNavigate}>
          Go to assignments
        </Link>
      </p>
    </>
  );
}
