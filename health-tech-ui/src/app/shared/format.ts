const AUDIO_EXT_RE = /\.(mp3|wav|m4a|flac|ogg|aac|aiff|opus)$/i;

export function fmtSize(bytes: number): string {
  return bytes >= 1e9 ? (bytes / 1e9).toFixed(2) + ' GB' : (bytes / 1e6).toFixed(1) + ' MB';
}

export function fmtDur(sec: number): string {
  sec = Math.max(0, Math.floor(sec));
  const h = Math.floor(sec / 3600),
    m = Math.floor((sec % 3600) / 60),
    s = sec % 60;
  const p = (n: number) => String(n).padStart(2, '0');
  return h ? `${h}:${p(m)}:${p(s)}` : `${m}:${p(s)}`;
}

/** "Azi, 14:02" / "Ieri, 09:10" / "12 sept. 2026". */
export function fmtDate(iso: string | number | undefined): string {
  if (iso === undefined) return '';
  const ts = typeof iso === 'number' ? iso : Date.parse(iso);
  const d = new Date(ts),
    now = new Date();
  const time = d.toLocaleTimeString('ro-RO', { hour: 'numeric', minute: '2-digit' });
  const day = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime();
  if (ts >= day) return 'Azi, ' + time;
  if (ts >= day - 864e5) return 'Ieri, ' + time;
  return d.toLocaleDateString('ro-RO', { month: 'short', day: 'numeric', year: 'numeric' });
}

export function fmtLongDate(iso: string): string {
  return new Date(iso).toLocaleDateString('ro-RO', {
    weekday: 'long',
    month: 'long',
    day: 'numeric',
    year: 'numeric',
  });
}

export function stripExt(name: string): string {
  return name.replace(AUDIO_EXT_RE, '');
}

export function initials(name: string): string {
  return name
    .replace(/^Dr\.\s*/, '')
    .split(/\s+/)
    .map((w) => w[0])
    .slice(0, 2)
    .join('')
    .toUpperCase();
}

export function plural(n: number, one: string, many: string): string {
  return `${n} ${n === 1 ? one : many}`;
}

export function hashText(text: string): number {
  let h = 2166136261;
  for (const c of text) {
    h ^= c.charCodeAt(0);
    h = Math.imul(h, 16777619);
  }
  return h >>> 0;
}
