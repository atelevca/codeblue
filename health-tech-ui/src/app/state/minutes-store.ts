import { Injectable, inject, signal } from '@angular/core';
import { ID, QuillDelta, SavedDocument } from '../api/models';
import { CodeBlueApi, toApiError } from '../api/codeblue-api';
import { ProcessingTracker } from './processing-tracker';
import { Toasts } from './toasts';

export type MinutesState =
  | { kind: 'loading' }
  /** Never generated (`GET /document/get` → 404). */
  | { kind: 'missing' }
  | { kind: 'generating'; startedAt: number }
  | { kind: 'ready'; doc: SavedDocument }
  /** An edit is being verified and saved; `doc` is the last saved version meanwhile. */
  | { kind: 'saving'; doc: SavedDocument; startedAt: number }
  | { kind: 'error'; message: string; canGenerate: boolean };

/**
 * The minutes (MoM) per record. Generation is one request that runs 6–10 minutes, so it lives in
 * a root service: leaving the page must not abort it.
 */
@Injectable({ providedIn: 'root' })
export class MinutesStore {
  private readonly api = inject(CodeBlueApi);
  private readonly toasts = inject(Toasts);
  private readonly tracker = inject(ProcessingTracker);
  private readonly states = signal<Record<ID, MinutesState>>({});

  of(id: ID): MinutesState {
    return this.states()[id] ?? { kind: 'loading' };
  }

  /** Loads the saved minutes unless they are already known or being generated. */
  load(id: ID): void {
    const s = this.states()[id];
    if (s && s.kind !== 'error') return;
    this.set(id, { kind: 'loading' });
    this.api.getDocument(id).subscribe({
      next: (doc) => this.set(id, { kind: 'ready', doc }),
      error: (e) => {
        const err = toApiError(e);
        this.set(
          id,
          err.status === 404
            ? { kind: 'missing' }
            : { kind: 'error', message: err.message, canGenerate: false },
        );
      },
    });
  }

  generate(id: ID, title: string): void {
    const kind = this.of(id).kind;
    if (kind === 'generating' || kind === 'saving') return;
    const previous = this.of(id);
    this.set(id, { kind: 'generating', startedAt: Date.now() });
    this.api.generateDocument(id).subscribe({
      next: (doc) => {
        this.set(id, { kind: 'ready', doc });
        if (this.tracker.viewing() !== id)
          this.toasts.show({
            kind: 'success',
            title: 'Proces-verbal gata',
            body: title,
            actionLabel: 'Vezi procesul-verbal',
            recordId: id,
          });
      },
      error: (e) => {
        const message = toApiError(e).message;
        this.set(
          id,
          previous.kind === 'ready' ? previous : { kind: 'error', message, canGenerate: true },
        );
        this.toasts.error('Generarea procesului-verbal a eșuat', message);
      },
    });
  }

  /**
   * Verifies and saves an edited document (a full `getContents()` snapshot). The request runs the
   * verification model, so it takes minutes; the previous version stays visible until it returns.
   * Resolves to the saved document, or null when the save failed (the error is toasted).
   */
  saveEdit(id: ID, delta: QuillDelta, title: string): Promise<SavedDocument | null> {
    const previous = this.of(id);
    if (previous.kind !== 'ready') return Promise.resolve(null);
    this.set(id, { kind: 'saving', doc: previous.doc, startedAt: Date.now() });
    return new Promise((resolve) =>
      this.api.saveDocument(id, delta).subscribe({
        next: (doc) => {
          this.set(id, { kind: 'ready', doc });
          const elsewhere = this.tracker.viewing() !== id;
          this.toasts.show({
            kind: 'success',
            title: 'Proces-verbal salvat',
            body: elsewhere ? title : 'PDF-ul și e-mailul folosesc acum versiunea editată',
            ...(elsewhere ? { actionLabel: 'Vezi procesul-verbal', recordId: id } : {}),
          });
          resolve(doc);
        },
        error: (e) => {
          this.set(id, previous);
          this.toasts.error('Salvarea procesului-verbal a eșuat', toApiError(e).message);
          resolve(null);
        },
      }),
    );
  }

  private set(id: ID, state: MinutesState): void {
    this.states.update((m) => ({ ...m, [id]: state }));
  }
}
