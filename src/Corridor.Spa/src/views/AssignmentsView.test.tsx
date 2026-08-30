import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { AssignmentsApi } from "../api/client";
import type { Assignment } from "../domain/assignments";
import { AssignmentsView } from "./AssignmentsView";

function fakeAssignments(): Assignment[] {
  const day = 24 * 60 * 60 * 1000;
  const now = Date.now();
  return [
    {
      id: 1,
      inspectorUpn: "inspector@corridor.example",
      licenseeName: "Northgate Firearms",
      focus: "Acquisition record spot check",
      dueAt: new Date(now + 30 * day).toISOString(),
      checklist: [
        { item: "Review acquisition log", done: true },
        { item: "Verify serial numbers", done: false },
      ],
    },
    {
      id: 2,
      inspectorUpn: "inspector@corridor.example",
      licenseeName: "Harbor Optics",
      focus: "Import permit reconciliation",
      dueAt: new Date(now - 2 * day).toISOString(),
      checklist: [{ item: "Photograph inventory", done: false }],
    },
  ];
}

function apiWith(list: () => Assignment[] | Promise<Assignment[]>): AssignmentsApi {
  return {
    list: async () => list(),
    setChecklistItem: vi.fn(async () => undefined),
  };
}

const noop = () => undefined;

describe("AssignmentsView", () => {
  it("renders one card per assignment with licensee, focus, and progress", async () => {
    render(<AssignmentsView api={apiWith(fakeAssignments)} onNavigate={noop} />);

    expect(await screen.findByText("Northgate Firearms")).toBeInTheDocument();
    expect(screen.getByText("Acquisition record spot check")).toBeInTheDocument();
    expect(screen.getByText("Harbor Optics")).toBeInTheDocument();
    expect(screen.getByLabelText("1 of 2 checklist items complete")).toBeInTheDocument();
    expect(screen.getByRole("list")).toBeInTheDocument();
    expect(screen.getAllByRole("listitem")).toHaveLength(2);
  });

  it("labels each card with the due-date status pill", async () => {
    render(<AssignmentsView api={apiWith(fakeAssignments)} onNavigate={noop} />);

    await waitFor(() => expect(screen.getByText("On track")).toBeInTheDocument());
    expect(screen.getByText("Overdue")).toBeInTheDocument();
  });

  it("shows an inline error panel with retry when the API call fails, then recovers", async () => {
    let failing = true;
    const api = apiWith(() => {
      if (failing) {
        failing = false;
        throw new Error("portal unreachable");
      }
      return fakeAssignments();
    });
    const user = userEvent.setup();

    render(<AssignmentsView api={api} onNavigate={noop} />);

    const panel = await screen.findByRole("alert");
    expect(panel).toHaveTextContent("portal unreachable");
    expect(screen.queryByText("Northgate Firearms")).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Retry" }));

    expect(await screen.findByText("Northgate Firearms")).toBeInTheDocument();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("shows an empty-state notice when the inspector has no assignments", async () => {
    render(<AssignmentsView api={apiWith(() => [])} onNavigate={noop} />);

    const notice = await screen.findByText(
      /No assignments are assigned to your account/,
    );
    expect(notice).toBeInTheDocument();
  });
});
