import { Injectable, signal } from '@angular/core';
import { ID } from '../api/models';
import { PEOPLE, Person } from '../shared/catalog';

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
 * Speaker ↔ person associations per record, index = position in `TranscriptResult.speakers`.
 * Kept on the client only: the backend has no speaker-binding endpoint yet
 * (`PUT /jobs/{id}/speakers` is planned, see ui-integration.md §11).
 */
@Injectable({ providedIn: 'root' })
export class SpeakerMap {
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
    this.links.update((m) => ({ ...m, [recordId]: arr }));
  }
}
