import { HttpClient, HttpErrorResponse, HttpEventType, HttpResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, filter, map, timer, switchMap, of } from 'rxjs';
import {
  ApiError,
  ExportFormat,
  ID,
  Lang,
  Mom,
  MomGenerating,
  ProcessingStatus,
  RecordDetails,
  RecordSummary,
  SaveRecordRequest,
  SendEmailRequest,
  SendEmailResponse,
  SpeakerAssociation,
  UploadFileResponse,
} from './models';

export type UploadEvent =
  { kind: 'progress'; loaded: number; total: number } | { kind: 'done'; file: UploadFileResponse };

export interface ExportedFile {
  blob: Blob;
  fileName: string | null;
}

/** getMom result: the minutes, or `null` while a translation is still being generated (202). */
export type MomResult = Mom | null;

@Injectable({ providedIn: 'root' })
export class ResonaApi {
  private readonly http = inject(HttpClient);

  /** 1. POST /files — emits upload progress, then the stored file. Unsubscribe to cancel. */
  uploadFile(file: File): Observable<UploadEvent> {
    const body = new FormData();
    body.append('file', file, file.name);
    return this.http
      .post<UploadFileResponse>('/files', body, { reportProgress: true, observe: 'events' })
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

  /** 2. POST /records */
  saveRecord(req: SaveRecordRequest): Observable<RecordSummary> {
    return this.http.post<RecordSummary>('/records', req);
  }

  /** 3. POST /records/:id/process */
  processRecord(id: ID): Observable<ProcessingStatus> {
    return this.http.post<ProcessingStatus>(`/records/${id}/process`, null);
  }

  /** 4. GET /records/:id */
  getRecord(id: ID): Observable<RecordDetails> {
    return this.http.get<RecordDetails>(`/records/${id}`);
  }

  /** 5. GET /records/:id/status */
  getProcessingStatus(id: ID): Observable<ProcessingStatus> {
    return this.http.get<ProcessingStatus>(`/records/${id}/status`);
  }

  /** 6. GET /records/:id/mom — `null` on 202 (generating). */
  getMom(id: ID, lang?: Lang): Observable<MomResult> {
    const params: Record<string, string> = lang ? { lang } : {};
    return this.http
      .get<Mom | MomGenerating>(`/records/${id}/mom`, { params, observe: 'response' })
      .pipe(map((r) => (r.status === 202 ? null : (r.body as Mom))));
  }

  /** getMom, retried every `retryMs` while the backend answers 202. */
  getMomWhenReady(id: ID, lang?: Lang, retryMs = 2000): Observable<Mom> {
    return this.getMom(id, lang).pipe(
      switchMap((mom) =>
        mom
          ? of(mom)
          : timer(retryMs).pipe(switchMap(() => this.getMomWhenReady(id, lang, retryMs))),
      ),
    );
  }

  /** 7. PUT /records/:id/speakers */
  speakerAssociation(id: ID, speakers: SpeakerAssociation[]): Observable<SpeakerAssociation[]> {
    return this.http
      .put<{ speakers: SpeakerAssociation[] }>(`/records/${id}/speakers`, { speakers })
      .pipe(map((r) => r.speakers));
  }

  /** 8. GET /records/:id/export */
  exportMom(id: ID, format: ExportFormat, lang?: Lang): Observable<ExportedFile> {
    const params: Record<string, string> = lang ? { format, lang } : { format };
    return this.http
      .get(`/records/${id}/export`, { params, responseType: 'blob', observe: 'response' })
      .pipe(
        map((r: HttpResponse<Blob>) => ({
          blob: r.body!,
          fileName: fileNameFromDisposition(r.headers.get('Content-Disposition')),
        })),
      );
  }

  /** 9. POST /records/:id/email */
  sendEmail(id: ID, req: SendEmailRequest): Observable<SendEmailResponse> {
    return this.http.post<SendEmailResponse>(`/records/${id}/email`, req);
  }

  /** 11. POST /records/:id/retry */
  retryProcessing(id: ID): Observable<ProcessingStatus> {
    return this.http.post<ProcessingStatus>(`/records/${id}/retry`, null);
  }

  /** 12. PATCH /records/:id */
  updateRecord(id: ID, title: string): Observable<RecordSummary> {
    return this.http.patch<RecordSummary>(`/records/${id}`, { title });
  }
}

/** Normalizes any HTTP failure into an ApiError with a user-facing RO message. */
export function toApiError(err: unknown): ApiError {
  if (err instanceof HttpErrorResponse) {
    const body = err.error as Partial<ApiError> | null;
    if (body && typeof body === 'object' && body.message) {
      return { code: body.code ?? String(err.status), message: body.message, field: body.field };
    }
    if (err.status === 0)
      return {
        code: 'NETWORK',
        message: 'Serverul nu răspunde. Verificați conexiunea și încercați din nou.',
      };
    return {
      code: String(err.status),
      message: 'A apărut o eroare neașteptată. Încercați din nou.',
    };
  }
  return { code: 'UNKNOWN', message: 'A apărut o eroare neașteptată. Încercați din nou.' };
}

function fileNameFromDisposition(header: string | null): string | null {
  if (!header) return null;
  const star = /filename\*=UTF-8''([^;]+)/i.exec(header);
  if (star) return decodeURIComponent(star[1]);
  const plain = /filename="?([^";]+)"?/i.exec(header);
  return plain ? plain[1] : null;
}
