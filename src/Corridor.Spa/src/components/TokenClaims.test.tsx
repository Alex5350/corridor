import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { TokenClaims, decodeJwt } from "./TokenClaims";

function segment(value: unknown): string {
  return btoa(JSON.stringify(value)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

/** A fake ID token with the shape okta-sim issues (RS256, kid, upn claims). */
const fakeIdToken = [
  segment({ alg: "RS256", typ: "JWT", kid: "okta-sim-2026-08" }),
  segment({
    iss: "http://localhost:8080",
    sub: "u-17",
    upn: "inspector@corridor.example",
    name: "Inspecting Iverson",
    role: "Inspector",
    groups: ["Inspectors", "FieldStaff"],
    auth_time: 1793647200,
  }),
  "signature-segment-not-decoded",
].join(".");

describe("decodeJwt", () => {
  it("decodes header and payload without the signature", () => {
    const decoded = decodeJwt(fakeIdToken);
    expect(decoded.header).toEqual({ alg: "RS256", typ: "JWT", kid: "okta-sim-2026-08" });
    expect(decoded.payload.upn).toBe("inspector@corridor.example");
    expect(decoded.payload.role).toBe("Inspector");
  });

  it("rejects tokens without decodable segments", () => {
    expect(() => decodeJwt("only-one-segment")).toThrow(/decodable JWT/);
  });
});

describe("TokenClaims pane", () => {
  it("keeps the claims hidden until the pane is expanded", async () => {
    const user = userEvent.setup();
    render(<TokenClaims token={fakeIdToken} />);

    const toggle = screen.getByRole("button", { name: /ID token claims/ });
    expect(toggle).toHaveAttribute("aria-expanded", "false");
    expect(screen.getByText("inspector@corridor.example")).not.toBeVisible();

    await user.click(toggle);

    expect(toggle).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByText("inspector@corridor.example")).toBeVisible();
  });

  it("renders header and payload claims as definition lists", async () => {
    const user = userEvent.setup();
    render(<TokenClaims token={fakeIdToken} />);
    await user.click(screen.getByRole("button", { name: /ID token claims/ }));

    const pane = screen
      .getByText("Payload claims", { selector: "h4" })
      .closest(".claims-body") as HTMLElement;
    expect(pane).toHaveTextContent("okta-sim-2026-08");
    expect(pane).toHaveTextContent("Inspectors, FieldStaff");
    expect(pane).toHaveTextContent("1793647200 (");

    const header = screen.getByText("Header", { selector: "h4" });
    expect(header.closest("div")).toHaveTextContent("RS256");
  });
});
