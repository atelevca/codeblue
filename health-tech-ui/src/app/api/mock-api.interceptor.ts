// In-memory stand-in for the backend endpoints (../docs/ui-integration.md).
// Enabled by USE_MOCK_API; any other request goes to the network untouched.
import {
  HttpEvent,
  HttpEventType,
  HttpHeaders,
  HttpInterceptorFn,
  HttpRequest,
  HttpResponse,
  HttpErrorResponse,
} from '@angular/common/http';
import { Observable, delay, of, throwError, interval, map, takeWhile, concat } from 'rxjs';
import { USE_MOCK_API } from './api.config';
import {
  Job,
  ProblemDetails,
  Profile,
  QuillDelta,
  SaveRecordRequest,
  SavedDocument,
  TranscriptResult,
} from './models';

const LATENCY_MS = 250;
const QUEUE_MS = 3000;
const PROCESSING_MS = 45000;
const GENERATION_MS = 8000;
/** A record whose title contains one of these fails during diarization (demo of the failure UI). */
const FAIL_MARKERS = /eșec|esec|fail/i;
const FAIL_AT = 60;

const PROFILES: Profile[] = [
  { key: 'administrative', displayName: 'Административная запись' },
  { key: 'financial', displayName: 'Финансовая запись' },
  { key: 'medical', displayName: 'Медицинская запись' },
];

/** Backend step captions by the percent at which they start. */
const STEPS: [number, string][] = [
  [0, 'Нормализация аудио'],
  [5, 'Подготовка 16 кГц'],
  [10, 'Поиск речи (VAD)'],
  [15, 'Распознавание'],
  [55, 'Диаризация'],
  [75, 'Выравнивание спикеров'],
  [78, 'Коррекция терминов'],
  [98, 'Сохранение'],
];

const TURNS: [number, string][] = [
  [0, 'Bună dimineața. Începem vizita cu patul cinci, șoc septic, ziua a doua.'],
  [1, 'Tensiunea medie e 62 pe norepinefrină 0,3. Lactatul a crescut la 4,1.'],
  [0, 'Creștem doza de norepinefrină și adăugăm hidrocortizon 200 de miligrame pe zi.'],
  [2, 'Am notat. Repet lactatul și gazometria la fiecare patru ore.'],
  [1, 'Patul trei poate începe sevrajul, facem testul de respirație spontană la zece.'],
  [0, 'De acord. Paturile unu și șapte se pregătesc pentru transfer mâine.'],
];

const MINUTES = `# Proces-verbal
## Rezumat
Vizita de dimineață în ATI. Pacientul de la patul 5 rămâne instabil pe vasopresoare; patul 3 începe sevrajul de la ventilație.
## Decizii
1. Patul 5: creșterea dozei de norepinefrină, se adaugă hidrocortizon 200 mg/zi.
2. Patul 3: test de respirație spontană la 10:00.
3. Paturile 1 și 7: pregătire pentru transfer mâine.
## Sarcini
- Lactat și gazometrie la patul 5 la fiecare 4 ore — Speaker 3.
- Test de respirație spontană, patul 3 — Speaker 2.`;

interface MockJob {
  job: Job;
  startedAt?: number;
  fail: boolean;
  doc?: SavedDocument;
}

const jobs = new Map<string, MockJob>();

export const mockApiInterceptor: HttpInterceptorFn = (req, next) => {
  if (!USE_MOCK_API || !/^\/(profiles|files|jobs|document|audio)(\/|\?|$)/.test(req.url))
    return next(req);
  if (req.method === 'POST' && req.url === '/files') return upload(req);
  try {
    const res = route(req);
    const wait = req.url.startsWith('/document/save') ? GENERATION_MS : LATENCY_MS;
    return res.pipe(delay(wait));
  } catch (e) {
    const err = e as { status: number; body: ProblemDetails };
    return throwError(
      () => new HttpErrorResponse({ status: err.status, error: err.body, url: req.url }),
    ).pipe(delay(LATENCY_MS));
  }
};

function fail(status: number, title: string, detail: string): never {
  throw { status, body: { status, title, detail, traceId: 'mock' } };
}

function ok<T>(body: T, status = 200, headers?: HttpHeaders): Observable<HttpEvent<T>> {
  return of(new HttpResponse({ status, body, headers }));
}

function upload(req: HttpRequest<unknown>): Observable<HttpEvent<unknown>> {
  const file = (req.body as FormData).get('file') as File;
  if (file.size === 0)
    return throwError(
      () =>
        new HttpErrorResponse({
          status: 422,
          error: { status: 422, title: 'NotAudio', detail: 'Загруженный файл пуст.' },
        }),
    );
  const steps = Math.max(14, Math.min(40, file.size / 12e6));
  const job: Job = {
    id: crypto.randomUUID(),
    fileName: file.name,
    profileKey: '',
    status: 'Uploaded',
    currentStep: null,
    percent: 0,
    workflowId: null,
    error: null,
    createdAt: new Date().toISOString(),
    completedAt: null,
    sizeBytes: file.size,
    format: 'wav',
    durationSec: Math.max(60, Math.round(file.size / 16000)),
    title: null,
    speakersCount: null,
  };
  const progress = interval(100).pipe(
    map((i) => Math.min(file.size, Math.round(((i + 1) / steps) * file.size))),
    takeWhile((loaded) => loaded < file.size, true),
    map((loaded): HttpEvent<unknown> => ({
      type: HttpEventType.UploadProgress,
      loaded,
      total: file.size,
    })),
  );
  const done = of(null).pipe(
    map(() => {
      jobs.set(job.id, { job, fail: false });
      return new HttpResponse({
        status: 200,
        body: {
          fileId: job.id,
          fileName: job.fileName,
          sizeBytes: job.sizeBytes,
          format: job.format,
          durationSec: job.durationSec,
        },
      });
    }),
  );
  return concat(progress, done);
}

function route(req: HttpRequest<unknown>): Observable<HttpEvent<unknown>> {
  const path = req.url.split('?')[0].split('/').slice(1);
  const key = `${req.method} /${path[0]}${path[1] && path[0] === 'document' ? '/' + path[1] : ''}`;
  if (key === 'GET /profiles') return ok(PROFILES);
  if (key === 'POST /jobs') return ok({ ...saveRecord(req.body as SaveRecordRequest) });
  if (key === 'GET /jobs' && !path[1])
    return ok(
      [...jobs.values()]
        .map((j) => ({ ...tick(j) }))
        .filter((j) => j.status !== 'Uploaded')
        .reverse(),
    );

  const id = path[0] === 'document' ? path[2] : path[1];
  const m = jobs.get(id) ?? fail(404, 'Not Found', 'Jobul nu a fost găsit.');
  tick(m);
  switch (key + (path[0] === 'jobs' && path[2] ? '/' + path[2] : '')) {
    case 'GET /jobs':
      return ok({ ...m.job });
    case 'GET /jobs/result':
      if (m.job.status !== 'Completed') fail(404, 'Not Found', 'Result not ready.');
      return ok(result(m.job));
    case 'POST /document/save':
      if (m.job.status !== 'Completed')
        fail(409, 'Document error', 'Transcrierea jobului nu este finalizată.');
      m.doc = buildDocument(m.job, (req.body as { delta?: QuillDelta } | null)?.delta);
      return ok(m.doc);
    case 'GET /document/get':
      return ok(
        m.doc ?? fail(404, 'Document error', 'Documentul nu a fost salvat pentru acest job.'),
      );
    case 'POST /document/sendemail': {
      if (!m.doc) fail(404, 'Document error', 'Documentul nu a fost salvat pentru acest job.');
      const body = req.body as { to: string[]; subject: string };
      return ok({
        to: body.to,
        subject: body.subject,
        attachmentFileName: `proces-verbal-${id}.pdf`,
      });
    }
    case 'GET /document/downloadpdf': {
      if (!m.doc) fail(404, 'Document error', 'Documentul nu a fost salvat pentru acest job.');
      const headers = new HttpHeaders({
        'Content-Disposition': `attachment; filename=proces-verbal-${id}.pdf`,
      });
      // The mock returns the Markdown as text; the real backend renders a PDF.
      return ok(new Blob([m.doc.minutesMarkdown], { type: 'text/plain' }), 200, headers);
    }
  }
  return fail(404, 'Not Found', 'Resursa nu există.');
}

function saveRecord(body: SaveRecordRequest): Job {
  const m = jobs.get(body.fileId) ?? fail(404, 'InputNotFound', 'Файл не найден.');
  if (m.job.status !== 'Uploaded')
    fail(409, 'RecordAlreadyCreated', 'Запись по этому файлу уже оформлена.');
  if (!PROFILES.some((p) => p.key === body.discussionType))
    fail(400, 'UnknownProfile', 'Неизвестный профиль.');
  if (body.speakersCount != null && (body.speakersCount < 1 || body.speakersCount > 20))
    fail(400, 'InvalidRequest', 'speakersCount вне 1..20.');
  const title = body.title?.trim() || m.job.fileName;
  Object.assign(m.job, {
    title,
    speakersCount: body.speakersCount ?? null,
    profileKey: body.discussionType,
    status: 'Pending',
    workflowId: crypto.randomUUID(),
  });
  m.fail = FAIL_MARKERS.test(title);
  m.startedAt = Date.now();
  return m.job;
}

/** Advances the simulated processing clock of a job. */
function tick(m: MockJob): Job {
  const j = m.job;
  if (!m.startedAt || j.status === 'Completed' || j.status === 'Failed') return j;
  const elapsed = Date.now() - m.startedAt - QUEUE_MS;
  if (elapsed < 0) return j;
  const percent = Math.min(100, Math.floor((elapsed / PROCESSING_MS) * 100));
  let step = [...STEPS].reverse().find(([from]) => percent >= from)![1];
  if (step === 'Распознавание')
    step += `: чанк ${Math.min(7, Math.floor(((percent - 15) / 40) * 7) + 1)} из 7`;
  if (step === 'Коррекция терминов')
    step += `: батч ${Math.min(5, Math.floor(((percent - 78) / 20) * 5) + 1)} из 5`;
  if (m.fail && percent >= FAIL_AT)
    Object.assign(j, {
      status: 'Failed',
      percent: FAIL_AT,
      error: 'Диаризация: модель не смогла разделить голоса.',
    });
  else if (percent >= 100)
    Object.assign(j, {
      status: 'Completed',
      percent: 100,
      currentStep: 'Готово',
      completedAt: new Date().toISOString(),
    });
  else Object.assign(j, { status: 'Running', percent, currentStep: step });
  return j;
}

function result(j: Job): TranscriptResult {
  const dur = j.durationSec ?? 60;
  const span = dur / TURNS.length;
  const mmss = (s: number) =>
    `${String(Math.floor(s / 60)).padStart(2, '0')}:${String(Math.floor(s % 60)).padStart(2, '0')}`;
  const turns = TURNS.map(([sp, text], i) => ({
    start: i * span,
    end: (i + 1) * span - 0.5,
    startTime: mmss(i * span),
    endTime: mmss((i + 1) * span - 0.5),
    speaker: `Speaker ${sp + 1}`,
    text,
  }));
  return {
    fileName: j.fileName,
    durationSeconds: dur,
    speakers: ['Speaker 1', 'Speaker 2', 'Speaker 3'],
    turns,
    text: turns.map((t) => `${t.speaker}:\n${t.text}`).join('\n\n'),
  };
}

function buildDocument(j: Job, edited?: QuillDelta): SavedDocument {
  const delta = edited ?? markdownDelta(MINUTES);
  return {
    jobId: j.id,
    minutesMarkdown: MINUTES,
    verification: {
      summary: 'Documentul corespunde transcrierii, cu o observație.',
      findings: [
        {
          kind: 'Omission',
          description: 'Transferul paturilor 1 și 7 nu menționează criteriul de stabilitate.',
          transcriptQuote: 'Paturile unu și șapte se pregătesc pentru transfer mâine.',
          documentQuote: null,
          suggestedCorrection: 'Adăugați „stabili hemodinamic, fără vasopresoare de 24 h”.',
        },
      ],
      completed: true,
      discardedFindings: 0,
      isConsistent: false,
    },
    savedAt: new Date().toISOString(),
    delta,
  };
}

/** The same Markdown → Quill conversion the backend does for generated minutes. */
function markdownDelta(md: string): QuillDelta {
  const ops: QuillDelta['ops'] = [];
  for (const line of md.split('\n')) {
    const h = /^(#{1,6})\s+(.*)$/.exec(line);
    const li = /^(?:[-*]|\d+\.)\s+(.*)$/.exec(line);
    if (h) ops.push({ insert: h[2] }, { insert: '\n', attributes: { header: h[1].length } });
    else if (li)
      ops.push(
        { insert: li[1] },
        { insert: '\n', attributes: { list: /^\d/.test(line) ? 'ordered' : 'bullet' } },
      );
    else ops.push({ insert: line + '\n' });
  }
  return { ops };
}
