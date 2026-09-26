import { Component, ElementRef, computed, inject, signal, viewChild } from '@angular/core';
import { Router } from '@angular/router';
import { toApiError } from '../../api/codeblue-api';
import {
  AUDIO_EXTENSIONS,
  MAX_SPEAKERS,
  SHOWN_FORMATS,
  SPEAKER_COLORS,
  typeInfo,
} from '../../shared/catalog';
import { fmtDur, fmtSize, stripExt } from '../../shared/format';
import { Breadcrumb } from '../../state/breadcrumb';
import { NewRecordStore } from '../../state/new-record-store';
import { Toasts } from '../../state/toasts';

type StepState = 'done' | 'active' | 'todo';

@Component({
  selector: 'app-new-record-page',
  templateUrl: './new-record-page.html',
  styleUrl: './new-record-page.scss',
})
export class NewRecordPage {
  private readonly router = inject(Router);
  private readonly toasts = inject(Toasts);
  protected readonly store = inject(NewRecordStore);
  private readonly fileInput = viewChild.required<ElementRef<HTMLInputElement>>('fileInput');

  protected readonly dragging = signal(false);
  protected readonly formats = SHOWN_FORMATS;
  protected readonly accept = AUDIO_EXTENSIONS.map((e) => '.' + e).join(',') + ',audio/*';
  protected readonly speakerOptions = Array.from({ length: MAX_SPEAKERS }, (_, i) => i + 1);
  protected readonly colors = SPEAKER_COLORS;
  protected readonly types = computed(() =>
    this.store.profiles().map((p) => ({ id: p.key, label: typeInfo(p.key, p.displayName).label })),
  );
  private readonly typeLabel = computed(() => {
    const key = this.store.type();
    return typeInfo(key, this.store.profiles().find((p) => p.key === key)?.displayName).label;
  });

  protected readonly ready = computed(() => this.store.phase() === 'ready');
  protected readonly uploading = computed(() => this.store.phase() === 'uploading');

  protected readonly steps = computed(() => {
    const ready = this.ready();
    const states: StepState[] = [ready ? 'done' : 'active', ready ? 'active' : 'todo', 'todo'];
    return ['Încărcare', 'Configurare', 'Procesare'].map((label, i) => ({
      label,
      state: states[i],
      mark: states[i] === 'done' ? '✓' : String(i + 1),
      line: i < 2,
    }));
  });

  protected readonly fileMeta = computed(() => {
    const f = this.store.file();
    if (!f) return '';
    if (this.uploading()) {
      const p = this.store.progress();
      return `Se încarcă · ${Math.floor(p)}% · ${fmtSize((f.size * p) / 100)} din ${fmtSize(f.size)}`;
    }
    return `${fmtDur(f.durationSec ?? 0)} · ${fmtSize(f.size)} · ${f.ext}`;
  });

  protected readonly nameSuggestions = computed(() => {
    const f = this.store.file();
    if (!f) return [];
    const d = new Date();
    const short = d
      .toLocaleDateString('ro-RO', { day: 'numeric', month: 'short' })
      .replace('.', '');
    const t = this.typeLabel();
    return [stripExt(f.name), `${t} — ${short}`, `${t} — ${d.toLocaleDateString('ro-RO')}`].filter(
      (x, i, a) => a.indexOf(x) === i,
    );
  });

  protected readonly typeHint = computed(() => {
    const hint = typeInfo(this.store.type()).hint;
    return hint
      ? `${this.typeLabel()} — procesul-verbal include: ${hint.charAt(0).toLowerCase()}${hint.slice(1)}`
      : this.typeLabel();
  });

  protected readonly outputLabel = computed(
    () => `${this.typeLabel()} — transcriere cu vorbitori și proces-verbal`,
  );

  protected readonly estLabel = computed(() => {
    if (!this.ready()) return 'Se așteaptă finalizarea încărcării';
    // Transcription runs at roughly 2–3× the audio length on the demo machine.
    const dur = this.store.file()?.durationSec ?? 0;
    return `Timp estimat de procesare: aproximativ ${Math.max(1, Math.round((dur * 2.5) / 60))} min`;
  });

  protected readonly placeholder = computed(() => {
    const f = this.store.file();
    return f ? stripExt(f.name) : '';
  });

  constructor() {
    inject(Breadcrumb).set('Înregistrare nouă');
    this.store.loadProfiles();
  }

  protected browse(e?: Event): void {
    e?.stopPropagation();
    this.fileInput().nativeElement.click();
  }

  protected onInputChange(e: Event): void {
    const input = e.target as HTMLInputElement;
    this.store.handleFile(input.files?.[0]);
    input.value = '';
  }

  protected onDragOver(e: DragEvent): void {
    e.preventDefault();
    this.dragging.set(true);
  }

  protected onDragLeave(e: DragEvent): void {
    const target = e.currentTarget as HTMLElement;
    if (!target.contains(e.relatedTarget as Node | null)) this.dragging.set(false);
  }

  protected onDrop(e: DragEvent): void {
    e.preventDefault();
    this.dragging.set(false);
    this.store.handleFile(e.dataTransfer?.files[0]);
  }

  protected start(): void {
    if (!this.ready() || this.store.starting()) return;
    this.store.start().subscribe({
      next: (id) => {
        this.router.navigate(['/rec', id]);
        window.scrollTo(0, 0);
      },
      error: (e) => this.toasts.error('Procesarea nu a putut începe', toApiError(e).message),
    });
  }
}
