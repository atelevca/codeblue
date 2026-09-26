// getSpeakers is frontend-only (schema.md): talk time, share and a sample quote are mocked,
// deterministically per record so they stay stable across renders.
import { PEOPLE, SNIPPETS } from './catalog';
import { hashText, seededRandom } from './format';

export interface SpeakerStat {
  /** Talk time in seconds. */
  talkSec: number;
  pct: number;
  /** Start of the first turn, seconds. */
  firstSec: number;
  quote: string;
}

export function speakerStats(
  recordId: string,
  speakers: number,
  durationSec: number,
): SpeakerStat[] {
  const r = seededRandom(recordId + speakers);
  const weights = Array.from({ length: speakers }, (_, i) => 0.35 + r() * (i === 0 ? 1.4 : 1));
  const total = weights.reduce((a, b) => a + b, 0);
  const talk = Array<number>(speakers).fill(0);
  const first = Array<number>(speakers).fill(-1);
  let t = 0,
    prev = -1;
  while (t < 100) {
    let sp: number;
    do {
      let x = r() * total;
      sp = 0;
      while (x > weights[sp]) x -= weights[sp++];
    } while (sp === prev && speakers > 1);
    const len = Math.min(0.5 + r() * 2.6, 100 - t);
    if (first[sp] < 0) first[sp] = t;
    talk[sp] += len;
    t += len + (r() < 0.35 ? r() * 0.7 : 0);
    prev = sp;
  }
  const sum = talk.reduce((a, b) => a + b, 0) || 1;
  const h = hashText(recordId);
  return talk.map((v, i) => ({
    talkSec: (durationSec * v) / sum,
    pct: Math.round((v / sum) * 100),
    firstSec: (Math.max(0, first[i]) / 100) * durationSec,
    quote: SNIPPETS[(h + i) % SNIPPETS.length],
  }));
}

export interface Suggestion {
  personId: string;
  name: string;
  confidence: number;
}

/** Speaker suggestions stub ("Sugestie · 87% match"), one per unmapped speaker. */
export function speakerSuggestions(
  recordId: string,
  medical: boolean,
  mapped: boolean[],
  usedPersonIds: string[],
): (Suggestion | null)[] {
  const pool = PEOPLE.filter((p) => p.med === medical);
  const h = hashText(recordId);
  const used = new Set(usedPersonIds);
  const seen = new Set<string>();
  return mapped.map((isMapped, i) => {
    if (isMapped) return null;
    for (let k = 0; k < pool.length; k++) {
      const p = pool[(h + i * 3 + k) % pool.length];
      if (!used.has(p.id) && !seen.has(p.id)) {
        seen.add(p.id);
        return { personId: p.id, name: p.name, confidence: Math.max(61, 94 - i * 7 - (h % 5)) };
      }
    }
    return null;
  });
}
