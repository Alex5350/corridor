/**
 * Assignment domain: shapes mirrored from the portal's REST contract, the
 * checklist reducer used by the detail view, and due-date status math.
 * Pure functions only, so everything here is unit-testable.
 */

export interface ChecklistItem {
  item: string;
  done: boolean;
}

export interface Assignment {
  id: number;
  inspectorUpn: string;
  licenseeName: string;
  focus: string;
  dueAt: string;
  checklist: ChecklistItem[];
}

export type ChecklistAction =
  | { type: "toggle"; index: number }
  | { type: "set"; index: number; done: boolean }
  | { type: "replace"; items: ChecklistItem[] };

/**
 * Immutably update a checklist. Out-of-range or negative indexes are ignored:
 * the exact same array reference comes back, which callers can rely on for
 * change detection and rollback.
 */
export function checklistReducer(
  state: ChecklistItem[],
  action: ChecklistAction,
): ChecklistItem[] {
  switch (action.type) {
    case "replace":
      return action.items;
    case "toggle":
      return applyAt(state, action.index, (item) => ({ ...item, done: !item.done }));
    case "set":
      return applyAt(state, action.index, (item) => ({ ...item, done: action.done }));
  }
}

function applyAt(
  state: ChecklistItem[],
  index: number,
  update: (item: ChecklistItem) => ChecklistItem,
): ChecklistItem[] {
  if (!Number.isInteger(index) || index < 0 || index >= state.length) {
    return state;
  }
  const next = state.slice();
  next[index] = update(state[index]);
  return next;
}

export interface ChecklistProgress {
  done: number;
  total: number;
}

export function checklistProgress(items: ChecklistItem[]): ChecklistProgress {
  return {
    done: items.filter((entry) => entry.done).length,
    total: items.length,
  };
}

export function allDone(items: ChecklistItem[]): boolean {
  return items.length > 0 && items.every((entry) => entry.done);
}

/** Status buckets shown in the pill on every assignment card. */
export type AssignmentStatus = "complete" | "overdue" | "due-soon" | "on-track";

/** An assignment counts as "due soon" inside this window (in days). */
export const dueSoonDays = 3;

export function dueDateStatus(
  dueAt: string,
  now: Date,
  complete: boolean,
): AssignmentStatus {
  if (complete) {
    return "complete";
  }
  const due = new Date(dueAt);
  const msUntilDue = due.getTime() - now.getTime();
  if (msUntilDue < 0) {
    return "overdue";
  }
  if (msUntilDue <= dueSoonDays * 24 * 60 * 60 * 1000) {
    return "due-soon";
  }
  return "on-track";
}

export function assignmentStatus(assignment: Assignment, now: Date): AssignmentStatus {
  return dueDateStatus(assignment.dueAt, now, allDone(assignment.checklist));
}

export const statusLabels: Record<AssignmentStatus, string> = {
  complete: "Complete",
  overdue: "Overdue",
  "due-soon": "Due soon",
  "on-track": "On track",
};

/** Stable human date, e.g. "Sep 12, 2026". */
export function formatDueDate(dueAt: string): string {
  return new Date(dueAt).toLocaleDateString("en-US", {
    month: "short",
    day: "numeric",
    year: "numeric",
  });
}
