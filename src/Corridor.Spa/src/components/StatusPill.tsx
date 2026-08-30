import { statusLabels, type AssignmentStatus } from "../domain/assignments";

/** Status pill colored by due-date bucket (and completion). */
export function StatusPill({ status }: { status: AssignmentStatus }) {
  return (
    <span className={`badge badge-${status}`}>
      <span className="visually-hidden">Status: </span>
      {statusLabels[status]}
    </span>
  );
}
