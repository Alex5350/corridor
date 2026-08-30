/**
 * RFC 9457 problem details parsing for portal API failures. Errors surface
 * as ApiError instances with the problem's fields so views can render them
 * inline (title plus detail) without knowing anything about HTTP.
 */

const problemContentType = "application/problem+json";

export class ApiError extends Error {
  readonly status: number;
  readonly title: string;
  readonly detail: string | null;
  /** The problem's "type" URI, when the portal sends one. */
  readonly type: string | null;
  /** The problem's "instance" URI, when the portal sends one. */
  readonly instance: string | null;
  /** Any extension members beyond the reserved fields. */
  readonly extensions: Record<string, string>;

  constructor(init: {
    status: number;
    title: string;
    detail?: string | null;
    type?: string | null;
    instance?: string | null;
    extensions?: Record<string, string>;
  }) {
    super(init.detail ? `${init.title}: ${init.detail}` : init.title);
    this.name = "ApiError";
    this.status = init.status;
    this.title = init.title;
    this.detail = init.detail ?? null;
    this.type = init.type ?? null;
    this.instance = init.instance ?? null;
    this.extensions = init.extensions ?? {};
  }
}

const reservedProblemFields = new Set([
  "type",
  "title",
  "status",
  "detail",
  "instance",
]);

interface ProblemShape {
  type?: unknown;
  title?: unknown;
  status?: unknown;
  detail?: unknown;
  instance?: unknown;
  [key: string]: unknown;
}

/**
 * Turn a failed response into an ApiError. Handles three body shapes:
 * application/problem+json (the contract), plain JSON with an error field,
 * and non-JSON bodies (falls back to the HTTP status text).
 */
export async function toApiError(response: Response): Promise<ApiError> {
  const status = response.status;
  const contentType = response.headers.get("content-type") ?? "";

  if (contentType.includes(problemContentType) || contentType.includes("application/json")) {
    try {
      const body = (await response.json()) as ProblemShape;
      const extensions: Record<string, string> = {};
      for (const [key, value] of Object.entries(body)) {
        if (!reservedProblemFields.has(key) && value !== null && value !== undefined) {
          extensions[key] = String(value);
        }
      }
      return new ApiError({
        status: typeof body.status === "number" ? body.status : status,
        title: typeof body.title === "string" && body.title ? body.title : fallbackTitle(status),
        detail: typeof body.detail === "string" ? body.detail : null,
        type: typeof body.type === "string" ? body.type : null,
        instance: typeof body.instance === "string" ? body.instance : null,
        extensions,
      });
    } catch {
      // Body was not valid JSON despite the content type: fall through.
    }
  }

  return new ApiError({
    status,
    title: fallbackTitle(status),
    detail: response.statusText || null,
  });
}

function fallbackTitle(status: number): string {
  if (status === 401) {
    return "Not authorized";
  }
  if (status === 404) {
    return "Not found";
  }
  return `Request failed (HTTP ${status})`;
}

/** Wrap network-layer failures (fetch rejected) in an ApiError too. */
export function toNetworkError(cause: unknown): ApiError {
  const message = cause instanceof Error ? cause.message : String(cause);
  return new ApiError({
    status: 0,
    title: "Could not reach the portal API",
    detail: message,
  });
}

/** Render an unknown thrown value for inline display. */
export function describeError(cause: unknown): string {
  if (cause instanceof ApiError) {
    return cause.detail ? `${cause.title}: ${cause.detail}` : cause.title;
  }
  if (cause instanceof Error) {
    return cause.message;
  }
  return String(cause);
}
