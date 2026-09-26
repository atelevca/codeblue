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
import { RecordDetails } from '../../api/models';
import { ResonaApi, toApiError } from '../../api/resona-api';
import { Breadcrumb } from '../../state/breadcrumb';
import { ProcessingTracker } from '../../state/processing-tracker';
import { Toasts } from '../../state/toasts';
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
    } @else if (record(); as rec) {
      @if (rec.status === 'completed') {
        <app-mom-view [record]="rec" (renamed)="onRenamed($event)" />
      } @else {
        <app-processing-view [record]="rec" [status]="status()" (retry)="retry()" />
      }
    }
  `,
})
export class RecordPage {
  private readonly api = inject(ResonaApi);
  private readonly tracker = inject(ProcessingTracker);
  private readonly toasts = inject(Toasts);
  private readonly crumb = inject(Breadcrumb);

  /** Route param (`rec/:id`). */
  readonly id = input.required<string>();

  protected readonly record = signal<RecordDetails | null>(null);
  protected readonly loadError = signal<string | null>(null);
  protected readonly status = computed(() => this.tracker.statusOf(this.id()));

  constructor() {
    effect(() => {
      const id = this.id();
      untracked(() => this.load(id));
    });

    // The tracker reports completion or failure → reload the record details.
    effect(() => {
      const s = this.status();
      const rec = untracked(this.record);
      if (s && rec && rec.id === s.recordId && s.status !== rec.status) {
        if (s.status === 'completed' || s.status === 'failed') untracked(() => this.load(rec.id));
        else this.record.set({ ...rec, status: s.status, progress: s.progress });
      }
    });

    inject(DestroyRef).onDestroy(() => this.tracker.viewing.set(null));
  }

  protected retry(): void {
    const rec = this.record();
    if (!rec) return;
    this.api.retryProcessing(rec.id).subscribe({
      next: (status) => {
        this.tracker.track(rec.id, rec.title, status);
        this.record.set({
          ...rec,
          status: status.status,
          progress: status.progress,
          error: undefined,
        });
      },
      error: (e) => this.toasts.error('Nu am putut relua procesarea', toApiError(e).message),
    });
  }

  protected onRenamed(title: string): void {
    const rec = this.record();
    if (!rec) return;
    this.record.set({ ...rec, title });
    this.crumb.set('Înregistrări', title);
    this.tracker.rename(rec.id, title);
  }

  private load(id: string): void {
    this.tracker.viewing.set(id);
    this.loadError.set(null);
    if (this.record()?.id !== id) this.record.set(null);
    this.api.getRecord(id).subscribe({
      next: (rec) => {
        this.record.set(rec);
        this.crumb.set('Înregistrări', rec.title);
        const known = this.tracker.statusOf(id);
        if (known && known.status === rec.status) return;
        // Opened directly (e.g. after a reload): fetch the stage details and start polling.
        if (rec.status !== 'completed')
          this.api.getProcessingStatus(id).subscribe((s) => this.tracker.track(id, rec.title, s));
        else
          this.tracker.track(id, rec.title, { recordId: id, status: 'completed', progress: 100 });
      },
      error: (e) => this.loadError.set(toApiError(e).message),
    });
  }
}
