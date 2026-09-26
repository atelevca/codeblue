import { Injectable, computed, inject, signal } from '@angular/core';
import { Subscription, forkJoin, interval, catchError, of } from 'rxjs';
import { STATUS_POLL_MS } from '../api/api.config';
import { ID, ProcessingStatus } from '../api/models';
import { ResonaApi } from '../api/resona-api';
import { Toasts } from './toasts';

export interface TrackedRecord {
  id: ID;
  title: string;
  status: ProcessingStatus;
}

const isActive = (s: ProcessingStatus) => s.status === 'pending' || s.status === 'processing';

/**
 * Polls getProcessingStatus for records processed in this session. Feeds the "În curs" sidebar,
 * the processing screen and the "ready" / "failed" toasts.
 */
@Injectable({ providedIn: 'root' })
export class ProcessingTracker {
  private readonly api = inject(ResonaApi);
  private readonly toasts = inject(Toasts);
  private poll?: Subscription;

  private readonly records = signal<Record<ID, TrackedRecord>>({});
  /** Record currently open on screen; its completion does not raise a toast. */
  readonly viewing = signal<ID | null>(null);

  readonly active = computed(() => Object.values(this.records()).filter((r) => isActive(r.status)));

  statusOf(id: ID): ProcessingStatus | undefined {
    return this.records()[id]?.status;
  }

  track(id: ID, title: string, status: ProcessingStatus): void {
    this.set({ id, title, status });
    if (isActive(status)) this.ensurePolling();
  }

  rename(id: ID, title: string): void {
    const r = this.records()[id];
    if (r) this.set({ ...r, title });
  }

  private set(r: TrackedRecord): void {
    this.records.update((m) => ({ ...m, [r.id]: r }));
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
    forkJoin(
      active.map((r) => this.api.getProcessingStatus(r.id).pipe(catchError(() => of(r.status)))),
    ).subscribe((statuses) =>
      statuses.forEach((status, i) => {
        const r = active[i];
        this.set({ ...r, status });
        if (!isActive(status)) this.notify(r, status);
      }),
    );
  }

  private notify(r: TrackedRecord, status: ProcessingStatus): void {
    const viewing = this.viewing() === r.id;
    if (status.status === 'completed' && !viewing)
      this.toasts.show({
        kind: 'success',
        title: 'Proces-verbal gata',
        body: r.title,
        actionLabel: 'Vezi rezultatul',
        recordId: r.id,
      });
    if (status.status === 'failed')
      this.toasts.show({
        kind: 'error',
        title: 'Procesare eșuată',
        body: r.title,
        actionLabel: viewing ? undefined : 'Vezi detalii',
        recordId: r.id,
      });
  }
}
