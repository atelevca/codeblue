import { Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { Job, JobStatus } from '../../api/models';
import { toApiError } from '../../api/codeblue-api';
import { speakerColor, typeInfo } from '../../shared/catalog';
import { fmtDate, fmtDur, fmtSize } from '../../shared/format';
import { Breadcrumb } from '../../state/breadcrumb';
import { ProcessingTracker, isActive, jobTitle } from '../../state/processing-tracker';

type FilterKey = 'all' | 'active' | 'completed' | 'failed';

const FILTERS: { key: FilterKey; label: string; test: (j: Job) => boolean }[] = [
  { key: 'all', label: 'Toate', test: () => true },
  { key: 'active', label: 'În curs', test: isActive },
  { key: 'completed', label: 'Finalizat', test: (j) => j.status === 'Completed' },
  { key: 'failed', label: 'Eșuat', test: (j) => j.status === 'Failed' },
];

const BADGES: Partial<Record<JobStatus, { cls: string; label: string }>> = {
  Pending: { cls: 'pending', label: 'În coadă' },
  Running: { cls: 'processing', label: 'În procesare' },
  Completed: { cls: 'completed', label: 'Finalizat' },
  Failed: { cls: 'failed', label: 'Eșuat' },
};

/** Istoric — every record the backend knows (`GET /jobs`), newest first. */
@Component({
  selector: 'app-history-page',
  imports: [RouterLink],
  templateUrl: './history-page.html',
})
export class HistoryPage {
  private readonly router = inject(Router);
  protected readonly tracker = inject(ProcessingTracker);

  protected readonly loading = signal(true);
  protected readonly loadError = signal<string | null>(null);
  protected readonly filter = signal<FilterKey>('all');
  protected readonly search = signal('');

  protected readonly filters = computed(() =>
    FILTERS.map((f) => ({ ...f, count: this.tracker.all().filter(f.test).length })),
  );

  protected readonly rows = computed(() => {
    const test = FILTERS.find((f) => f.key === this.filter())!.test;
    const q = this.search().trim().toLowerCase();
    return this.tracker
      .all()
      .filter(test)
      .filter((j) => !q || `${jobTitle(j)} ${j.fileName}`.toLowerCase().includes(q))
      .map((j) => ({
        job: j,
        name: jobTitle(j),
        sub: `${typeInfo(j.profileKey).label} · ${fmtSize(j.sizeBytes)}`,
        date: fmtDate(j.createdAt),
        dur: j.durationSec ? fmtDur(j.durationSec) : '—',
        dots: Array.from({ length: Math.min(j.speakersCount ?? 0, 4) }, (_, i) => speakerColor(i)),
        badge: BADGES[j.status] ?? { cls: 'pending', label: j.status },
        action: j.status === 'Completed' ? 'Deschide' : j.status === 'Failed' ? 'Detalii' : 'Vezi',
      }));
  });

  constructor() {
    inject(Breadcrumb).set('Istoric');
    this.tracker.reload().subscribe({
      next: () => this.loading.set(false),
      error: (e) => {
        this.loading.set(false);
        this.loadError.set(toApiError(e).message);
      },
    });
  }

  protected open(job: Job): void {
    this.router.navigate(['/rec', job.id]);
  }
}
