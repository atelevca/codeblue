import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ExportFormat, Lang, Mom, RecordDetails } from '../../api/models';
import { ResonaApi, toApiError } from '../../api/resona-api';
import {
  EXPORT_EXT,
  EXPORT_FORMATS,
  LANGS,
  SPEAKER_COLORS,
  langName,
  typeInfo,
} from '../../shared/catalog';
import { fmtDate, fmtDur, fmtLongDate } from '../../shared/format';
import { SpeakerMap, resolveLink } from '../../state/speaker-map';
import { Toasts } from '../../state/toasts';
import { EmailDialog } from './email-dialog';
import { SpeakerPanel } from './speaker-panel';

type DownloadState = 'idle' | 'preparing' | 'done';

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

  readonly record = input.required<RecordDetails>();
  readonly renamed = output<string>();

  protected readonly mom = signal<Mom | null>(null);
  protected readonly momError = signal<string | null>(null);
  protected readonly translatingTo = signal<Lang | null>(null);
  protected readonly format = signal<ExportFormat>('DOCX');
  protected readonly download = signal<DownloadState>('idle');
  protected readonly copied = signal(false);
  protected readonly emailOpen = signal(false);
  protected readonly mappingExpanded = signal(false);

  protected readonly langs = LANGS;
  protected readonly formats = EXPORT_FORMATS;
  protected readonly colors = SPEAKER_COLORS;
  protected readonly langName = langName;

  protected readonly lang = computed<Lang>(() => this.mom()?.lang ?? this.record().momLang);
  protected readonly typeLabel = computed(() => typeInfo(this.record().discussionType).label);

  protected readonly names = computed(() => {
    const links = this.speakerMap.of(this.record().id);
    return Array.from(
      { length: this.record().speakersCount },
      (_, i) => resolveLink(links[i])?.name ?? `Vorbitor ${i + 1}`,
    );
  });

  protected readonly meta = computed(() => {
    const r = this.record();
    const people = r.speakersCount === 1 ? '1 participant' : `${r.speakersCount} participanți`;
    return `${fmtLongDate(r.createdAt)} · ${fmtDur(r.durationSec)} · ${people} · ${langName(this.lang())}`;
  });

  protected readonly readyDate = computed(() =>
    fmtDate(this.record().completedAt ?? this.record().createdAt),
  );

  protected readonly actions = computed(() => {
    const names = this.names();
    return (this.mom()?.actionItems ?? []).map((a) => ({
      ...a,
      owner: names[a.ownerSpeakerIndex] ?? `Vorbitor ${a.ownerSpeakerIndex + 1}`,
    }));
  });

  protected readonly topics = computed(() =>
    (this.mom()?.topics ?? []).map((t) => ({ ...t, ts: fmtDur(t.startSec) })),
  );

  protected readonly docName = computed(
    () => `${this.record().title} — proces-verbal.${EXPORT_EXT[this.format()]}`,
  );

  protected readonly details = computed(() => {
    const r = this.record();
    return [
      { k: 'Fișier sursă', v: r.fileName },
      { k: 'Durată', v: fmtDur(r.durationSec) },
      { k: 'Vorbitori', v: String(r.speakersCount) },
      { k: 'Tipul discuției', v: this.typeLabel() },
      { k: 'Limbă', v: langName(this.lang()) },
      { k: 'Timp de procesare', v: fmtDur(r.processingTimeSec ?? 0) },
    ];
  });

  ngOnInit(): void {
    this.api.getMomWhenReady(this.record().id).subscribe({
      next: (m) => this.mom.set(m),
      error: (e) => this.momError.set(toApiError(e).message),
    });
  }

  protected pickLang(code: Lang): void {
    if (code === this.lang() || this.translatingTo()) return;
    this.translatingTo.set(code);
    this.download.set('idle');
    this.api.getMomWhenReady(this.record().id, code).subscribe({
      next: (m) => {
        this.mom.set(m);
        this.translatingTo.set(null);
        this.toasts.success('Proces-verbal actualizat', 'Limba: ' + langName(code));
      },
      error: (e) => {
        this.translatingTo.set(null);
        this.toasts.error('Traducerea a eșuat', toApiError(e).message);
      },
    });
  }

  protected pickFormat(f: ExportFormat): void {
    this.format.set(f);
    this.download.set('idle');
  }

  protected rename(e: Event): void {
    const input = e.target as HTMLInputElement;
    const title = input.value.trim();
    const current = this.record().title;
    if (!title || title === current) {
      input.value = current;
      return;
    }
    this.api.updateRecord(this.record().id, title).subscribe({
      next: (r) => this.renamed.emit(r.title),
      error: (err) => {
        input.value = current;
        this.toasts.error('Redenumirea a eșuat', toApiError(err).message);
      },
    });
  }

  protected exportFile(): void {
    if (this.download() === 'preparing') return;
    this.download.set('preparing');
    const fallback = this.docName();
    this.api.exportMom(this.record().id, this.format(), this.lang()).subscribe({
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

  /** "Copiază ca text" is built on the client from the Mom. */
  protected copyText(): void {
    const m = this.mom();
    if (!m) return;
    const r = this.record();
    const text = [
      '# ' + r.title,
      `Proces-verbal · ${fmtLongDate(r.createdAt)} · ${fmtDur(r.durationSec)}`,
      '',
      'Participanți: ' + this.names().join(', '),
      '',
      '## ' + m.sectionTitles.summary,
      m.summary,
      '',
      '## ' + m.sectionTitles.decisions,
      ...m.decisions.map((d, i) => `${i + 1}. ${d}`),
      '',
      '## ' + m.sectionTitles.actions,
      ...this.actions().map((a) => `- ${a.task} — ${a.owner}, termen ${a.due}`),
      '',
      '## ' + m.sectionTitles.topics,
      ...this.topics().map((t) => `- ${t.ts} ${t.title}: ${t.note}`),
    ].join('\n');
    navigator.clipboard?.writeText(text).catch(() => undefined);
    this.copied.set(true);
    this.toasts.success('Copiat în clipboard', 'Proces-verbal: ' + r.title);
    setTimeout(() => this.copied.set(false), 2000);
  }

  protected openMappingFromEmail(): void {
    this.emailOpen.set(false);
    this.mappingExpanded.set(true);
  }
}
