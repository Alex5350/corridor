import { useEffect, useState } from "react";
import type { AssignmentsApi } from "../api/client";
import { ErrorPanel } from "../components/ErrorPanel";
import { ProgressRing } from "../components/ProgressRing";
import { StatusPill } from "../components/StatusPill";
import {
  assignmentStatus,
  checklistProgress,
  formatDueDate,
  type Assignment,
} from "../domain/assignments";
import { Link } from "../router";

interface AssignmentsViewProps {
  api: AssignmentsApi;
  onNavigate: (to: string) => void;
}

/**
 * Assignments list: one card per inspection with licensee, focus, due date,
 * a progress ring, and a due-date status pill.
 */
export function AssignmentsView({ api, onNavigate }: AssignmentsViewProps) {
  const [assignments, setAssignments] = useState<Assignment[] | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [reloadKey, setReloadKey] = useState(0);

  useEffect(() => {
    let cancelled = false;
    api
      .list()
      .then((items) => {
        if (!cancelled) {
          setAssignments(items);
          setError(null);
        }
      })
      .catch((cause: unknown) => {
        if (!cancelled) {
          setError(cause);
        }
      });
    return () => {
      cancelled = true;
    };
  }, [api, reloadKey]);

  const retry = () => {
    setAssignments(null);
    setError(null);
    setReloadKey((key) => key + 1);
  };

  return (
    <>
      <h1>Assignments</h1>
      <p className="page-intro">
        Seeded inspection assignments for your upn, served by the portal API
        with your Okta access token.
      </p>

      {error ? <ErrorPanel error={error} onRetry={retry} /> : null}

      {assignments === null && !error ? (
        <p role="status">Loading assignments...</p>
      ) : null}

      {assignments !== null && assignments.length === 0 && !error ? (
        <div className="notice">
          No assignments are assigned to your account right now.
        </div>
      ) : null}

      {assignments !== null && assignments.length > 0 ? (
        <ul className="card-grid assignment-grid">
          {assignments.map((assignment) => {
            const progress = checklistProgress(assignment.checklist);
            const status = assignmentStatus(assignment, new Date());
            return (
              <li className="card assignment-card" key={assignment.id}>
                <div className="assignment-card-top">
                  <ProgressRing
                    progress={progress}
                    label={`${progress.done} of ${progress.total} checklist items complete`}
                  />
                  <StatusPill status={status} />
                </div>
                <h2 className="assignment-licensee">{assignment.licenseeName}</h2>
                <p className="assignment-focus">{assignment.focus}</p>
                <p className="assignment-due">
                  <span className="visually-hidden">Due date: </span>
                  Due {formatDueDate(assignment.dueAt)}
                </p>
                <Link
                  to={`/assignment/${assignment.id}`}
                  className="button secondary assignment-open"
                  onNavigate={onNavigate}
                >
                  Open checklist
                  <span className="visually-hidden">
                    {" "}
                    for {assignment.licenseeName}
                  </span>
                </Link>
              </li>
            );
          })}
        </ul>
      ) : null}
    </>
  );
}
