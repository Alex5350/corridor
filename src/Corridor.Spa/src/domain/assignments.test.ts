import { describe, expect, it } from "vitest";
import {
  allDone,
  assignmentStatus,
  checklistProgress,
  checklistReducer,
  dueDateStatus,
  formatDueDate,
  statusLabels,
  type Assignment,
  type ChecklistItem,
} from "./assignments";

function items(...done: boolean[]): ChecklistItem[] {
  return done.map((entry, index) => ({ item: `Step ${index + 1}`, done: entry }));
}

const base: Assignment = {
  id: 1,
  inspectorUpn: "inspector@corridor.example",
  licenseeName: "Northgate Firearms",
  focus: "Acquisition record spot check",
  dueAt: "2026-10-01T12:00:00Z",
  checklist: items(false, false, false),
};

describe("checklistReducer", () => {
  it("toggles the item at the given index and leaves siblings alone", () => {
    const before = items(false, false, false);
    const after = checklistReducer(before, { type: "toggle", index: 1 });
    expect(after).toEqual(items(false, true, false));
  });

  it("returns a new array instead of mutating the old one", () => {
    const before = items(false, false);
    const after = checklistReducer(before, { type: "set", index: 0, done: true });
    expect(after).not.toBe(before);
    expect(before[0].done).toBe(false);
    expect(after[0].done).toBe(true);
  });

  it("ignores an out-of-range index and returns the same reference", () => {
    const before = items(false, true);
    expect(checklistReducer(before, { type: "toggle", index: 7 })).toBe(before);
    expect(checklistReducer(before, { type: "set", index: -1, done: true })).toBe(before);
    expect(checklistReducer(before, { type: "toggle", index: 1.5 })).toBe(before);
  });

  it("replaces the whole checklist on the replace action", () => {
    const next = items(true, true);
    expect(checklistReducer(items(false), { type: "replace", items: next })).toBe(next);
  });
});

describe("checklist progress and completion", () => {
  it("counts done items against the total", () => {
    expect(checklistProgress(items(false, true, true, false))).toEqual({ done: 2, total: 4 });
  });

  it("treats an all-done checklist as complete but not an empty one", () => {
    expect(allDone(items(true, true))).toBe(true);
    expect(allDone(items(true, false))).toBe(false);
    expect(allDone([])).toBe(false);
  });

  it("marks the assignment complete when every checklist item is done", () => {
    const done = { ...base, checklist: items(true, true, true) };
    expect(assignmentStatus(done, new Date("2026-09-01T00:00:00Z"))).toBe("complete");
  });
});

describe("dueDateStatus", () => {
  const now = new Date("2026-09-01T12:00:00Z");

  it("flags past due dates as overdue", () => {
    expect(dueDateStatus("2026-08-31T12:00:00Z", now, false)).toBe("overdue");
  });

  it("flags the three day window as due soon", () => {
    expect(dueDateStatus("2026-09-03T12:00:00Z", now, false)).toBe("due-soon");
    expect(dueDateStatus("2026-09-04T11:59:59Z", now, false)).toBe("due-soon");
  });

  it("leaves everything beyond the window on track", () => {
    expect(dueDateStatus("2026-09-04T12:00:01Z", now, false)).toBe("on-track");
  });

  it("prefers complete over every date bucket", () => {
    expect(dueDateStatus("2026-08-01T00:00:00Z", now, true)).toBe("complete");
  });

  it("maps every status to a human label", () => {
    for (const status of ["complete", "overdue", "due-soon", "on-track"] as const) {
      expect(statusLabels[status].length).toBeGreaterThan(0);
    }
  });
});

describe("formatDueDate", () => {
  it("formats a stable human readable date", () => {
    expect(formatDueDate("2026-09-12T17:00:00Z")).toBe("Sep 12, 2026");
  });
});
