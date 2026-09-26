// In-memory stand-in for the `/files` and `/records` endpoints of schema.md.
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
  ApiError,
  DiscussionType,
  Lang,
  Mom,
  ProcessingStage,
  ProcessingStatus,
  RecordDetails,
  SaveRecordRequest,
  SendEmailRequest,
  SpeakerAssociation,
  UploadFileResponse,
} from './models';

const LATENCY_MS = 250;
const QUEUE_MS = 3000;
const PROCESSING_MS = 45000;
const TRANSLATION_MS = 1500;
/** A record whose title contains one of these fails at speaker identification (demo of the failure UI). */
const FAIL_MARKERS = /eșec|esec|fail/i;
const FAIL_AT = 44;

const STAGE_RANGES: [ProcessingStage, number, number][] = [
  ['audio_analysis', 0, 26],
  ['speaker_identification', 26, 60],
  ['transcription', 60, 80],
  ['mom_drafting', 80, 100],
];

const SECTION_TITLES: Record<DiscussionType, [string, string, string, string]> = {
  medical: ['Situația clinică', 'Decizii clinice', 'Indicații și sarcini', 'Note pe pacienți'],
  executive: ['Rezumat', 'Decizii', 'Sarcini', 'Discuții'],
  administrative: ['Rezumat', 'Poziții convenite', 'Sarcini', 'Agendă'],
};

type MomBody = Pick<Mom, 'summary' | 'decisions' | 'actionItems'> & {
  topics: { f: number; title: string; note: string }[];
};

const MEDICAL_MOM: MomBody = {
  summary:
    'Vizita de dimineață în ATI, 8 din 10 paturi ocupate. Doi pacienți se ameliorează și pot fi transferați în secție; un pacient cu șoc septic rămâne instabil pe vasopresoare.',
  decisions: [
    'Patul 3: începerea sevrajului de la ventilația mecanică (test de respirație spontană la 10:00)',
    'Patul 5: creșterea dozei de norepinefrină, se adaugă hidrocortizon 200 mg/zi',
    'Paturile 1 și 7: pregătire pentru transfer în secție mâine',
  ],
  actionItems: [
    {
      task: 'Lactat și gazometrie la patul 5 la fiecare 4 ore',
      ownerSpeakerIndex: 1,
      due: '12:00',
    },
    { task: 'Test de respirație spontană, patul 3', ownerSpeakerIndex: 2, due: '10:00' },
    { task: 'Radiografie toracică, patul 8', ownerSpeakerIndex: 0, due: '11:00' },
    {
      task: 'Informarea familiilor pacienților de la paturile 1 și 7',
      ownerSpeakerIndex: 0,
      due: 'Azi',
    },
  ],
  topics: [
    {
      f: 0.03,
      title: 'Patul 5 — șoc septic, ziua 2',
      note: 'MAP 62 pe norepinefrină 0,3 µg/kg/min. Lactat 4,1 mmol/L. Culturi în lucru.',
    },
    {
      f: 0.3,
      title: 'Patul 3 — insuficiență respiratorie postoperatorie',
      note: 'FiO2 35%, PEEP 6. Conștient și cooperant.',
    },
    {
      f: 0.58,
      title: 'Paturile 1 și 7 — transfer',
      note: 'Stabili hemodinamic, fără suport vasopresor de 24 de ore.',
    },
    {
      f: 0.82,
      title: 'Patul 8 — pneumonie',
      note: 'Febră persistentă, se reevaluează antibioterapia după radiografie.',
    },
  ],
};

const GENERAL_MOM: MomBody = {
  summary:
    'Grupul a discutat prioritățile pentru trimestrul următor, cu accent pe retenția clienților și reproiectarea procesului de înrolare. Participanții au convenit asupra a trei priorități și au stabilit responsabilii.',
  decisions: [
    'Reproiectarea înrolării are prioritate față de integrările noi în T4',
    'Cinci interviuri cu clienți înainte de finalizarea planului de retenție',
    'Ședință de revizuire peste două săptămâni',
  ],
  actionItems: [
    { task: 'Recrutarea a cinci clienți pentru interviuri', ownerSpeakerIndex: 1, due: '2 oct.' },
    {
      task: 'Transmiterea datelor despre pâlnia de înrolare',
      ownerSpeakerIndex: 0,
      due: '30 sept.',
    },
    { task: 'Schița planului de retenție', ownerSpeakerIndex: 2, due: '7 oct.' },
  ],
  topics: [
    {
      f: 0.02,
      title: 'Deschidere și context',
      note: 'Recapitularea ratei de abandon din trimestrul trecut și obiectivele ședinței.',
    },
    {
      f: 0.22,
      title: 'Retenția clienților',
      note: 'Abandonul se concentrează în primele 30 de zile, deci înrolarea este pârghia principală.',
    },
    {
      f: 0.55,
      title: 'Reproiectarea înrolării',
      note: 'Trei variante discutate; se testează varianta cu ghid interactiv.',
    },
    {
      f: 0.8,
      title: 'Pașii următori',
      note: 'Responsabili stabiliți și dată fixată pentru revizuire.',
    },
  ],
};

interface MockRecord extends RecordDetails {
  processStartedAt?: number;
  failed?: boolean;
  langsReady: Set<Lang>;
  translating: Map<Lang, number>;
  speakers: SpeakerAssociation[];
}

const files = new Map<string, UploadFileResponse>();
const records = new Map<string, MockRecord>();

const newId = (prefix: string) => prefix + '_' + Math.random().toString(36).slice(2, 7);

export const mockApiInterceptor: HttpInterceptorFn = (req, next) => {
  if (!USE_MOCK_API || !/^\/(files|records)(\/|\?|$)/.test(req.url)) return next(req);
  if (req.method === 'POST' && req.url === '/files') return upload(req);
  try {
    return route(req).pipe(delay(LATENCY_MS));
  } catch (e) {
    const err = e as { status: number; body: ApiError };
    return throwError(
      () => new HttpErrorResponse({ status: err.status, error: err.body, url: req.url }),
    ).pipe(delay(LATENCY_MS));
  }
};

function fail(status: number, code: string, message: string): never {
  throw { status, body: { code, message } };
}

function ok<T>(body: T, status = 200, headers?: HttpHeaders): Observable<HttpEvent<T>> {
  return of(new HttpResponse({ status, body, headers }));
}

function upload(req: HttpRequest<unknown>): Observable<HttpEvent<unknown>> {
  const file = (req.body as FormData).get('file') as File;
  const ext = (file.name.split('.').pop() ?? '').toUpperCase();
  if (file.size === 0)
    return throwError(
      () =>
        new HttpErrorResponse({
          status: 400,
          error: { code: 'EMPTY_FILE', message: 'Acest fișier nu conține date audio.' },
        }),
    );
  const steps = Math.max(14, Math.min(40, file.size / 12e6));
  const stored: UploadFileResponse = {
    fileId: newId('fil'),
    fileName: file.name,
    sizeBytes: file.size,
    format: ext,
    durationSec: Math.max(60, Math.round(file.size / 16000)),
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
      files.set(stored.fileId, stored);
      return new HttpResponse({ status: 201, body: stored });
    }),
  );
  return concat(progress, done);
}

function route(req: HttpRequest<unknown>): Observable<HttpEvent<unknown>> {
  const [, , id, action] = req.url.split('?')[0].split('/');
  if (req.method === 'POST' && !id) return ok(saveRecord(req.body as SaveRecordRequest), 201);

  const rec = records.get(id) ?? fail(404, 'RECORD_NOT_FOUND', 'Înregistrarea nu a fost găsită.');
  tickRecord(rec);

  switch (`${req.method} ${action ?? ''}`) {
    case 'GET ':
      return ok(details(rec));
    case 'PATCH ':
      rec.title = (req.body as { title: string }).title;
      return ok(details(rec));
    case 'POST process':
      if (rec.status !== 'pending' || rec.processStartedAt)
        fail(409, 'ALREADY_PROCESSING', 'Înregistrarea este deja în procesare.');
      rec.processStartedAt = Date.now();
      rec.startedAt = new Date().toISOString();
      return ok(status(rec), 202);
    case 'POST retry':
      if (rec.status !== 'failed')
        fail(409, 'NOT_FAILED', 'Doar o înregistrare eșuată poate fi reluată.');
      Object.assign(rec, { status: 'pending', progress: 0, error: undefined, failed: false });
      rec.processStartedAt = Date.now();
      return ok(status(rec), 202);
    case 'GET status':
      return ok(status(rec));
    case 'GET mom':
      return mom(rec, req.params.get('lang') as Lang | null);
    case 'PUT speakers':
      rec.speakers = (req.body as { speakers: SpeakerAssociation[] }).speakers;
      if (rec.speakers.some((s) => s.speakerIndex >= rec.speakersCount))
        fail(422, 'INVALID_SPEAKER_INDEX', 'Vorbitor inexistent în această înregistrare.');
      return ok({ speakers: rec.speakers });
    case 'GET export':
      return exportFile(
        rec,
        req.params.get('format') ?? 'MD',
        (req.params.get('lang') as Lang) ?? rec.momLang,
      );
    case 'POST email': {
      const body = req.body as SendEmailRequest;
      if (!body.to.length) fail(422, 'INVALID_RECIPIENTS', 'Adăugați cel puțin un destinatar.');
      return ok({ sentTo: body.to });
    }
  }
  return fail(404, 'NOT_FOUND', 'Resursa nu există.');
}

function saveRecord(body: SaveRecordRequest): RecordDetails {
  const file =
    files.get(body.fileId) ?? fail(404, 'FILE_NOT_FOUND', 'Fișierul încărcat nu a fost găsit.');
  if (!body.title.trim() || body.title.length > 120)
    throw {
      status: 422,
      body: {
        code: 'VALIDATION_ERROR',
        message: 'Numele trebuie să aibă 1–120 de caractere.',
        field: 'title',
      },
    };
  const rec: MockRecord = {
    id: newId('rsn'),
    title: body.title.trim(),
    fileName: file.fileName,
    sizeBytes: file.sizeBytes,
    durationSec: file.durationSec,
    speakersCount: body.speakersCount,
    discussionType: body.discussionType,
    momLang: body.momLang,
    status: 'pending',
    progress: 0,
    createdAt: new Date().toISOString(),
    estimatedProcessingSec: Math.round((QUEUE_MS + PROCESSING_MS) / 1000),
    failed: FAIL_MARKERS.test(body.title),
    langsReady: new Set([body.momLang]),
    translating: new Map(),
    speakers: [],
  };
  records.set(rec.id, rec);
  return details(rec);
}

/** Advances the simulated processing clock of a record. */
function tickRecord(rec: MockRecord): void {
  if (!rec.processStartedAt || rec.status === 'completed' || rec.status === 'failed') return;
  const elapsed = Date.now() - rec.processStartedAt - QUEUE_MS;
  if (elapsed < 0) return;
  const progress = Math.min(100, (elapsed / PROCESSING_MS) * 100);
  if (rec.failed && progress >= FAIL_AT) {
    Object.assign(rec, {
      status: 'failed',
      progress: FAIL_AT,
      error: {
        code: 'SPEAKERS_OVERLAP',
        message:
          'Vocile se suprapun în cea mai mare parte a înregistrării, așa că nu am putut distinge vorbitorii. Reîncercați sau alegeți un număr mai mic de vorbitori.',
      },
    });
  } else if (progress >= 100) {
    const now = Date.now();
    Object.assign(rec, {
      status: 'completed',
      progress: 100,
      completedAt: new Date(now).toISOString(),
      processingTimeSec: Math.round((now - rec.processStartedAt) / 1000),
    });
  } else {
    Object.assign(rec, { status: 'processing', progress });
  }
}

function details(rec: MockRecord): RecordDetails {
  const { processStartedAt, failed, langsReady, translating, speakers, ...pub } = rec;
  return { ...pub, progress: Math.floor(pub.progress) };
}

function status(rec: MockRecord): ProcessingStatus {
  const s: ProcessingStatus = {
    recordId: rec.id,
    status: rec.status,
    progress: Math.floor(rec.progress),
  };
  if (rec.status === 'pending') s.queuePosition = 1;
  if (rec.status === 'processing' || rec.status === 'failed') {
    const [stage, from, to] =
      STAGE_RANGES.find(([, f, t]) => rec.progress >= f && rec.progress < t) ?? STAGE_RANGES[3];
    s.stage = stage;
    s.stageProgress = Math.floor(((rec.progress - from) / (to - from)) * 100);
  }
  if (rec.status === 'processing')
    s.etaSec = Math.ceil(((100 - rec.progress) / 100) * (PROCESSING_MS / 1000));
  if (rec.status === 'failed') s.error = rec.error;
  return s;
}

function mom(rec: MockRecord, lang: Lang | null): Observable<HttpEvent<unknown>> {
  if (rec.status !== 'completed')
    fail(409, 'RECORD_NOT_COMPLETED', 'Procesarea nu s-a încheiat încă.');
  const l = lang ?? rec.momLang;
  if (!rec.langsReady.has(l)) {
    const started = rec.translating.get(l) ?? Date.now();
    rec.translating.set(l, started);
    if (Date.now() - started < TRANSLATION_MS)
      return ok({ recordId: rec.id, lang: l, status: 'generating' }, 202);
    rec.langsReady.add(l);
  }
  return ok(buildMom(rec, l));
}

function buildMom(rec: MockRecord, lang: Lang): Mom {
  const body = rec.discussionType === 'medical' ? MEDICAL_MOM : GENERAL_MOM;
  const [summary, decisions, actions, topics] = SECTION_TITLES[rec.discussionType];
  return {
    recordId: rec.id,
    lang,
    discussionType: rec.discussionType,
    generatedAt: rec.completedAt ?? new Date().toISOString(),
    sectionTitles: { summary, decisions, actions, topics },
    summary: body.summary,
    decisions: body.decisions,
    actionItems: body.actionItems.map((a) => ({
      ...a,
      ownerSpeakerIndex: a.ownerSpeakerIndex % rec.speakersCount,
    })),
    topics: body.topics.map((t) => ({
      startSec: Math.round(t.f * rec.durationSec),
      title: t.title,
      note: t.note,
    })),
  };
}

function exportFile(rec: MockRecord, format: string, lang: Lang): Observable<HttpEvent<unknown>> {
  if (!rec.langsReady.has(lang))
    fail(409, 'MOM_NOT_READY', 'Procesul-verbal în această limbă nu este gata.');
  const m = buildMom(rec, lang);
  const name = (i: number) =>
    rec.speakers.find((s) => s.speakerIndex === i)?.name ?? `Vorbitor ${i + 1}`;
  const text = [
    `# ${rec.title}`,
    '',
    `## ${m.sectionTitles.summary}`,
    m.summary,
    '',
    `## ${m.sectionTitles.decisions}`,
    ...m.decisions.map((d, i) => `${i + 1}. ${d}`),
    '',
    `## ${m.sectionTitles.actions}`,
    ...m.actionItems.map((a) => `- ${a.task} — ${name(a.ownerSpeakerIndex)}, ${a.due}`),
    '',
    `## ${m.sectionTitles.topics}`,
    ...m.topics.map((t) => `- ${t.title}: ${t.note}`),
  ].join('\n');
  const ext = format === 'DOCX' ? 'docx' : format === 'PDF' ? 'pdf' : 'md';
  const fileName = `${rec.title} — proces-verbal.${ext}`;
  const headers = new HttpHeaders({
    'Content-Disposition': `attachment; filename*=UTF-8''${encodeURIComponent(fileName)}`,
  });
  // The mock always returns Markdown text, whatever the requested format.
  return ok(new Blob([text], { type: 'text/markdown' }), 200, headers);
}
