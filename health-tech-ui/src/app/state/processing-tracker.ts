import { Injectable, computed, inject, signal } from '@angular/core';
import { Subscription, forkJoin, interval, catchError, of } from 'rxjs';
import { STATUS_POLL_MS } from '../api/api.config';
import { ID, Job } from '../api/models';
import { ResonaApi } from '../api/resona-api';
import { stripExt } from '../shared/format';
import { Toasts } from './toasts';

export const isActive = (j: Job) => j.status === 'Pending' || j.status === 'Running';

export const jobTitle = (j: Job) => j.title?.trim() || stripExt(j.fileName);

/**
 * Polls `GET /jobs/{id}` for records being processed. Feeds the "În curs" sidebar, the
 * processing screen and the "ready" / "failed" toasts. On start it picks up the records the
 * backend is already processing, so a page reload does not lose them.
 */
@Injectable({ providedIn: 'root' })
export class ProcessingTracker {
  private readonly api = inject(ResonaApi);
  private readonly toasts = inject(Toasts);
  private poll?: Subscription;

  private readonly jobs = signal<Record<ID, Job>>({});
  /** Record currently open on screen; its completion does not raise a toast. */
  readonly viewing = signal<ID | null>(null);

  readonly active = computed(() => Object.values(this.jobs()).filter(isActive));

  constructor() {
    this.api.listJobs().subscribe({
      next: (list) => list.filter(isActive).forEach((j) => this.track(j)),
      error: () => undefined,
    });
  }

  jobOf(id: ID): Job | undefined {
    return this.jobs()[id];
  }

  track(job: Job): void {
    this.jobs.update((m) => ({ ...m, [job.id]: job }));
    if (isActive(job)) this.ensurePolling();
  }

  private ensurePolling(): void {
    if (this.poll && !this.poll.closed) return;
    this.poll = interval(STATUS_POLL_MS).subscribe(() => this.refresh());
  }

  private refresh(): void {
    const active = this.active();
    if (!active.length) {
      this.poll?.unsubscribe();
      return;
    }
    forkJoin(active.map((j) => this.api.getJob(j.id).pipe(catchError(() => of(j))))).subscribe(
      (jobs) =>
        jobs.forEach((job) => {
          this.track(job);
          if (!isActive(job)) this.notify(job);
        }),
    );
  }

  private notify(job: Job): void {
    const viewing = this.viewing() === job.id;
    if (job.status === 'Completed' && !viewing)
      this.toasts.show({
        kind: 'success',
        title: 'Transcriere gata',
        body: jobTitle(job),
        actionLabel: 'Vezi rezultatul',
        recordId: job.id,
      });
    if (job.status === 'Failed')
      this.toasts.show({
        kind: 'error',
        title: 'Procesare eșuată',
        body: jobTitle(job),
        actionLabel: viewing ? undefined : 'Vezi detalii',
        recordId: job.id,
      });
  }
}
