import { Injectable, inject, signal } from '@angular/core';
import { ID, SpeakerAssociation } from '../api/models';
import { ResonaApi, toApiError } from '../api/resona-api';
import { PEOPLE, Person, emailOf } from '../shared/catalog';
import { Toasts } from './toasts';

/** A speaker mapped to a directory person or to an external guest. */
export type SpeakerLink = { personId: string } | { guest: string };

export interface ResolvedPerson {
  name: string;
  role: string;
  guest: boolean;
  person?: Person;
}

export function resolveLink(link: SpeakerLink | null | undefined): ResolvedPerson | null {
  if (!link) return null;
  if ('guest' in link) return { name: link.guest, role: 'Participant extern', guest: true };
  const p = PEOPLE.find((x) => x.id === link.personId);
  return p ? { name: p.name, role: p.role, guest: false, person: p } : null;
}

/**
 * Speaker ↔ person associations per record. The directory is frontend-only, so the state lives
 * here and every change is sent with speakerAssociation (PUT replaces all).
 */
@Injectable({ providedIn: 'root' })
export class SpeakerMap {
  private readonly api = inject(ResonaApi);
  private readonly toasts = inject(Toasts);
  private readonly links = signal<Record<ID, (SpeakerLink | null)[]>>({});

  of(recordId: ID): (SpeakerLink | null)[] {
    return this.links()[recordId] ?? [];
  }

  set(recordId: ID, index: number, link: SpeakerLink | null): void {
    const arr = [...this.of(recordId)];
    // A directory person can voice only one speaker.
    if (link && 'personId' in link)
      arr.forEach((m, k) => {
        if (m && 'personId' in m && m.personId === link.personId) arr[k] = null;
      });
    arr[index] = link;
    this.save(recordId, arr);
  }

  setMany(recordId: ID, changes: Map<number, SpeakerLink>): void {
    const arr = [...this.of(recordId)];
    changes.forEach((link, i) => (arr[i] = link));
    this.save(recordId, arr);
  }

  private save(recordId: ID, arr: (SpeakerLink | null)[]): void {
    const previous = this.of(recordId);
    this.links.update((m) => ({ ...m, [recordId]: arr }));
    const speakers: SpeakerAssociation[] = [];
    arr.forEach((link, speakerIndex) => {
      const r = resolveLink(link);
      if (!r) return;
      speakers.push(
        r.person
          ? { speakerIndex, name: r.name, email: emailOf(r.person), role: r.role, isGuest: false }
          : { speakerIndex, name: r.name, isGuest: true },
      );
    });
    this.api.speakerAssociation(recordId, speakers).subscribe({
      error: (e) => {
        this.links.update((m) => ({ ...m, [recordId]: previous }));
        this.toasts.error('Asocierea nu a fost salvată', toApiError(e).message);
      },
    });
  }
}
