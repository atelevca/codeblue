// Per-speaker figures computed from the transcript (`GET /jobs/{id}/result`). Speaker suggestions
// stay a frontend stub: the backend has no people directory yet.
import { TranscriptResult } from '../api/models';
import { PEOPLE } from './catalog';
import { hashText } from './format';

export interface SpeakerStat {
  /** Label from the backend, e.g. `Speaker 1`. */
  label: string;
  /** Talk time in seconds. */
  talkSec: number;
  pct: number;
  /** Start of the first turn, seconds. */
  firstSec: number;
  quote: string;
}

const QUOTE_MAX = 140;

export function speakerStats(result: TranscriptResult): SpeakerStat[] {
  const talk = result.speakers.map(() => 0);
  const first = result.speakers.map(() => -1);
  const quote = result.speakers.map(() => '');
  for (const t of result.turns) {
    const i = result.speakers.indexOf(t.speaker);
    if (i < 0) continue;
    talk[i] += Math.max(0, t.end - t.start);
    if (first[i] < 0) first[i] = t.start;
    if (!quote[i] && t.text.trim()) quote[i] = t.text.trim();
  }
  const sum = talk.reduce((a, b) => a + b, 0) || 1;
  return result.speakers.map((label, i) => ({
    label,
    talkSec: talk[i],
    pct: Math.round((talk[i] / sum) * 100),
    firstSec: Math.max(0, first[i]),
    quote: quote[i].length > QUOTE_MAX ? quote[i].slice(0, QUOTE_MAX - 1) + '…' : quote[i],
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
