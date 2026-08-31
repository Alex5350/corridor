import { config } from "../config";
import type { Assignment } from "../domain/assignments";
import { toApiError, toNetworkError } from "./problem";

/**
 * Thin fetch wrapper for the portal REST API. Adds the okta-sim access token
 * as a bearer header and turns failures (HTTP errors and network faults)
 * into ApiError so views render one consistent inline error shape.
 *
 * A 401 is different: the access token is expired, revoked, or unknown, so
 * the session is dead. Rather than surfacing a dead-end inline error, the
 * caller's onUnauthorized hook fires (the app wires it to the auth session
 * reset) and the user lands back on the login gate with a reason.
 */

export interface AssignmentsApi {
  list(): Promise<Assignment[]>;
  setChecklistItem(assignmentId: number, itemIndex: number, done: boolean): Promise<void>;
}

/** Reads the current access token lazily so silent renew is always picked up. */
export type TokenSource = () => string | null;

/** Fired once per 401 response: the session must be reset, not retried inline. */
export type UnauthorizedHandler = () => void;

export interface CreateApiOptions {
  /** Fetch implementation; tests inject a fake, production uses the global. */
  fetchFn?: typeof fetch;
  /** Session reset hook invoked when the portal answers 401. */
  onUnauthorized?: UnauthorizedHandler;
}

export function createApi(
  getToken: TokenSource,
  { fetchFn = fetch, onUnauthorized }: CreateApiOptions = {},
): AssignmentsApi {
  async function request(path: string, init: RequestInit): Promise<Response> {
    const token = getToken();
    const headers = new Headers(init.headers);
    if (init.body) {
      headers.set("content-type", "application/json");
    }
    if (token) {
      headers.set("authorization", `Bearer ${token}`);
    }
    let response: Response;
    try {
      response = await fetchFn(`${config.portalApi}${path}`, { ...init, headers });
    } catch (cause) {
      throw toNetworkError(cause);
    }
    if (!response.ok) {
      if (response.status === 401) {
        onUnauthorized?.();
      }
      throw await toApiError(response);
    }
    return response;
  }

  return {
    async list(): Promise<Assignment[]> {
      const response = await request("/api/assignments", { method: "GET" });
      return (await response.json()) as Assignment[];
    },

    async setChecklistItem(assignmentId: number, itemIndex: number, done: boolean) {
      await request(`/api/assignments/${assignmentId}`, {
        method: "PATCH",
        body: JSON.stringify({ itemIndex, done }),
      });
    },
  };
}
