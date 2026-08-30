import { describe, expect, it } from "vitest";
import { ApiError, describeError, toApiError, toNetworkError } from "./problem";

function problemResponse(body: unknown, init?: ResponseInit): Response {
  return new Response(typeof body === "string" ? body : JSON.stringify(body), {
    status: 400,
    headers: { "content-type": "application/problem+json" },
    ...init,
  });
}

describe("toApiError (RFC 9457 problem+json)", () => {
  it("maps the reserved problem fields onto the ApiError", async () => {
    const response = problemResponse({
      type: "https://corridor.example/problems/validation",
      title: "Checklist item out of range",
      status: 422,
      detail: "itemIndex 9 is beyond the checklist length.",
      instance: "/api/assignments/3",
      traceId: "corridor-004211",
    });
    const error = await toApiError(response);
    expect(error).toBeInstanceOf(ApiError);
    expect(error.status).toBe(422);
    expect(error.title).toBe("Checklist item out of range");
    expect(error.detail).toBe("itemIndex 9 is beyond the checklist length.");
    expect(error.type).toBe("https://corridor.example/problems/validation");
    expect(error.instance).toBe("/api/assignments/3");
    expect(error.extensions).toEqual({ traceId: "corridor-004211" });
  });

  it("falls back to a status-derived title when the body is sparse", async () => {
    const response = problemResponse({}, { status: 404 });
    const error = await toApiError(response);
    expect(error.status).toBe(404);
    expect(error.title).toBe("Not found");
    expect(error.detail).toBeNull();
  });

  it("handles non-JSON bodies by using the status text", async () => {
    const response = new Response("<html>boom</html>", {
      status: 502,
      statusText: "Bad Gateway",
      headers: { "content-type": "text/html" },
    });
    const error = await toApiError(response);
    expect(error.status).toBe(502);
    expect(error.title).toBe("Request failed (HTTP 502)");
    expect(error.detail).toBe("Bad Gateway");
  });

  it("handles JSON bodies that are not valid despite the content type", async () => {
    const response = problemResponse("{not json", { status: 500 });
    const error = await toApiError(response);
    expect(error.status).toBe(500);
    expect(error.title).toBe("Request failed (HTTP 500)");
  });
});

describe("toNetworkError and describeError", () => {
  it("wraps fetch rejections with a reachable-portal message", () => {
    const error = toNetworkError(new TypeError("fetch failed"));
    expect(error.status).toBe(0);
    expect(error.title).toBe("Could not reach the portal API");
  });

  it("renders ApiError as title plus detail for inline display", () => {
    const error = new ApiError({ status: 422, title: "Invalid item", detail: "index 9" });
    expect(describeError(error)).toBe("Invalid item: index 9");
    expect(describeError("boom")).toBe("boom");
  });
});
