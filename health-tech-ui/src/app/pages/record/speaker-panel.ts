import { Component, DestroyRef, computed, inject, input, model, signal } from '@angular/core';
import { RecordDetails } from '../../api/models';
import { PEOPLE, SPEAKER_COLORS } from '../../shared/catalog';
import { fmtDur, initials } from '../../shared/format';
import { speakerStats, speakerSuggestions } from '../../shared/speaker-stats';
import { SpeakerLink, SpeakerMap, resolveLink } from '../../state/speaker-map';
import { Toasts } from '../../state/toasts';

@Component({
  selector: 'app-speaker-panel',
  templateUrl: './speaker-panel.html',
  styleUrl: './speaker-panel.scss',
})
export class SpeakerPanel {
  private readonly map = inject(SpeakerMap);
  private readonly toasts = inject(Toasts);

  readonly record = input.required<RecordDetails>();
  readonly expanded = model(false);

  protected readonly pickFor = signal<number | null>(null);
  protected readonly pickQ = signal('');
  protected readonly playing = signal<number | null>(null);
  private playTimer?: ReturnType<typeof setTimeout>;

  private readonly links = computed(() => this.map.of(this.record().id));
  private readonly count = computed(() => this.record().speakersCount);

  private readonly suggestions = computed(() => {
    const links = this.links();
    const mapped = Array.from({ length: this.count() }, (_, i) => !!links[i]);
    const used = links.flatMap((l) => (l && 'personId' in l ? [l.personId] : []));
    return speakerSuggestions(
      this.record().id,
      this.record().discussionType === 'medical',
      mapped,
      used,
    );
  });

  protected readonly mappedCount = computed(() => this.links().filter(Boolean).length);
  protected readonly suggestionCount = computed(() => this.suggestions().filter(Boolean).length);

  protected readonly speakers = computed(() => {
    const r = this.record(),
      links = this.links(),
      sugg = this.suggestions();
    const stats = speakerStats(r.id, r.speakersCount, r.durationSec);
    return stats.map((st, i) => {
      const p = resolveLink(links[i]);
      return {
        i,
        label: `Vorbitor ${i + 1}`,
        color: SPEAKER_COLORS[i],
        time: fmtDur(st.talkSec),
        pct: st.pct + '%',
        ts: fmtDur(st.firstSec),
        quote: st.quote,
        person: p,
        av: p ? initials(p.name) : '?',
        avBg: p ? (p.guest ? '#F3F2EF' : SPEAKER_COLORS[i]) : '#fff',
        suggestion: sugg[i],
      };
    });
  });

  protected readonly options = computed(() => {
    const i = this.pickFor();
    if (i === null) return [];
    const q = this.pickQ().trim().toLowerCase();
    const med = this.record().discussionType === 'medical';
    const links = this.links();
    const sugg = this.suggestions()[i];
    return PEOPLE.filter((p) => !q || `${p.name} ${p.role}`.toLowerCase().includes(q))
      .sort((a, b) => Number(b.med === med) - Number(a.med === med))
      .map((p) => {
        const at = links.findIndex((l) => !!l && 'personId' in l && l.personId === p.id);
        const mine = at === i,
          suggested = sugg?.personId === p.id;
        return {
          person: p,
          av: initials(p.name),
          mine,
          tag: mine ? 'Selectat' : at >= 0 ? `Vorbitor ${at + 1}` : suggested ? 'Sugerat' : '',
          tagAccent: mine || suggested,
        };
      });
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => clearTimeout(this.playTimer));
  }

  protected toggleExpanded(): void {
    this.expanded.update((v) => !v);
    this.pickFor.set(null);
  }

  protected togglePicker(i: number): void {
    this.pickFor.update((v) => (v === i ? null : i));
    this.pickQ.set('');
  }

  /** Voice sample stub: toggles a "playing" state for a few seconds. */
  protected play(i: number): void {
    clearTimeout(this.playTimer);
    if (this.playing() === i) {
      this.playing.set(null);
      return;
    }
    this.playing.set(i);
    this.playTimer = setTimeout(() => this.playing.set(null), 3200);
  }

  protected link(i: number, link: SpeakerLink | null): void {
    this.map.set(this.record().id, i, link);
    this.pickFor.set(null);
    this.pickQ.set('');
  }

  protected addGuest(i: number): void {
    const name = this.pickQ()
      .trim()
      .replace(/(^|\s)\S/g, (c) => c.toUpperCase());
    if (name) this.link(i, { guest: name });
  }

  protected acceptAll(): void {
    const changes = new Map<number, SpeakerLink>();
    this.suggestions().forEach((g, i) => g && changes.set(i, { personId: g.personId }));
    if (!changes.size) return;
    this.map.setMany(this.record().id, changes);
    this.pickFor.set(null);
    this.toasts.success(
      'Vorbitori asociați',
      (changes.size > 1 ? `${changes.size} sugestii aplicate` : 'O sugestie aplicată') +
        ' în procesul-verbal',
    );
  }
}
