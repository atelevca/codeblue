import { Injectable, signal } from '@angular/core';

export interface Toast {
  id: number;
  kind: 'success' | 'error';
  title: string;
  body: string;
  actionLabel?: string;
  recordId?: string;
  /** Mounted but not yet faded in, or fading out. */
  hidden: boolean;
}

const LIFETIME_MS = 7000;
const FADE_MS = 250;

@Injectable({ providedIn: 'root' })
export class Toasts {
  private nextId = 1;
  readonly list = signal<Toast[]>([]);

  show(t: Omit<Toast, 'id' | 'hidden'>): void {
    const id = this.nextId++;
    this.list.update((l) => [...l, { ...t, id, hidden: true }]);
    setTimeout(() => this.patch(id, { hidden: false }), 30);
    setTimeout(() => this.patch(id, { hidden: true }), LIFETIME_MS);
    setTimeout(() => this.dismiss(id), LIFETIME_MS + FADE_MS);
  }

  success(title: string, body: string): void {
    this.show({ kind: 'success', title, body });
  }

  error(title: string, body: string): void {
    this.show({ kind: 'error', title, body });
  }

  dismiss(id: number): void {
    this.list.update((l) => l.filter((t) => t.id !== id));
  }

  private patch(id: number, p: Partial<Toast>): void {
    this.list.update((l) => l.map((t) => (t.id === id ? { ...t, ...p } : t)));
  }
}
