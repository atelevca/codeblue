import { HttpClient, HttpErrorResponse, HttpEventType, HttpResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, filter, map } from 'rxjs';
import {
  ApiError,
  ID,
  Job,
  ProblemDetails,
  Profile,
  QuillDelta,
  SaveRecordRequest,
  SavedDocument,
  TranscriptCorrectionResult,
  TranscriptResult,
  UploadedFile,
} from './models';

export type UploadEvent =
  { kind: 'progress'; loaded: number; total: number } | { kind: 'done'; file: UploadedFile };

export interface ExportedFile {
  blob: Blob;
  fileName: string | null;
}

/** One method per backend endpoint (../docs/ui-integration.md). */
@Injectable({ providedIn: 'root' })
export class CodeBlueApi {
  private readonly http = inject(HttpClient);

  /** GET /profiles — the discussion types for the upload form. */
  getProfiles(): Observable<Profile[]> {
    return this.http.get<Profile[]>('/profiles');
  }

  /** POST /files — emits upload progress, then the probed file. Unsubscribe to cancel. */
  uploadFile(file: File): Observable<UploadEvent> {
    const body = new FormData();
    body.append('file', file, file.name);
    return this.http
      .post<UploadedFile>('/files', body, { reportProgress: true, observe: 'events' })
      .pipe(
        map((e): UploadEvent | null => {
          if (e.type === HttpEventType.UploadProgress)
            return { kind: 'progress', loaded: e.loaded, total: e.total ?? file.size };
          if (e.type === HttpEventType.Response) return { kind: 'done', file: e.body! };
          return null;
        }),
        filter((e): e is UploadEvent => e !== null),
      );
  }

  /** POST /jobs — saves the record card and starts processing. */
  saveRecord(req: SaveRecordRequest): Observable<Job> {
    return this.http.post<Job>('/jobs', req);
  }

  /** GET /jobs — all records, newest first (without `Uploaded`). */
  listJobs(): Observable<Job[]> {
    return this.http.get<Job[]>('/jobs');
  }

  /** GET /jobs/{id} — card and progress; poll it while the record is processed. */
  getJob(id: ID): Observable<Job> {
    return this.http.get<Job>(`/jobs/${id}`);
  }

  /** GET /jobs/{id}/result — the transcript with speakers (404 until `Completed`). */
  getResult(id: ID): Observable<TranscriptResult> {
    return this.http.get<TranscriptResult>(`/jobs/${id}/result`);
  }

  /** POST /document/save/{id} without a body — generates the minutes. Takes 6–10 minutes. */
  generateDocument(id: ID): Observable<SavedDocument> {
    return this.http.post<SavedDocument>(`/document/save/${id}`, null);
  }

  /** POST /document/save/{id} with a full Quill snapshot — verifies and saves an edit. Slow too. */
  saveDocument(id: ID, delta: QuillDelta): Observable<SavedDocument> {
    return this.http.post<SavedDocument>(`/document/save/${id}`, { delta });
  }

  /** GET /document/get/{id} — the saved minutes (404 if never generated). */
  getDocument(id: ID): Observable<SavedDocument> {
    return this.http.get<SavedDocument>(`/document/get/${id}`);
  }

  /** GET /document/downloadpdf/{id} — the saved minutes as PDF. */
  downloadPdf(id: ID): Observable<ExportedFile> {
    return this.http
      .get(`/document/downloadpdf/${id}`, { responseType: 'blob', observe: 'response' })
      .pipe(
        map((r: HttpResponse<Blob>) => ({
          blob: r.body!,
          fileName: fileNameFromDisposition(r.headers.get('Content-Disposition')),
        })),
      );
  }

  /** POST /audio/correctTranscript — re-runs term correction on a finished artifact (service). */
  correctTranscript(
    jobId: ID,
    fileName: string,
    profile: string,
  ): Observable<TranscriptCorrectionResult> {
    return this.http.post<TranscriptCorrectionResult>('/audio/correctTranscript', null, {
      params: { jobId, fileName, profile },
    });
  }
}

/** RO texts per ProblemDetails `title`; the backend `detail` may contain server paths. */
const ERROR_MESSAGES: Record<string, string> = {
  NotAudio: 'Fișierul nu conține audio sau este gol.',
  CorruptedAudio: 'Fișierul audio este deteriorat și nu poate fi citit.',
  UnknownProfile: 'Tipul discuției nu este recunoscut de server.',
  InvalidRequest:
    'Datele trimise nu sunt valide. Numărul de vorbitori trebuie să fie între 1 și 20.',
  RecordAlreadyCreated: 'Înregistrarea pentru acest fișier a fost deja creată.',
  InputNotFound: 'Fișierul nu a mai fost găsit pe server. Încărcați-l din nou.',
  FfmpegUnavailable: 'Procesarea audio nu este disponibilă pe server (FFmpeg lipsește).',
  LlmModelNotFound: 'Modelul lingvistic nu este instalat pe server.',
  ConversionFailed: 'Conversia fișierului audio a eșuat.',
  FileSystemError: 'Serverul nu a putut salva fișierul.',
  ModelFailed: 'Modelul de recunoaștere a eșuat.',
  InvalidTranscript: 'Transcrierea nu poate fi citită.',
};

const STATUS_MESSAGES: Record<number, string> = {
  0: 'Serverul nu răspunde. Verificați conexiunea și încercați din nou.',
  404: 'Resursa nu a fost găsită.',
  409: 'Operația nu este posibilă în starea actuală a înregistrării.',
  413: 'Fișierul depășește limita de 1 GB.',
};

const FALLBACK = 'A apărut o eroare neașteptată. Încercați din nou.';

/** Normalizes any HTTP failure into an ApiError with a user-facing RO message. */
export function toApiError(err: unknown): ApiError {
  if (!(err instanceof HttpErrorResponse)) return { code: 'UNKNOWN', message: FALLBACK };
  const body = (err.error && typeof err.error === 'object' ? err.error : {}) as ProblemDetails;
  if (body.traceId) console.warn(`API ${err.status} ${body.title ?? ''} traceId=${body.traceId}`);
  const code = body.title ?? (err.status === 0 ? 'NETWORK' : String(err.status));
  // DocumentService writes its details in Romanian and without paths.
  const message =
    (code === 'Document error' ? body.detail : undefined) ??
    ERROR_MESSAGES[code] ??
    STATUS_MESSAGES[err.status] ??
    FALLBACK;
  return { code, message, status: err.status, traceId: body.traceId };
}

function fileNameFromDisposition(header: string | null): string | null {
  if (!header) return null;
  const star = /filename\*=UTF-8''([^;]+)/i.exec(header);
  if (star) return decodeURIComponent(star[1]);
  const plain = /filename="?([^";]+)"?/i.exec(header);
  return plain ? plain[1] : null;
}
