import { describe, expect, it, vi } from "vitest";
import { createApi } from "./client";
import { ApiError } from "./problem";

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json" },
  });
}

const assignment = {
  id: 1,
  inspectorUpn: "inspector@corridor.example",
  licenseeName: "Harbor Optics",
  focus: "Serial number reconciliation",
  dueAt: "2026-09-12T12:00:00Z",
  checklist: [{ item: "Review acquisition log", done: false }],
};

describe("createApi", () => {
  it("sends the bearer token and parses the assignment list", async () => {
    const fetchMock = vi.fn(async () => jsonResponse([assignment]));
    const api = createApi(() => "token-123", { fetchFn: fetchMock as unknown as typeof fetch });

    const list = await api.list();

    expect(list).toEqual([assignment]);
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("http://localhost:5200/api/assignments");
    expect(new Headers(init.headers).get("authorization")).toBe("Bearer token-123");
  });

  it("PATCHes itemIndex/done as JSON and tolerates the 204", async () => {
    const fetchMock = vi.fn(async () => new Response(null, { status: 204 }));
    const api = createApi(() => "token-123", { fetchFn: fetchMock as unknown as typeof fetch });

    await api.setChecklistItem(3, 0, true);

    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("http://localhost:5200/api/assignments/3");
    expect(init.method).toBe("PATCH");
    expect(new Headers(init.headers).get("content-type")).toBe("application/json");
    expect(JSON.parse(String(init.body))).toEqual({ itemIndex: 0, done: true });
  });

  it("throws an ApiError parsed from a problem+json failure", async () => {
    const fetchMock = vi.fn(
      async () =>
        new Response(
          JSON.stringify({ title: "Item out of range", detail: "itemIndex 4" }),
          { status: 422, headers: { "content-type": "application/problem+json" } },
        ),
    );
    const api = createApi(() => "token-123", { fetchFn: fetchMock as unknown as typeof fetch });

    const error = await api.setChecklistItem(3, 4, true).catch((cause: unknown) => cause);

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(422);
    expect((error as ApiError).title).toBe("Item out of range");
  });

  it("resets the session through onUnauthorized when the portal answers 401", async () => {
    const fetchMock = vi.fn(
      async () =>
        new Response(JSON.stringify({ title: "Not authorized", detail: "expired token" }), {
          status: 401,
          headers: { "content-type": "application/problem+json" },
        }),
    );
    const onUnauthorized = vi.fn();
    const api = createApi(() => "stale-token", {
      fetchFn: fetchMock as unknown as typeof fetch,
      onUnauthorized,
    });

    const error = await api.list().catch((cause: unknown) => cause);

    expect(onUnauthorized).toHaveBeenCalledTimes(1);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(401);
  });

  it("does not reset the session on non-401 failures", async () => {
    const fetchMock = vi.fn(async () => jsonResponse({ title: "Boom" }, 500));
    const onUnauthorized = vi.fn();
    const api = createApi(() => "token-123", {
      fetchFn: fetchMock as unknown as typeof fetch,
      onUnauthorized,
    });

    await api.list().catch(() => undefined);

    expect(onUnauthorized).not.toHaveBeenCalled();
  });

  it("wraps network faults as a status zero ApiError", async () => {
    const fetchMock = vi.fn(async () => {
      throw new TypeError("fetch failed");
    });
    const api = createApi(() => null, { fetchFn: fetchMock as unknown as typeof fetch });

    const error = await api.list().catch((cause: unknown) => cause);

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(0);
    expect((error as ApiError).title).toBe("Could not reach the portal API");
  });
});
