import type { ChecklistProgress } from "../domain/assignments";

interface ProgressRingProps {
  progress: ChecklistProgress;
  label: string;
}

const size = 48;
const strokeWidth = 6;
const radius = (size - strokeWidth) / 2;
const circumference = 2 * Math.PI * radius;

/**
 * SVG progress ring for checklist completion. Carries an explicit
 * role="img" and aria-label so the state is announced without relying on
 * the geometry.
 */
export function ProgressRing({ progress, label }: ProgressRingProps) {
  const total = Math.max(progress.total, 1);
  const fraction = progress.done / total;
  const offset = circumference * (1 - fraction);
  const complete = progress.done === progress.total && progress.total > 0;
  return (
    <svg
      className={`progress-ring${complete ? " is-complete" : ""}`}
      width={size}
      height={size}
      viewBox={`0 0 ${size} ${size}`}
      role="img"
      aria-label={label}
      focusable="false"
    >
      <circle
        className="progress-ring-track"
        cx={size / 2}
        cy={size / 2}
        r={radius}
        strokeWidth={strokeWidth}
        fill="none"
      />
      <circle
        className="progress-ring-value"
        cx={size / 2}
        cy={size / 2}
        r={radius}
        strokeWidth={strokeWidth}
        strokeLinecap="round"
        fill="none"
        strokeDasharray={circumference}
        strokeDashoffset={offset}
        transform={`rotate(-90 ${size / 2} ${size / 2})`}
      />
      <text className="progress-ring-text" x="50%" y="50%" dominantBaseline="central" textAnchor="middle">
        {progress.done}/{progress.total}
      </text>
    </svg>
  );
}
