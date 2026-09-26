/**
 * `true` answers the backend endpoints with `mockApiInterceptor` in memory (no backend needed).
 * `false` hits the real backend through `proxy.conf.mjs`.
 */
export const USE_MOCK_API = false;

/** How often a processing record is polled (ui-integration.md: every 2–3 s is plenty). */
export const STATUS_POLL_MS = 3000;
