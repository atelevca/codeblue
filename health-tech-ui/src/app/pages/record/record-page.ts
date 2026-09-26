import {
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { Job } from '../../api/models';
import { CodeBlueApi, toApiError } from '../../api/codeblue-api';
import { Breadcrumb } from '../../state/breadcrumb';
import { ProcessingTracker, isActive, jobTitle } from '../../state/processing-tracker';
import { ProcessingView } from './processing-view';
import { MomView } from './mom-view';

@Component({
  selector: 'app-record-page',
  imports: [ProcessingView, MomView],
  template: `
    @if (loadError(); as err) {
      <div class="alert-error" style="max-width: 760px; margin: 0 auto">
        <div class="alert-error__icon">!</div>
        <div style="flex: 1">
          <div class="alert-error__title">Înregistrarea nu a putut fi deschisă</div>
          <div class="alert-error__msg">{{ err }}</div>
        </div>
      </div>
    } @else if (job(); as j) {
      @if (j.status === 'Completed') {
        <app-mom-view [record]="j" />
      } @else {
        <app-processing-view [record]="j" />
      }
    }
  `,
})
export class RecordPage {
  private readonly api = inject(CodeBlueApi);
  private readonly tracker = inject(ProcessingTracker);
  private readonly crumb = inject(Breadcrumb);

  /** Route param (`rec/:id`). */
  readonly id = input.required<string>();

  private readonly loaded = signal<Job | null>(null);
  protected readonly loadError = signal<string | null>(null);

  /** The tracker's copy while it polls, otherwise the one loaded here. */
  protected readonly job = computed(() => {
    const loaded = this.loaded();
    if (!loaded || loaded.id !== this.id()) return null;
    return this.tracker.jobOf(loaded.id) ?? loaded;
  });

  constructor() {
    effect(() => {
      const id = this.id();
      untracked(() => this.load(id));
    });
    inject(DestroyRef).onDestroy(() => this.tracker.viewing.set(null));
  }

  private load(id: string): void {
    this.tracker.viewing.set(id);
    this.loadError.set(null);
    this.api.getJob(id).subscribe({
      next: (job) => {
        this.loaded.set(job);
        this.crumb.set('Istoric', jobTitle(job));
        // Opened directly (e.g. after a reload): start polling it.
        if (isActive(job) || this.tracker.jobOf(id)) this.tracker.track(job);
      },
      error: (e) => this.loadError.set(toApiError(e).message),
    });
  }
}
