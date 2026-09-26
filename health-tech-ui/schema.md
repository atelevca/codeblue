# CodeBlue — API contract

12 backend methods. Speakers (`getSpeakers`), voice samples, speaker suggestions and the people directory live on the frontend (mocks and stubs), so they are not API calls.

| # | Method | Endpoint |
|---|---|---|
| 1 | uploadFile | `POST /files` |
| 2 | saveRecord | `POST /records` |
| 3 | processRecord | `POST /records/:id/process` |
| 4 | getRecord | `GET /records/:id` |
| 5 | getProcessingStatus | `GET /records/:id/status` |
| 6 | getMom | `GET /records/:id/mom?lang=` |
| 7 | speakerAssociation | `PUT /records/:id/speakers` |
| 8 | exportMom | `GET /records/:id/export?format=&lang=` |
| 9 | sendEmail | `POST /records/:id/email` |
| 10 | getRecords | `GET /records?filter=&q=&page=&limit=` |
| 11 | retryProcessing | `POST /records/:id/retry` |
| 12 | updateRecord | `PATCH /records/:id` |

---

## Common types

```ts
type ID = string;                  // "rsn_7k2p9"
type ISODate = string;             // "2026-09-26T10:14:00Z"
type Lang = "RO" | "RU" | "EN";
type RecordStatus = "pending" | "processing" | "completed" | "failed";
type ExportFormat = "DOCX" | "PDF" | "MD";
type ProcessingStage = "audio_analysis" | "speaker_identification" | "transcription" | "mom_drafting";
type DiscussionType = "medical" | "executive" | "administrative";

// any 4xx/5xx error
interface ApiError {
  code: string;      // machine-readable code
  message: string;   // user-facing text (RO)
}

// list row, also returned by saveRecord and updateRecord
interface RecordSummary {
  id: ID;
  title: string;
  fileName: string;
  sizeBytes: number;
  durationSec: number;
  speakersCount: number;
  discussionType: DiscussionType;
  momLang: Lang;
  status: RecordStatus;
  progress: number;          // 0–100
  createdAt: ISODate;
}

interface ProcessingStatus {
  recordId: ID;
  status: RecordStatus;
  progress: number;          // 0–100, overall
  stage?: ProcessingStage;   // current stage; for failed — the stage where it failed
  stageProgress?: number;    // 0–100 within the stage
  etaSec?: number;
  queuePosition?: number;    // pending only
  error?: ApiError;          // failed only
}
```

---

## 1. uploadFile

`POST /files` — `multipart/form-data`

**Payload**

| field | type | required | notes |
|---|---|---|---|
| file | File | yes | mp3, wav, m4a, flac, ogg, aac, aiff, opus; 1 byte – 500 MB |

**Response 201**

```ts
interface UploadFileResponse {
  fileId: ID;
  fileName: string;      // original name
  sizeBytes: number;
  format: string;        // "M4A"
  durationSec: number;
}
```

```json
{ "fileId": "fil_91ab2", "fileName": "Medpark_audio_2min.m4a", "sizeBytes": 2048000, "format": "M4A", "durationSec": 120 }
```

**Errors:** `415 UNSUPPORTED_FORMAT`, `413 FILE_TOO_LARGE`, `400 EMPTY_FILE`

The client tracks upload progress (upload progress events). The "Anulează" button simply aborts the request.

---

## 2. saveRecord

`POST /records` — JSON

**Payload**

```ts
interface SaveRecordRequest {
  fileId: ID;
  title: string;                   // 1–120 chars
  speakersCount: number;           // 1–8
  discussionType: DiscussionType;
  momLang: Lang;                   // language of the minutes
}
```

```json
{ "fileId": "fil_91ab2", "title": "Vizită ATI — 26 sept", "speakersCount": 4, "discussionType": "medical", "momLang": "RO" }
```

**Response 201:** `RecordSummary`, with `status: "pending"`, `progress: 0`

**Errors:** `404 FILE_NOT_FOUND`, `422 VALIDATION_ERROR` (plus a `field` with the name of the invalid field)

---

## 3. processRecord

`POST /records/:id/process` — no body

**Response 202:** `ProcessingStatus`

**Errors:** `404 RECORD_NOT_FOUND`, `409 ALREADY_PROCESSING`

---

## 4. getRecord

`GET /records/:id`

**Response 200**

```ts
interface RecordDetails extends RecordSummary {
  startedAt?: ISODate;
  completedAt?: ISODate;           // "Gata · Azi, 14:02"
  processingTimeSec?: number;      // "Timp de procesare"
  estimatedProcessingSec?: number; // "Timp estimat ~2 min"
  error?: ApiError;                // when status = failed
}
```

```json
{
  "id": "rsn_6s1ic", "title": "Vizită ATI de dimineață — 25 sep", "fileName": "Vizită ATI de dimineață — 25 sep.m4a",
  "sizeBytes": 24100000, "durationSec": 1520, "speakersCount": 4, "discussionType": "medical", "momLang": "RO",
  "status": "completed", "progress": 100, "createdAt": "2026-09-25T08:10:00Z",
  "startedAt": "2026-09-25T08:10:05Z", "completedAt": "2026-09-25T08:11:27Z", "processingTimeSec": 82
}
```

**Errors:** `404 RECORD_NOT_FOUND`

---

## 5. getProcessingStatus

`GET /records/:id/status` — poll every 5 s until the status is `completed` or `failed`

**Response 200:** `ProcessingStatus`

```json
{ "recordId": "rsn_7k2p9", "status": "processing", "progress": 58, "stage": "speaker_identification", "stageProgress": 94, "etaSec": 70 }
```

```json
{ "recordId": "rsn_6v0ze", "status": "failed", "progress": 41, "stage": "speaker_identification",
  "error": { "code": "SPEAKERS_OVERLAP", "message": "Vocile se suprapun în cea mai mare parte a înregistrării…" } }
```

**Errors:** `404 RECORD_NOT_FOUND`

---

## 6. getMom

`GET /records/:id/mom?lang=RU`

| query | type | required |
|---|---|---|
| lang | Lang | no, defaults to the record's `momLang` |

**Response 200** — the minutes are ready

```ts
interface Mom {
  recordId: ID;
  lang: Lang;
  discussionType: DiscussionType;
  generatedAt: ISODate;
  sectionTitles: {                 // depend on the discussion type
    summary: string; decisions: string; actions: string; topics: string;
  };
  summary: string;
  decisions: string[];
  actionItems: {
    task: string;
    ownerSpeakerIndex: number;     // 0-based; the frontend takes the name from the speaker association
    due: string;                   // "Oct 3", "12:00", "Today"
  }[];
  topics: {
    startSec: number;              // timestamp
    title: string;
    note: string;
  }[];
}
```

```json
{
  "recordId": "rsn_6s1ic", "lang": "RO", "discussionType": "medical", "generatedAt": "2026-09-25T08:11:27Z",
  "sectionTitles": { "summary": "Situația secției", "decisions": "Decizii clinice", "actions": "Indicații și sarcini", "topics": "Pacienți pe paturi" },
  "summary": "Vizita de dimineață în ATI, 8 din 10 paturi ocupate…",
  "decisions": ["Patul 3: începerea sevrajului de la ventilația mecanică"],
  "actionItems": [{ "task": "Lactat și gazometrie la patul 5 la fiecare 4 ore", "ownerSpeakerIndex": 1, "due": "12:00" }],
  "topics": [{ "startSec": 45, "title": "Patul 5 — șoc septic, ziua 2", "note": "MAP 62 pe norepinefrină…" }]
}
```

**Response 202** — no minutes in this language yet; generation has started and the frontend retries the request (UI shows "Se traduce în…")

```json
{ "recordId": "rsn_6s1ic", "lang": "RU", "status": "generating" }
```

**Errors:** `404 RECORD_NOT_FOUND`, `409 RECORD_NOT_COMPLETED`

---

## 7. speakerAssociation

`PUT /records/:id/speakers` — replaces all associations. The people directory lives on the frontend, so the backend receives the person's details, not a `personId`.

**Payload**

```ts
interface SpeakerAssociationRequest {
  speakers: {
    speakerIndex: number;          // 0-based
    name: string;                  // "Dr. Ana Popescu" or the guest's name
    email?: string;                // guests have none
    role?: string;                 // "Cardiolog · Medicină internă"
    isGuest: boolean;              // "participant extern"
  }[];                             // unassociated speakers are simply omitted
}
```

```json
{ "speakers": [
  { "speakerIndex": 0, "name": "Dr. Mihai Ionescu", "email": "mihai.ionescu@spitalul-clinic.ro", "role": "Șef secție ATI", "isGuest": false },
  { "speakerIndex": 2, "name": "Ion Rotaru", "isGuest": true }
] }
```

**Response 200:** the same `speakers` array (the saved state)

**Errors:** `404 RECORD_NOT_FOUND`, `422 INVALID_SPEAKER_INDEX` (index ≥ `speakersCount`)

The minutes don't need to be regenerated afterwards, because action items store `ownerSpeakerIndex`. The backend stores the names because export (8) and email (9) need them.

---

## 8. exportMom

`GET /records/:id/export?format=PDF&lang=RO`

| query | type | required |
|---|---|---|
| format | ExportFormat | yes |
| lang | Lang | no, defaults to `momLang` |

**Response 200:** binary file

- `Content-Type`: `application/pdf` / `application/vnd.openxmlformats-officedocument.wordprocessingml.document` / `text/markdown`
- `Content-Disposition: attachment; filename="<title> — proces-verbal.pdf"`

**Errors:** `404 RECORD_NOT_FOUND`, `409 MOM_NOT_READY` (no minutes in this language yet — call 6 first)

---

## 9. sendEmail

`POST /records/:id/email` — JSON

**Payload**

```ts
interface SendEmailRequest {
  to: string[];                    // ≥ 1 valid addresses
  subject: string;                 // defaults to "Proces-verbal: <title>"
  message?: string;
  attachmentFormat: ExportFormat;
  lang: Lang;
  includeInline: boolean;          // "Include în corpul e-mailului"
}
```

```json
{ "to": ["mihai.ionescu@spitalul-clinic.ro", "sef@medpark.md"], "subject": "Proces-verbal: Vizită ATI — 26 sept",
  "message": "", "attachmentFormat": "DOCX", "lang": "RO", "includeInline": true }
```

**Response 200**

```ts
interface SendEmailResponse {
  sentTo: string[];
  failed?: { email: string; reason: string }[];
}
```

**Errors:** `422 INVALID_RECIPIENTS`, `409 MOM_NOT_READY`

---

## 10. getRecords

`GET /records?filter=active&q=ati&page=1&limit=20`

| query | type | required | notes |
|---|---|---|---|
| filter | `"all" \| "active" \| "completed" \| "failed"` | no | `active` = pending + processing |
| q | string | no | search by title and file name |
| page, limit | number | no | |

The same call with `filter=active` feeds the "În curs" block in the sidebar and the "ready" toasts. Poll it every 5 s while the list is not empty.

**Response 200**

```ts
interface ListRecordsResponse {
  items: RecordSummary[];          // sorted by createdAt desc
  total: number;                   // for the current filter
  counts: { all: number; active: number; completed: number; failed: number }; // tab and sidebar counters
}
```

---

## 11. retryProcessing

`POST /records/:id/retry` — no body

**Response 202:** `ProcessingStatus`, with `status: "pending"`, `progress: 0`

**Errors:** `404 RECORD_NOT_FOUND`, `409 NOT_FAILED` (only a failed record can be retried)

---

## 12. updateRecord

`PATCH /records/:id` — JSON

**Payload**

```ts
interface UpdateRecordRequest { title: string; }   // 1–120 chars
```

**Response 200:** `RecordSummary`

**Errors:** `404 RECORD_NOT_FOUND`, `422 VALIDATION_ERROR`

---

## Frontend-only (not API calls)

| What | Where it lives |
|---|---|
| getSpeakers (talk time, %, sample quote) | UI mock |
| Speaker voice sample (▶) | stub |
| Speaker suggestions ("Sugestie · 87% match") | stub |
| People directory / search | UI |
| Discussion types, languages, formats, limits | hardcoded on UI |
| "Copiază ca text" | built on the client from `Mom` |
| Cancel upload | client-side abort |

## Open question

If methods 2 and 3 always run back to back (one "Începe procesarea" button in the design), you can add an `autoProcess: true` flag to `SaveRecordRequest` and save one request.
