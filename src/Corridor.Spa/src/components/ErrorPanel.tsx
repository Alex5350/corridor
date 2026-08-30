import { describeError } from "../api/problem";

interface ErrorPanelProps {
  error: unknown;
  onRetry?: () => void;
  retryLabel?: string;
}

/**
 * Inline error panel with an optional retry action. role="alert" so screen
 * readers pick it up as soon as it mounts.
 */
export function ErrorPanel({ error, onRetry, retryLabel = "Retry" }: ErrorPanelProps) {
  return (
    <div className="notice error" role="alert">
      <p className="notice-title">{describeError(error)}</p>
      {onRetry ? (
        <button type="button" className="button secondary" onClick={onRetry}>
          {retryLabel}
        </button>
      ) : null}
    </div>
  );
}
