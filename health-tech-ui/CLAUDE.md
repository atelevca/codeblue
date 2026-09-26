# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

Angular 22 frontend ("Resona") for the HealthTech hackathon. It sits next to the ASP.NET Core backend in the parent folder (`../HealthTech`, see `../CLAUDE.md` and `../FLOW.md`). Standalone components + signals, zoneless. Implements the "Resona v2" design's create → process → minutes (MoM) flow; the design's History (Istoric) view and glossary are intentionally not built.

- Routes (`src/app/app.routes.ts`): `/new` (upload + configure, `pages/new-record/`) and `/rec/:id` (`pages/record/record-page.ts`, shows `processing-view` until `completed`, then `mom-view` with `speaker-panel` and `email-dialog`). App routes avoid `/files` and `/records` because those paths are proxied to the API.
- `src/app/api/`: `models.ts` (types from `schema.md`), `resona-api.ts` (one method per endpoint; `getMomWhenReady` retries on 202; `toApiError` normalizes errors), `mock-api.interceptor.ts` (in-memory backend, on while `USE_MOCK_API` in `api.config.ts` is `true`; a title containing "eșec"/"fail" simulates a failure at speaker identification). HttpClient uses `withXhr()` because fetch can't report upload progress. `proxy.conf.json` forwards `/files` and `/records` to `http://localhost:5089`.
- `src/app/state/`: root services — `NewRecordStore` (upload/config state, survives navigation), `ProcessingTracker` (polls status every 5 s for records started this session; feeds the "În curs" sidebar and ready/failed toasts), `SpeakerMap` (speaker ↔ person links, PUT on each change), `Toasts`, `Breadcrumb`.
- `src/app/shared/`: `catalog.ts` (frontend-only data: discussion types, languages, stages, people directory, colors), `format.ts`, `speaker-stats.ts` (mocked talk time/quotes/suggestions).
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

`schema.md` is the agreed contract between this UI and the backend: 12 endpoints under `/files` and `/records` (upload → save record → process → poll status every 5 s → get minutes-of-meeting (`Mom`) per language → speaker association → export/email), plus the shared types (`RecordSummary`, `ProcessingStatus`, `Mom`, `ApiError`, `DiscussionType`, `Lang` = `RO`/`RU`/`EN`, ...). Build API types and services from it.

- These endpoints are **not implemented in the backend yet** — it currently only exposes `/audio/*` endpoints (`http://localhost:5089` via `dotnet run --project HealthTech --launch-profile http` from the parent folder).
- Frontend-only by design (mocks/stubs, never API calls): speakers list with talk time, voice samples, speaker suggestions, the people directory, discussion types/languages/formats/limits, "Copiază ca text", and upload cancel (abort the request).
- User-facing text is Romanian; `ApiError.message` is already user-facing RO text.
- `getMom` returns 202 `{ status: "generating" }` while a translation is being produced — the UI retries. Action items reference speakers by `ownerSpeakerIndex` (0-based) and the name comes from the speaker association.
