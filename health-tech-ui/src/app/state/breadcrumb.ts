import { Injectable, signal } from '@angular/core';

/** Header breadcrumb, set by the routed page. */
@Injectable({ providedIn: 'root' })
export class Breadcrumb {
  readonly root = signal('Înregistrare nouă');
  readonly leaf = signal<string | null>(null);

  set(root: string, leaf: string | null = null): void {
    this.root.set(root);
    this.leaf.set(leaf);
  }
}
