import { describe, expect, it } from "vitest";
import { matchRoute } from "./router";

describe("matchRoute", () => {
  it("maps the root to the assignments list", () => {
    expect(matchRoute("/")).toEqual({ name: "assignments" });
  });

  it("extracts the numeric id from an assignment path", () => {
    expect(matchRoute("/assignment/12")).toEqual({ name: "assignment", id: 12 });
  });

  it("maps profile, callback, and everything else", () => {
    expect(matchRoute("/profile")).toEqual({ name: "profile" });
    expect(matchRoute("/callback")).toEqual({ name: "callback" });
    expect(matchRoute("/nowhere/fast")).toEqual({ name: "not-found" });
  });
});
