/**
 * TypeScript re-export of the okta-sim browser shim (see cors-shim.mjs for the
 * rationale). The implementation lives in plain JavaScript so the standalone
 * screenshot runner can share it.
 */
export { allowOktaCrossOrigin } from "./cors-shim.mjs";
