// Types of the backend API (../docs/ui-integration.md, controllers in ../HealthTech/Controllers).
// All JSON is camelCase, ids are GUIDs, enums come as strings.

export type ID = string;
export type ISODate = string;

/** `GET /profiles` — one entry of the "discussion type" dropdown. `key` goes to `discussionType`. */
export interface Profile {
  key: string;
  displayName: string;
}

/** `POST /files` response. `fileId` is also the id of the record (job) created from it. */
export interface UploadedFile {
  fileId: ID;
  fileName: string;
  sizeBytes: number;
  /** ffprobe's container synonyms as is, e.g. `mov,mp4,m4a,3gp,3g2,mj2`. */
  format: string;
  /** Fractional seconds; 0 when the container did not report a duration. */
  durationSec: number;
}

/** `POST /jobs` body — saves the record card and starts processing. */
export interface SaveRecordRequest {
  fileId: ID;
  /** Blank → the file name is used. */
  title?: string | null;
  /** 1..20 or null. Stored only: diarization decides the speaker count itself. */
  speakersCount?: number | null;
  /** One of `Profile.key`. */
  discussionType: string;
}

/** `Uploaded` rows are files whose card was not saved yet; `GET /jobs` hides them. */
export type JobStatus = 'Uploaded' | 'Pending' | 'Running' | 'Completed' | 'Failed';

/** `GET /jobs/{id}` — the record card plus processing progress. */
export interface Job {
  id: ID;
  fileName: string;
  profileKey: string;
  status: JobStatus;
  /** Human-readable step (RU), e.g. `Распознавание: чанк 3 из 7`. */
  currentStep: string | null;
  /** 0..100, never decreases. */
  percent: number;
  workflowId: string | null;
  error: string | null;
  createdAt: ISODate;
  completedAt: ISODate | null;
  sizeBytes: number;
  format: string | null;
  durationSec: number | null;
  title: string | null;
  speakersCount: number | null;
}

export interface LowConfidenceWord {
  /** Character offset inside the turn's `text`. */
  at: number;
  word: string;
  p: number;
}

export interface TranscriptTurn {
  start: number;
  end: number;
  /** `mm:ss`, minutes are not folded into hours. */
  startTime: string;
  endTime: string;
  /** `Speaker 1`, `Speaker 2`, ... */
  speaker: string;
  text: string;
  lowConfidence?: LowConfidenceWord[];
}

/** `GET /jobs/{id}/result` — the transcript with speakers (404 until `Completed`). */
export interface TranscriptResult {
  fileName: string;
  durationSeconds: number;
  /** Labels in order of first appearance; index = speaker index in the UI. */
  speakers: string[];
  turns: TranscriptTurn[];
  text: string;
  transcriptionMs?: number;
  diarizationMs?: number;
}

export interface QuillOp {
  insert: string;
  attributes?: Record<string, unknown>;
}

/** A full `quill.getContents()` snapshot (never an incremental delta). */
export interface QuillDelta {
  ops: QuillOp[];
}

export type FindingKind = 'Unsupported' | 'Omission' | 'Contradiction';

export interface VerificationFinding {
  kind: FindingKind;
  description: string;
  /** null for `Unsupported`. */
  transcriptQuote: string | null;
  /** null for `Omission`. */
  documentQuote: string | null;
  suggestedCorrection: string;
}

/**
 * completed + isConsistent → checked, no discrepancies; completed only → discrepancies in
 * `findings`; neither → the check failed, the document is saved unverified.
 */
export interface MinutesVerification {
  summary: string;
  findings: VerificationFinding[];
  completed: boolean;
  isConsistent: boolean;
}

/** `POST /document/save/{jobId}` and `GET /document/get/{jobId}`. */
export interface SavedDocument {
  jobId: ID;
  minutesMarkdown: string;
  verification: MinutesVerification;
  savedAt: ISODate;
  delta: QuillDelta;
}

/** `POST /audio/correctTranscript` (service endpoint, not used by the regular flow). */
export interface TranscriptCorrectionResult {
  sourceFile: string;
  correctedFile: string;
  reportFile: string;
  segmentCount: number;
  accepted: number;
  rejected: number;
  changes: unknown[];
}

/** RFC 7807 body of every backend error. `detail` may contain server paths. */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  traceId?: string;
}

/** Error normalized by `toApiError`; `message` is user-facing RO text. */
export interface ApiError {
  /** ProblemDetails `title` (`NotAudio`, `UnknownProfile`, ...), or `NETWORK` / the HTTP status. */
  code: string;
  message: string;
  status?: number;
  traceId?: string;
}
