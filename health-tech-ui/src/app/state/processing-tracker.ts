import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, Subscription, forkJoin, interval, catchError, of, tap } from 'rxjs';
import { STATUS_POLL_MS } from '../api/api.config';
import { ID, Job } from '../api/models';
import { CodeBlueApi } from '../api/codeblue-api';
import { stripExt } from '../shared/format';
import { Toasts } from './toasts';

export const isActive = (j: Job) => j.status === 'Pending' || j.status === 'Running';

export const jobTitle = (j: Job) => j.title?.trim() || stripExt(j.fileName);

/**
 * Known records, from `GET /jobs` plus the ones created or opened since. Polls `GET /jobs/{id}`
 * for those being processed; feeds the "În curs" sidebar, the Istoric page, the processing
 * screen and the "ready" / "failed" toasts. Loading the list at start picks up the records the
 * backend is already processing, so a page reload does not lose them.
 */
@Injectable({ providedIn: 'root' })
export class ProcessingTracker {
  private readonly api = inject(CodeBlueApi);
  private readonly toasts = inject(Toasts);
  private poll?: Subscription;

  private readonly jobs = signal<Record<ID, Job>>({});
  /** Record currently open on screen; its completion does not raise a toast. */
  readonly viewing = signal<ID | null>(null);

  /** All known records, newest first. */
  readonly all = computed(() =>
    Object.values(this.jobs()).sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt)),
  );
  readonly active = computed(() => this.all().filter(isActive));

  constructor() {
    this.reload().subscribe({ error: () => undefined });
  }

  /** Re-reads `GET /jobs`; emits the list once it is merged in. */
  reload(): Observable<Job[]> {
    return this.api.listJobs().pipe(
      tap((list) => {
        this.jobs.update((m) => ({ ...m, ...Object.fromEntries(list.map((j) => [j.id, j])) }));
        if (list.some(isActive)) this.ensurePolling();
      }),
    );
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
