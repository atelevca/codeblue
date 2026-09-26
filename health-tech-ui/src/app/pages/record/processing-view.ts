import { Component, DestroyRef, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Job } from '../../api/models';
import { STAGES } from '../../shared/catalog';
import { fmtDate, fmtDur, fmtSize, plural } from '../../shared/format';
import { jobTitle } from '../../state/processing-tracker';

const BARS = 64;

/** Backend step captions of the two slow steps: `Распознавание: чанк 3 из 7`, `...: батч 2 из 5`. */
const STEP_COUNTER = /(чанк|батч)\s+(\d+)\s+из\s+(\d+)/i;

@Component({
  selector: 'app-processing-view',
  imports: [RouterLink],
  templateUrl: './processing-view.html',
  styleUrl: './processing-view.scss',
})
export class ProcessingView {
  readonly record = input.required<Job>();

  private readonly tick = signal(0);

  protected readonly title = computed(() => jobTitle(this.record()));
  protected readonly failed = computed(() => this.record().status === 'Failed');
  protected readonly pending = computed(() => this.record().status === 'Pending');
  protected readonly progress = computed(() => Math.floor(this.record().percent));

  private readonly stageIndex = computed(() => {
    if (this.pending()) return -1;
    const p = this.record().percent;
    const i = STAGES.findIndex((st) => p < st.to);
    return i < 0 ? STAGES.length - 1 : i;
  });

  /** "Fragmentul 3 din 7" / "Lotul 2 din 5" from the backend's current step, if any. */
  private readonly counter = computed(() => {
    const m = STEP_COUNTER.exec(this.record().currentStep ?? '');
    if (!m) return null;
    return `${m[1].toLowerCase() === 'чанк' ? 'Fragmentul' : 'Lotul'} ${m[2]} din ${m[3]}`;
  });

  protected readonly meta = computed(() => {
    const r = this.record();
    const sent = fmtDate(r.createdAt).replace('Azi, ', 'azi la ');
    const speakers = r.speakersCount
      ? plural(r.speakersCount, 'vorbitor', 'vorbitori') + ' · '
      : '';
    return `${speakers}${fmtDur(r.durationSec ?? 0)} · ${fmtSize(r.sizeBytes)} · Trimis ${sent}`;
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
    if (this.pending()) return 'Procesarea începe imediat ce există capacitate disponibilă';
    const counter = this.counter();
    return counter ? `${stage.desc} · ${counter}` : stage.desc;
  });

  protected readonly foot = computed(() =>
    this.failed()
      ? 'Oprit'
      : this.pending()
        ? 'Neînceput'
        : 'Durează câteva minute · puteți părăsi pagina',
  );

  protected readonly errorMessage = computed(
    () => this.record().error ?? 'Motivul nu a fost raportat de server.',
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
      p = this.record().percent;
    return STAGES.map((st, i) => {
      const state = i < idx ? 'done' : i === idx ? (failed ? 'failed' : 'active') : 'todo';
      const within = Math.floor(((p - st.from) / (st.to - st.from)) * 100);
      return {
        name: st.name,
        desc: st.desc,
        state,
        right:
          state === 'done'
            ? 'Gata'
            : state === 'failed'
              ? 'Eșuat'
              : state === 'active'
                ? `${Math.max(0, Math.min(100, within))}%`
                : 'În așteptare',
      };
    });
  });

  constructor() {
    const timer = setInterval(() => this.tick.update((t) => t + 1), 100);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }
}
