// Frontend-only reference data. The backend has no endpoints for these.

/** Speaker colors, by 0-based speaker index. */
export const SPEAKER_COLORS = [
  'oklch(0.72 0.15 272)',
  'oklch(0.78 0.12 190)',
  'oklch(0.82 0.13 80)',
  'oklch(0.72 0.16 345)',
  'oklch(0.77 0.14 145)',
  'oklch(0.72 0.15 35)',
  'oklch(0.75 0.12 235)',
  'oklch(0.82 0.12 115)',
];

export function speakerColor(i: number): string {
  return SPEAKER_COLORS[i % SPEAKER_COLORS.length];
}

/** Speaker count choices in the form (the backend accepts 1..20). */
export const MAX_SPEAKERS = 8;
/** `Uploads:MaxBytes` on the backend. */
export const MAX_FILE_BYTES = 1024 ** 3;
export const AUDIO_EXTENSIONS = ['mp3', 'wav', 'm4a', 'flac', 'ogg', 'aac', 'aiff', 'opus'];
export const SHOWN_FORMATS = ['MP3', 'WAV', 'M4A', 'FLAC', 'OGG', 'AAC'];

export interface DiscussionTypeInfo {
  label: string;
  hint: string;
}

/**
 * RO labels for the profile keys of `GET /profiles`. The list itself comes from the server; a key
 * missing here falls back to the server's `displayName`.
 */
const DISCUSSION_TYPES: Record<string, DiscussionTypeInfo> = {
  medical: {
    label: 'Medical',
    hint: 'Acuze, evaluare, decizii clinice, indicații și note pe pacient.',
  },
  administrative: {
    label: 'Administrativ',
    hint: 'Rezumat, poziții convenite, sarcini și punctele de pe agendă.',
  },
  financial: {
    label: 'Financiar',
    hint: 'Rezumat, cifre și bugete discutate, decizii și sarcini.',
  },
};

/** Used when `GET /profiles` cannot be loaded. */
export const FALLBACK_PROFILE_KEYS = Object.keys(DISCUSSION_TYPES);

export function typeInfo(key: string | undefined, displayName?: string): DiscussionTypeInfo {
  return (key && DISCUSSION_TYPES[key]) || { label: displayName ?? key ?? 'Necunoscut', hint: '' };
}

export interface StageInfo {
  name: string;
  desc: string;
  /** Band of `Job.percent` covered by the stage (backend step weights). */
  from: number;
  to: number;
}

/** Backend steps grouped into four stages: normalize+prepare+VAD → ASR → diarize+align → LLM. */
export const STAGES: StageInfo[] = [
  {
    name: 'Analiză audio',
    desc: 'Normalizarea sunetului și detectarea vorbirii',
    from: 0,
    to: 15,
  },
  {
    name: 'Transcriere',
    desc: 'Conversia vorbirii în text, fragment cu fragment',
    from: 15,
    to: 55,
  },
  {
    name: 'Identificarea vorbitorilor',
    desc: 'Separarea vocilor și atribuirea replicilor',
    from: 55,
    to: 78,
  },
  {
    name: 'Corectarea terminologiei',
    desc: 'Verificarea termenilor de specialitate cu modelul lingvistic',
    from: 78,
    to: 100,
  },
];

export interface Person {
  id: string;
  name: string;
  role: string;
  med: boolean;
}

/** People directory (UI mock). */
export const PEOPLE: Person[] = [
  { id: 'p1', name: 'Dr. Ana Popescu', role: 'Cardiolog · Medicină internă', med: true },
  { id: 'p2', name: 'Dr. Mihai Ionescu', role: 'Șef secție ATI · Anestezie', med: true },
  { id: 'p3', name: 'Andrei Stan', role: 'Asistent șef · ATI', med: true },
  { id: 'p4', name: 'Dr. Elena Rusu', role: 'Oncolog', med: true },
  { id: 'p5', name: 'Dr. Victor Lungu', role: 'Chirurg general', med: true },
  { id: 'p6', name: 'Ioana Marin', role: 'Asistentă · Medicină internă', med: true },
  { id: 'p7', name: 'Dr. Natalia Ceban', role: 'Medic rezident · Medicină de urgență', med: true },
  { id: 'p8', name: 'Radu Popa', role: 'Director financiar · Finanțe', med: false },
  { id: 'p9', name: 'Cristina Dobre', role: 'Controlor financiar · Finanțe', med: false },
  { id: 'p10', name: 'Alexei Morozov', role: 'Manager de cont · Vânzări', med: false },
  { id: 'p11', name: 'Maria Toma', role: 'Consilier juridic · Juridic', med: false },
  { id: 'p12', name: 'Dan Vasile', role: 'Șef de produs · Produs', med: false },
  { id: 'p13', name: 'Olga Smirnova', role: 'Manager operațiuni · Operațiuni', med: false },
  { id: 'p14', name: 'Sorin Enache', role: 'Lider tehnic · Inginerie', med: false },
];

export function emailOf(p: Person): string {
  const base = p.name
    .replace(/^Dr\.\s*/, '')
    .normalize('NFD')
    .replace(/[̀-ͯ]/g, '')
    .toLowerCase()
    .split(/\s+/)
    .join('.');
  return base + (p.med ? '@spitalul-clinic.ro' : '@companie.ro');
}
