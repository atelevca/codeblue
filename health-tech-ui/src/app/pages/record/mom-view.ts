import {
  Component,
  DestroyRef,
  ElementRef,
  OnInit,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import type Quill from 'quill';
import { FindingKind, Job, QuillOp, TranscriptResult } from '../../api/models';
import { CodeBlueApi, toApiError } from '../../api/codeblue-api';
import { speakerColor, typeInfo } from '../../shared/catalog';
import { fmtDate, fmtDur, fmtLongDate } from '../../shared/format';
import { deltaLines, deltaText } from '../../shared/quill';
import { MinutesStore } from '../../state/minutes-store';
import { jobTitle } from '../../state/processing-tracker';
import { SpeakerMap, resolveLink } from '../../state/speaker-map';
import { Toasts } from '../../state/toasts';
import { EmailDialog } from './email-dialog';
import { SpeakerPanel } from './speaker-panel';
import { TranscriptLine, TranscriptPanel } from './transcript-panel';

type DownloadState = 'idle' | 'preparing' | 'done';

/** MoM language switch. UI only: the backend has no language parameter, nothing is regenerated. */
const LANGS = [
  { code: 'RO', label: 'Română' },
  { code: 'RU', label: 'Русский' },
  { code: 'EN', label: 'English' },
];

/** The formats the backend accepts in a saved delta (see HealthTech/Documents/README.md). */
const QUILL_FORMATS = ['header', 'bold', 'italic', 'underline', 'list', 'indent', 'align', 'link'];
const QUILL_TOOLBAR = [
  [{ header: [1, 2, 3, false] }],
  ['bold', 'italic', 'underline'],
  [{ list: 'ordered' }, { list: 'bullet' }],
  [{ indent: '-1' }, { indent: '+1' }],
  [{ align: [] }],
  ['link'],
  ['clean'],
];

const KIND_LABELS: Record<FindingKind, string> = {
  Unsupported: 'Afirmație fără suport în transcriere',
  Omission: 'Omisiune',
  Contradiction: 'Contradicție',
  Misattribution: 'Atribuire greșită',
};

@Component({
  selector: 'app-mom-view',
  imports: [RouterLink, SpeakerPanel, EmailDialog, TranscriptPanel],
  templateUrl: './mom-view.html',
  styleUrl: './mom-view.scss',
})
export class MomView implements OnInit {
  private readonly api = inject(CodeBlueApi);
  private readonly toasts = inject(Toasts);
  private readonly speakerMap = inject(SpeakerMap);
  private readonly minutes = inject(MinutesStore);

  readonly record = input.required<Job>();

  protected readonly result = signal<TranscriptResult | null>(null);
  protected readonly resultError = signal<string | null>(null);
  protected readonly download = signal<DownloadState>('idle');
  protected readonly copied = signal(false);
  protected readonly emailOpen = signal(false);
  protected readonly mappingExpanded = signal(false);
  protected readonly transcriptOpen = signal(false);
  protected readonly editing = signal(false);
  private readonly editorHost = viewChild<ElementRef<HTMLDivElement>>('editor');
  private quill: Quill | null = null;
  private quillCtor: typeof Quill | null = null;
  protected readonly langs = LANGS;
  protected readonly lang = signal('RO');
  private readonly now = signal(Date.now());

  protected readonly title = computed(() => jobTitle(this.record()));
  protected readonly typeLabel = computed(() => typeInfo(this.record().profileKey).label);
  protected readonly state = computed(() => this.minutes.of(this.record().id));
  protected readonly doc = computed(() => {
    const s = this.state();
    return s.kind === 'ready' || s.kind === 'saving' ? s.doc : null;
  });
  protected readonly saving = computed(() => this.state().kind === 'saving');
  protected readonly errorState = computed(() => {
    const s = this.state();
    return s.kind === 'error' ? s : null;
  });
  protected readonly lines = computed(() => deltaLines(this.doc()?.delta));

  protected readonly elapsed = computed(() => {
    const s = this.state();
    return s.kind === 'generating' || s.kind === 'saving'
      ? fmtDur((this.now() - s.startedAt) / 1000)
      : '';
  });

  private readonly speakerCount = computed(
    () => this.result()?.speakers.length ?? this.record().speakersCount ?? 0,
  );

  protected readonly names = computed(() => {
    const links = this.speakerMap.of(this.record().id);
    return Array.from(
      { length: this.speakerCount() },
      (_, i) => resolveLink(links[i])?.name ?? `Vorbitor ${i + 1}`,
    );
  });

  protected readonly colors = computed(() =>
    Array.from({ length: this.speakerCount() }, (_, i) => speakerColor(i)),
  );

  protected readonly turns = computed((): TranscriptLine[] => {
    const r = this.result();
    if (!r) return [];
    const names = this.names();
    return r.turns.map((t) => {
      const i = r.speakers.indexOf(t.speaker);
      return {
        time: t.startTime,
        name: names[i] ?? t.speaker,
        color: i >= 0 ? speakerColor(i) : 'var(--text-4)',
        text: t.text,
      };
    });
  });

  protected readonly meta = computed(() => {
    const r = this.record();
    const n = this.speakerCount();
    const people = n === 1 ? '1 participant' : `${n} participanți`;
    return `${fmtLongDate(r.createdAt)} · ${fmtDur(r.durationSec ?? 0)} · ${people}`;
  });

  protected readonly readyDate = computed(() =>
    fmtDate(this.record().completedAt ?? this.record().createdAt),
  );

  protected readonly verification = computed(() => {
    const v = this.doc()?.verification;
    if (!v) return null;
    const partialClean =
      v.completed && !!v.partial && v.findings.length === 0 && v.discardedFindings === 0;
    const tone = !v.completed || partialClean ? 'unknown' : v.isConsistent ? 'ok' : 'warn';
    const label = v.pending
      ? 'Verificarea automată este în curs — documentul poate fi consultat, dar nu este încă verificat'
      : !v.completed
        ? 'Verificarea automată nu a fost finalizată — documentul nu este verificat'
        : v.isConsistent
          ? 'Verificat: nicio neconcordanță cu transcrierea'
          : partialClean
            ? 'Verificat pe fragmente: nicio neconcordanță; afirmațiile fără suport nu au fost verificate'
            : v.findings.length === 0
              ? `Verificare neconcludentă: ${v.discardedFindings} constatări fără citat regăsit — necesită revizuire`
              : v.findings.length === 1
                ? '1 neconcordanță cu transcrierea'
                : `${v.findings.length} neconcordanțe cu transcrierea`;
    return {
      tone,
      label,
      summary: v.summary,
      findings: v.findings.map((f) => ({ ...f, kindLabel: KIND_LABELS[f.kind] ?? f.kind })),
    };
  });

  protected readonly docName = computed(() => `proces-verbal-${this.record().id}.pdf`);

  protected readonly plainText = computed(() => {
    const d = this.doc();
    return d ? deltaText(d.delta) || d.minutesMarkdown : '';
  });

  protected readonly details = computed(() => {
    const r = this.record();
    const rows = [
      { k: 'Fișier sursă', v: r.fileName },
      { k: 'Durată', v: fmtDur(r.durationSec ?? 0) },
      { k: 'Vorbitori detectați', v: this.result() ? String(this.speakerCount()) : '—' },
      { k: 'Tipul discuției', v: this.typeLabel() },
    ];
    if (r.speakersCount)
      rows.splice(3, 0, { k: 'Vorbitori declarați', v: String(r.speakersCount) });
    if (r.completedAt)
      rows.push({
        k: 'Timp de procesare',
        v: fmtDur((Date.parse(r.completedAt) - Date.parse(r.createdAt)) / 1000),
      });
    const d = this.doc();
    if (d) rows.push({ k: 'Proces-verbal salvat', v: fmtDate(d.savedAt) });
    return rows;
  });

  constructor() {
    const timer = setInterval(() => this.now.set(Date.now()), 1000);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
    // The editor host exists only while editing; Quill is created when it appears and filled
    // with the saved delta, and dropped with it.
    effect(() => {
      const host = this.editorHost()?.nativeElement;
      if (!host) {
        this.quill = null;
        return;
      }
      const Ctor = this.quillCtor;
      if (!Ctor || this.quill?.container === host) return;
      const quill = new Ctor(host, {
        theme: 'snow',
        formats: QUILL_FORMATS,
        modules: { toolbar: QUILL_TOOLBAR },
      });
      const delta = untracked(this.doc)?.delta;
      if (delta) quill.setContents(delta.ops, 'api');
      this.quill = quill;
    });
    // A regeneration or a load error takes the document away: leave edit mode with it.
    effect(() => {
      if (!this.doc()) this.editing.set(false);
    });
  }

  ngOnInit(): void {
    const id = this.record().id;
    this.minutes.load(id);
    this.api.getResult(id).subscribe({
      next: (r) => this.result.set(r),
      error: (e) => this.resultError.set(toApiError(e).message),
    });
  }

  protected generate(): void {
    this.download.set('idle');
    this.minutes.generate(this.record().id, this.title());
  }

  /** Quill is loaded on demand: most visits only read the document. */
  protected async startEdit(): Promise<void> {
    if (!this.doc() || this.saving() || this.editing()) return;
    if (!this.quillCtor) this.quillCtor = (await import('quill')).default;
    this.editing.set(true);
  }

  protected cancelEdit(): void {
    this.editing.set(false);
  }

  /** Sends the full editor snapshot; the backend verifies it and the PDF is rendered from it. */
  protected async saveEdit(): Promise<void> {
    const quill = this.quill;
    if (!quill || this.saving()) return;
    const delta = { ops: quill.getContents().ops as QuillOp[] };
    quill.enable(false);
    const saved = await this.minutes.saveEdit(this.record().id, delta, this.title());
    if (saved) {
      this.editing.set(false);
      this.download.set('idle');
    } else if (this.quill === quill) {
      quill.enable(true);
    }
  }

  protected exportPdf(): void {
    if (this.download() === 'preparing') return;
    this.download.set('preparing');
    const fallback = this.docName();
    this.api.downloadPdf(this.record().id).subscribe({
      next: ({ blob, fileName }) => {
        const name = fileName ?? fallback;
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = name;
        a.click();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
        this.download.set('done');
        this.toasts.success('Descărcare finalizată', `${name} a fost salvat în Descărcări`);
      },
      error: (err) => {
        this.download.set('idle');
        this.toasts.error('Exportul a eșuat', toApiError(err).message);
      },
    });
  }

  /** "Copiază ca text" is built on the client from the saved document. */
  protected copyText(): void {
    const text = this.plainText();
    if (!text) return;
    const full = ['Participanți: ' + this.names().join(', '), '', text].join('\n');
    navigator.clipboard?.writeText(full).catch(() => undefined);
    this.copied.set(true);
    this.toasts.success('Copiat în clipboard', 'Proces-verbal: ' + this.title());
    setTimeout(() => this.copied.set(false), 2000);
  }

  protected openMappingFromEmail(): void {
    this.emailOpen.set(false);
    this.mappingExpanded.set(true);
  }
}
