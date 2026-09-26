/**
 * The backend does not implement `/files` and `/records` yet (see CLAUDE.md), so requests are
 * answered by `mockApiInterceptor` in memory. Set to `false` to hit the real backend through
 * `proxy.conf.json`.
 */
export const USE_MOCK_API = true;

/** How often processing status is polled (schema.md: every 5 s). */
export const STATUS_POLL_MS = 5000;
