// Read-only rendering of a saved Quill delta, the same subset the backend PDF renderer reads:
// headers, bullet/ordered lists, indent, bold/italic/underline and links.
import { QuillDelta } from '../api/models';

export interface DocRun {
  text: string;
  bold: boolean;
  italic: boolean;
  underline: boolean;
  link: string | null;
}

export interface DocLine {
  kind: 'h1' | 'h2' | 'h3' | 'p' | 'bullet' | 'ordered';
  runs: DocRun[];
  indent: number;
  /** 1-based number of an ordered item within its run of ordered items. */
  n: number;
}

/** Splits the delta into lines; line attributes sit on the `\n` that ends the line. */
export function deltaLines(delta: QuillDelta | null | undefined): DocLine[] {
  const lines: DocLine[] = [];
  let runs: DocRun[] = [];
  let counter = 0;
  for (const op of delta?.ops ?? []) {
    const a = op.attributes ?? {};
    const parts = op.insert.split('\n');
    parts.forEach((text, i) => {
      if (text)
        runs.push({
          text,
          bold: a['bold'] === true,
          italic: a['italic'] === true,
          underline: a['underline'] === true,
          link: typeof a['link'] === 'string' && /^https?:/i.test(a['link']) ? a['link'] : null,
        });
      if (i === parts.length - 1) return;
      const header = Number(a['header'] ?? 0);
      const list = a['list'];
      const kind: DocLine['kind'] =
        header === 1
          ? 'h1'
          : header === 2
            ? 'h2'
            : header >= 3
              ? 'h3'
              : list === 'bullet'
                ? 'bullet'
                : list === 'ordered'
                  ? 'ordered'
                  : 'p';
      counter = kind === 'ordered' ? counter + 1 : 0;
      if (runs.length || kind !== 'p')
        lines.push({ kind, runs, indent: Number(a['indent'] ?? 0), n: counter });
      runs = [];
    });
  }
  if (runs.length) lines.push({ kind: 'p', runs, indent: 0, n: 0 });
  return lines;
}

/** Plain text of the document, for "Copiază ca text" and e-mail bodies. */
export function deltaText(delta: QuillDelta | null | undefined): string {
  return deltaLines(delta)
    .map((l) => {
      const text = l.runs.map((r) => r.text).join('');
      const pad = '  '.repeat(l.indent);
      if (l.kind === 'bullet') return `${pad}- ${text}`;
      if (l.kind === 'ordered') return `${pad}${l.n}. ${text}`;
      return l.kind === 'p' ? text : '\n' + text;
    })
    .join('\n')
    .trim();
}
