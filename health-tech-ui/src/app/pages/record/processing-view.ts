import { Component, DestroyRef, computed, inject, input, output, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ProcessingStatus, RecordDetails } from '../../api/models';
import { STAGES } from '../../shared/catalog';
import { fmtDate, fmtDur, fmtSize, plural } from '../../shared/format';

const BARS = 64;

@Component({
  selector: 'app-processing-view',
  imports: [RouterLink],
  templateUrl: './processing-view.html',
  styleUrl: './processing-view.scss',
})
export class ProcessingView {
  readonly record = input.required<RecordDetails>();
  readonly status = input<ProcessingStatus | undefined>();
  readonly retry = output<void>();

  private readonly tick = signal(0);

  protected readonly s = computed<ProcessingStatus>(
    () =>
      this.status() ?? {
        recordId: this.record().id,
        status: this.record().status,
        progress: this.record().progress,
        error: this.record().error,
      },
  );
  protected readonly failed = computed(() => this.s().status === 'failed');
  protected readonly pending = computed(() => this.s().status === 'pending');
  protected readonly progress = computed(() => Math.floor(this.s().progress));

  private readonly stageIndex = computed(() => {
    if (this.pending()) return -1;
    const i = STAGES.findIndex((st) => st.id === this.s().stage);
    return i < 0 ? 0 : i;
  });

  protected readonly meta = computed(() => {
    const r = this.record();
    const sent = fmtDate(r.createdAt).replace('Azi, ', 'azi la ');
    return `${plural(r.speakersCount, 'vorbitor', 'vorbitori')} · ${fmtDur(r.durationSec)} · ${fmtSize(r.sizeBytes)} · Trimis ${sent}`;
  });

  protected readonly headline = computed(() =>
    this.failed()
      ? 'Procesare oprită'
      : this.pending()
        ? 'În așteptare'
        : STAGES[this.stageIndex()].name,
  );

  protected readonly subline = computed(() => {
    const stage = STAGES[Math.max(0, this.stageIndex())];
    if (this.failed()) return `Eșuat la etapa: ${stage.name.toLowerCase()}`;
    if (this.pending()) {
      const q = this.s().queuePosition;
      return (
        'Procesarea începe imediat ce există capacitate disponibilă' +
        (q ? ` · poziția ${q} în coadă` : '')
      );
    }
    return stage.id === 'speaker_identification'
      ? `Separarea a ${this.record().speakersCount} voci`
      : stage.desc;
  });

  protected readonly eta = computed(() => {
    if (this.failed()) return 'Oprit';
    if (this.pending()) return 'Neînceput';
    const eta = this.s().etaSec;
    if (eta === undefined) return 'Se estimează timpul rămas…';
    return eta > 60
      ? `Aproximativ ${Math.ceil(eta / 60)} min rămase`
      : `Aproximativ ${eta} sec rămase`;
  });

  protected readonly errorMessage = computed(
    () => this.s().error?.message ?? this.record().error?.message ?? '',
  );

  protected readonly wave = computed(() => {
    const tick = this.tick(),
      p = this.progress(),
      pending = this.pending(),
      failed = this.failed();
    return Array.from({ length: BARS }, (_, i) => {
      const base = 0.18 + 0.5 * Math.abs(Math.sin(i * 0.9) * Math.cos(i * 0.23));
      const live =
        pending || failed
          ? 0
          : 0.35 * Math.abs(Math.sin(i * 0.37 + tick * 0.28) * Math.cos(i * 0.11 - tick * 0.09));
      return {
        h: pending ? 6 : Math.min(100, (base + live) * 100),
        filled: i / BARS < p / 100,
      };
    });
  });

  protected readonly stages = computed(() => {
    const idx = this.stageIndex(),
      failed = this.failed(),
      speakers = this.record().speakersCount;
    return STAGES.map((st, i) => {
      const state = i < idx ? 'done' : i === idx ? (failed ? 'failed' : 'active') : 'todo';
      return {
        name: st.name,
        desc:
          st.id === 'speaker_identification'
            ? `Separarea și etichetarea a ${speakers} voci`
            : st.desc,
        state,
        right:
          state === 'done'
            ? 'Gata'
            : state === 'failed'
              ? 'Eșuat'
              : state === 'active'
                ? `${Math.floor(this.s().stageProgress ?? 0)}%`
                : 'În așteptare',
      };
    });
  });

  constructor() {
    const timer = setInterval(() => this.tick.update((t) => t + 1), 100);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }
}
