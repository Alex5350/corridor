import { useCallback, useEffect, useState } from "react";
import type { AssignmentsApi } from "../api/client";
import { ErrorPanel } from "../components/ErrorPanel";
import { ProgressRing } from "../components/ProgressRing";
import { StatusPill } from "../components/StatusPill";
import {
  checklistProgress,
  checklistReducer,
  dueDateStatus,
  formatDueDate,
  statusLabels,
  type ChecklistItem,
} from "../domain/assignments";
import { Link } from "../router";

interface AssignmentDetailViewProps {
  api: AssignmentsApi;
  assignmentId: number;
  onNavigate: (to: string) => void;
}

interface FailedWrite {
  itemIndex: number;
  done: boolean;
  error: unknown;
}

/**
 * Assignment detail: checklist rows are native checkboxes. Toggling applies
 * optimistically, PATCHes the portal, and rolls the row back if the write
 * fails; the inline panel offers a retry of the last failed write.
 */
export function AssignmentDetailView({
  api,
  assignmentId,
  onNavigate,
}: AssignmentDetailViewProps) {
  const [items, setItems] = useState<ChecklistItem[] | null>(null);
  const [heading, setHeading] = useState<{ licensee: string; focus: string; dueAt: string } | null>(null);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [failedWrite, setFailedWrite] = useState<FailedWrite | null>(null);
  const [announcement, setAnnouncement] = useState<string | null>(null);
  const [reloadKey, setReloadKey] = useState(0);
  const [writing, setWriting] = useState(false);

  useEffect(() => {
    let cancelled = false;
    api
      .list()
      .then((assignments) => {
        if (cancelled) {
          return;
        }
        const found = assignments.find((entry) => entry.id === assignmentId);
        if (!found) {
          setLoadError(new Error("That assignment does not exist or is not assigned to you."));
          return;
        }
        setHeading({ licensee: found.licenseeName, focus: found.focus, dueAt: found.dueAt });
        setItems(found.checklist);
        setLoadError(null);
      })
      .catch((cause: unknown) => {
        if (!cancelled) {
          setLoadError(cause);
        }
      });
    return () => {
      cancelled = true;
    };
  }, [api, assignmentId, reloadKey]);

  const retry = () => {
    setItems(null);
    setHeading(null);
    setLoadError(null);
    setReloadKey((key) => key + 1);
  };

  const persist = useCallback(
    async (itemIndex: number, done: boolean, rollbackTo: ChecklistItem[]) => {
      setWriting(true);
      try {
        await api.setChecklistItem(assignmentId, itemIndex, done);
        setFailedWrite(null);
        setAnnouncement(done ? "Saved: item marked done." : "Saved: item reopened.");
      } catch (error) {
        setItems(rollbackTo);
        setFailedWrite({ itemIndex, done, error });
        setAnnouncement("Save failed. The change was rolled back.");
      } finally {
        setWriting(false);
      }
    },
    [api, assignmentId],
  );

  const onToggle = (index: number, next: boolean) => {
    if (!items || writing) {
      return;
    }
    const optimistic = checklistReducer(items, { type: "set", index, done: next });
    setItems(optimistic);
    setFailedWrite(null);
    setAnnouncement(null);
    void persist(index, next, items);
  };

  const retryWrite = () => {
    if (!items || !failedWrite || writing) {
      return;
    }
    const optimistic = checklistReducer(items, {
      type: "set",
      index: failedWrite.itemIndex,
      done: failedWrite.done,
    });
    setItems(optimistic);
    void persist(failedWrite.itemIndex, failedWrite.done, items);
  };

  if (loadError) {
    return (
      <>
        <h1>Assignment</h1>
        <ErrorPanel error={loadError} onRetry={retry} retryLabel="Reload assignment" />
        <p>
          <Link to="/" className="button secondary" onNavigate={onNavigate}>
            Back to assignments
          </Link>
        </p>
      </>
    );
  }

  if (!items || !heading) {
    return (
      <>
        <h1>Assignment</h1>
        <p role="status">Loading assignment...</p>
      </>
    );
  }

  const progress = checklistProgress(items);
  const status = dueDateStatus(heading.dueAt, new Date(), progress.done === progress.total && progress.total > 0);

  return (
    <>
      <p>
        <Link to="/" className="back-link" onNavigate={onNavigate}>
          &larr; All assignments
        </Link>
      </p>
      <div className="card detail-heading">
        <div className="assignment-card-top">
          <ProgressRing
            progress={progress}
            label={`${progress.done} of ${progress.total} checklist items complete`}
          />
          <StatusPill status={status} />
        </div>
        <h1>{heading.licensee}</h1>
        <p className="assignment-focus">{heading.focus}</p>
        <p className="assignment-due">
          <span className="visually-hidden">Due date: </span>
          Due {formatDueDate(heading.dueAt)}
          <span className="visually-hidden">. Status {statusLabels[status]}.</span>
        </p>
        <p className="progress-text">
          {progress.done} of {progress.total} checklist items complete
        </p>
      </div>

      <p className="sr-live" role="status" aria-live="polite">
        {announcement}
      </p>

      {failedWrite ? (
        <ErrorPanel error={failedWrite.error} onRetry={retryWrite} retryLabel="Retry save" />
      ) : null}

      <div className="card">
        <h2>Inspection checklist</h2>
        <ul className="checklist">
          {items.map((entry, index) => {
            const id = `checklist-item-${index}`;
            return (
              <li className="check-row" key={id}>
                <input
                  id={id}
                  type="checkbox"
                  className="check-input"
                  checked={entry.done}
                  onChange={(event) => onToggle(index, event.target.checked)}
                />
                <label htmlFor={id} className="check-label">
                  <span className="visually-hidden">
                    {entry.done ? "Mark not done: " : "Mark done: "}
                  </span>
                  {entry.item}
                </label>
                <span className={`check-state${entry.done ? " is-done" : ""}`} aria-hidden="true">
                  {entry.done ? "Done" : "Open"}
                </span>
              </li>
            );
          })}
        </ul>
        <p className="field-hint">
          Changes save immediately. If a save fails, the row rolls back and can
          be retried.
        </p>
      </div>
    </>
  );
}
