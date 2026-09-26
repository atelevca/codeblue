# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

Angular 22 frontend ("Resona") for the HealthTech hackathon. It sits next to the ASP.NET Core backend in the parent folder (`../HealthTech`, see `../CLAUDE.md` and `../FLOW.md`). Standalone components + signals, zoneless. Implements the "Resona v2" design's create → process → minutes (MoM) flow; the design's History (Istoric) view and glossary are intentionally not built.

- Routes (`src/app/app.routes.ts`): `/new` (upload + configure, `pages/new-record/`) and `/rec/:id` (`pages/record/record-page.ts`, shows `processing-view` until `Completed`, then `mom-view` with `speaker-panel` and `email-dialog`). App routes avoid `/profiles`, `/files`, `/jobs`, `/document` and `/audio` because those paths are proxied to the API.
- `src/app/api/`: `models.ts` (backend types), `resona-api.ts` (one method per backend endpoint; `toApiError` maps ProblemDetails `title` to RO text), `mock-api.interceptor.ts` (in-memory backend, on while `USE_MOCK_API` in `api.config.ts` is `true`; a title containing "eșec"/"fail" simulates a failure at diarization). HttpClient uses `withXhr()` because fetch can't report upload progress. `proxy.conf.mjs` forwards the API paths to `API_URL` (default `http://localhost:5089`; the VS `https` profile needs `API_URL=https://localhost:7059`, since 5089 then only redirects).
- `src/app/state/`: root services — `NewRecordStore` (profiles, upload/config state, survives navigation), `ProcessingTracker` (polls `GET /jobs/{id}` every 3 s; picks up running jobs from `GET /jobs` at start; feeds the "În curs" sidebar and ready/failed toasts), `MinutesStore` (minutes per record; generation is one 6–10 min request, kept here so navigation does not abort it), `SpeakerMap` (speaker ↔ person links, client-only), `Toasts`, `Breadcrumb`.
- `src/app/shared/`: `catalog.ts` (frontend-only data: RO labels for profile keys, stages as `percent` bands, people directory, colors), `format.ts`, `quill.ts` (read-only rendering of the saved Quill delta), `speaker-stats.ts` (talk time/quotes from the transcript; suggestions are a stub).
- Styles: tokens and shared classes in `src/styles.scss`; component `.scss` files must stay under the 4 kB budget, so move shared rules to the global file.

## Commands

```sh
npm install
npm start            # ng serve, development config, http://localhost:4200
npm run build        # production build to dist/ (budgets: 500kB warn / 1MB error initial, 4kB/8kB per component style)
npm run watch        # dev build in watch mode
npm test             # ng test (Vitest + jsdom via @angular/build:unit-test)
npx prettier --write "src/**/*.{ts,html,scss}"
```

- Formatting: Prettier (`.prettierrc`: printWidth 100, single quotes, Angular parser for `.html`). No ESLint is configured.
- Component schematics default to SCSS; selector prefix is `app`.
- The parent project forbids creating tests ("Do NOT create any tests" in `../CLAUDE.md`). The CLI-generated `src/app/app.spec.ts` exists, but do not add new spec files.

## API contract

`../docs/ui-integration.md` is the contract; controllers are in `../HealthTech/Controllers`. Flow: `GET /profiles` → `POST /files` (multipart, probed, nothing starts) → `POST /jobs` (record card; this starts processing, `fileId` = job id) → poll `GET /jobs/{id}` (`status` `Pending`/`Running`/`Completed`/`Failed`, `percent`, `currentStep` in RU) → `GET /jobs/{id}/result` (transcript with `Speaker N` labels) → `POST /document/save/{id}` without body generates the minutes (6–10 min, no progress) → `GET /document/get/{id}` / `GET /document/downloadpdf/{id}`.

- Backend: `dotnet run --project HealthTech --launch-profile http` from the parent folder.
- Errors are RFC 7807; show RO text by `title`, never `detail` verbatim (may contain server paths) — except `Document error`, whose `detail` is RO.
- Not in the backend, so frontend-only: the people directory and speaker ↔ person binding (not persisted), speaker suggestions, voice samples, e-mail (opens `mailto:`), "Copiază ca text", upload cancel. No MoM language choice, rename, retry of a failed job, or export formats other than PDF.
- `speakersCount` is informational only; the speaker list shown after processing comes from the transcript.
- User-facing text is Romanian.
