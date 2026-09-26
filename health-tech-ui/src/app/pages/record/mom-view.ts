import { Component, DestroyRef, OnInit, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { FindingKind, Job, TranscriptResult } from '../../api/models';
import { ResonaApi, toApiError } from '../../api/resona-api';
import { speakerColor, typeInfo } from '../../shared/catalog';
import { fmtDate, fmtDur, fmtLongDate } from '../../shared/format';
import { deltaLines, deltaText } from '../../shared/quill';
import { MinutesStore } from '../../state/minutes-store';
import { jobTitle } from '../../state/processing-tracker';
import { SpeakerMap, resolveLink } from '../../state/speaker-map';
import { Toasts } from '../../state/toasts';
import { EmailDialog } from './email-dialog';
import { SpeakerPanel } from './speaker-panel';

type DownloadState = 'idle' | 'preparing' | 'done';

const KIND_LABELS: Record<FindingKind, string> = {
  Unsupported: 'Afirmație fără suport în transcriere',
  Omission: 'Omisiune',
  Contradiction: 'Contradicție',
};

@Component({
  selector: 'app-mom-view',
  imports: [RouterLink, SpeakerPanel, EmailDialog],
  templateUrl: './mom-view.html',
  styleUrl: './mom-view.scss',
})
export class MomView implements OnInit {
  private readonly api = inject(ResonaApi);
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
  private readonly now = signal(Date.now());

  protected readonly title = computed(() => jobTitle(this.record()));
  protected readonly typeLabel = computed(() => typeInfo(this.record().profileKey).label);
  protected readonly state = computed(() => this.minutes.of(this.record().id));
  protected readonly doc = computed(() => {
    const s = this.state();
    return s.kind === 'ready' ? s.doc : null;
  });
  protected readonly errorState = computed(() => {
    const s = this.state();
    return s.kind === 'error' ? s : null;
  });
  protected readonly lines = computed(() => deltaLines(this.doc()?.delta));

  protected readonly elapsed = computed(() => {
    const s = this.state();
    return s.kind === 'generating' ? fmtDur((this.now() - s.startedAt) / 1000) : '';
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

  protected readonly turns = computed(() => {
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
    const tone = !v.completed ? 'unknown' : v.isConsistent ? 'ok' : 'warn';
    const label = !v.completed
      ? 'Verificarea automată nu a fost finalizată — documentul nu este verificat'
      : v.isConsistent
        ? 'Verificat: nicio neconcordanță cu transcrierea'
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
