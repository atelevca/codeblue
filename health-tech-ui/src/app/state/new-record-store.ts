import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, Subscription, map, tap } from 'rxjs';
import { Profile, UploadedFile } from '../api/models';
import { ResonaApi, toApiError } from '../api/resona-api';
import {
  AUDIO_EXTENSIONS,
  FALLBACK_PROFILE_KEYS,
  MAX_FILE_BYTES,
  typeInfo,
} from '../shared/catalog';
import { fmtSize, stripExt } from '../shared/format';
import { ProcessingTracker } from './processing-tracker';

export type UploadPhase = 'empty' | 'uploading' | 'ready' | 'error';

export interface PickedFile {
  name: string;
  size: number;
  ext: string;
  durationSec?: number;
}

/** Titles by ProblemDetails `title` of `POST /files`. */
const UPLOAD_ERROR_TITLES: Record<string, string> = {
  NotAudio: 'Fișierul nu este audio',
  CorruptedAudio: 'Fișier audio deteriorat',
  FfmpegUnavailable: 'Serviciu indisponibil',
  413: 'Fișierul este prea mare',
};

const SAMPLE_NAME = 'Masă rotundă fondatori — ep. 12.wav';

/** State of the "Înregistrare nouă" flow; kept in a service so it survives navigation. */
@Injectable({ providedIn: 'root' })
export class NewRecordStore {
  private readonly api = inject(ResonaApi);
  private readonly tracker = inject(ProcessingTracker);
  private uploadSub?: Subscription;

  readonly phase = signal<UploadPhase>('empty');
  readonly file = signal<PickedFile | null>(null);
  readonly loaded = signal(0);
  readonly error = signal<{ title: string; message: string } | null>(null);
  readonly uploaded = signal<UploadedFile | null>(null);

  /** `GET /profiles`; the discussion types offered in the form. */
  readonly profiles = signal<Profile[]>([]);
  readonly recName = signal('');
  readonly speakers = signal(2);
  readonly type = signal('medical');
  readonly starting = signal(false);

  readonly progress = computed(() => {
    const f = this.file();
    return f && f.size ? Math.min(100, (this.loaded() / f.size) * 100) : 0;
  });

  loadProfiles(): void {
    if (this.profiles().length) return;
    this.api.getProfiles().subscribe({
      next: (list) => this.useProfiles(list),
      error: () =>
        this.useProfiles(
          FALLBACK_PROFILE_KEYS.map((key) => ({ key, displayName: typeInfo(key).label })),
        ),
    });
  }

  handleFile(f: File | undefined): void {
    if (!f) return;
    const ext = (f.name.includes('.') ? f.name.split('.').pop()! : '').toLowerCase();
    if (!AUDIO_EXTENSIONS.includes(ext))
      return this.fail(
        'Format de fișier neacceptat',
        `„${f.name}” nu este un fișier audio pe care îl putem procesa. Încărcați un fișier MP3, WAV, M4A, FLAC, OGG sau AAC.`,
      );
    if (f.size > MAX_FILE_BYTES)
      return this.fail(
        'Fișierul este prea mare',
        `Acest fișier are ${fmtSize(f.size)}. Limita este de 1 GB — încercați să-l exportați ca MP3 sau să eliminați pauzele.`,
      );
    if (f.size === 0)
      return this.fail(
        'Fișierul este gol',
        'Acest fișier nu conține date audio. Verificați exportul și încercați din nou.',
      );
    this.upload(f, ext.toUpperCase());
  }

  /** "Folosiți o înregistrare demonstrativă": uploads a generated silent WAV. */
  useSample(): void {
    this.upload(new File([silentWav(8)], SAMPLE_NAME, { type: 'audio/wav' }), 'WAV');
    this.speakers.set(3);
  }

  remove(): void {
    this.uploadSub?.unsubscribe();
    this.phase.set('empty');
    this.file.set(null);
    this.uploaded.set(null);
    this.error.set(null);
  }

  clearError(): void {
    this.error.set(null);
    this.phase.set('empty');
  }

  /** `POST /jobs` saves the card and starts processing; emits the record id (= fileId). */
  start(): Observable<string> {
    const up = this.uploaded()!;
    const title = this.recName().trim() || stripExt(up.fileName);
    this.starting.set(true);
    return this.api
      .saveRecord({
        fileId: up.fileId,
        title,
        speakersCount: this.speakers(),
        discussionType: this.type(),
      })
      .pipe(
        tap((job) => this.tracker.track(job)),
        map((job) => job.id),
        tap({
          next: () => this.reset(),
          error: () => this.starting.set(false),
        }),
      );
  }

  private reset(): void {
    this.remove();
    this.recName.set('');
    this.speakers.set(2);
    this.starting.set(false);
  }

  private useProfiles(list: Profile[]): void {
    this.profiles.set(list);
    if (list.length && !list.some((p) => p.key === this.type()))
      this.type.set(list.find((p) => p.key === 'medical')?.key ?? list[0].key);
  }

  private fail(title: string, message: string): void {
    this.uploadSub?.unsubscribe();
    this.file.set(null);
    this.error.set({ title, message });
    this.phase.set('error');
  }

  private upload(f: File, ext: string): void {
    this.uploadSub?.unsubscribe();
    this.error.set(null);
    this.uploaded.set(null);
    this.file.set({ name: f.name, size: f.size, ext });
    this.loaded.set(0);
    this.phase.set('uploading');
    this.recName.set(stripExt(f.name));
    this.uploadSub = this.api.uploadFile(f).subscribe({
      next: (e) => {
        if (e.kind === 'progress') {
          this.loaded.set(e.loaded);
          return;
        }
        this.uploaded.set(e.file);
        // `format` is ffprobe's synonym list (`mov,mp4,m4a,...`), so the extension stays.
        this.file.update((pf) => pf && { ...pf, durationSec: e.file.durationSec });
        this.loaded.set(f.size);
        this.phase.set('ready');
      },
      error: (err) => {
        const api = toApiError(err);
        this.fail(
          UPLOAD_ERROR_TITLES[api.code] ??
            UPLOAD_ERROR_TITLES[api.status ?? ''] ??
            'Încărcarea a eșuat',
          api.message,
        );
      },
    });
  }
}

/** A mono 16 kHz 16-bit PCM WAV of `seconds` of silence. */
function silentWav(seconds: number): ArrayBuffer {
  const rate = 16000,
    samples = rate * seconds,
    buf = new ArrayBuffer(44 + samples * 2),
    v = new DataView(buf);
  const str = (o: number, s: string) =>
    [...s].forEach((c, i) => v.setUint8(o + i, c.charCodeAt(0)));
  str(0, 'RIFF');
  v.setUint32(4, 36 + samples * 2, true);
  str(8, 'WAVE');
  str(12, 'fmt ');
  v.setUint32(16, 16, true);
  v.setUint16(20, 1, true);
  v.setUint16(22, 1, true);
  v.setUint32(24, rate, true);
  v.setUint32(28, rate * 2, true);
  v.setUint16(32, 2, true);
  v.setUint16(34, 16, true);
  str(36, 'data');
  v.setUint32(40, samples * 2, true);
  return buf;
}
