// Frontend-only reference data (see "Frontend-only" in schema.md).
import { DiscussionType, ExportFormat, Lang, ProcessingStage } from '../api/models';

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

export const MAX_SPEAKERS = 8;
export const MAX_FILE_BYTES = 500e6;
export const AUDIO_EXTENSIONS = ['mp3', 'wav', 'm4a', 'flac', 'ogg', 'aac', 'aiff', 'opus'];
export const SHOWN_FORMATS = ['MP3', 'WAV', 'M4A', 'FLAC', 'OGG', 'AAC'];
export const EXPORT_FORMATS: ExportFormat[] = ['DOCX', 'PDF', 'MD'];
export const EXPORT_EXT: Record<ExportFormat, string> = { DOCX: 'docx', PDF: 'pdf', MD: 'md' };

export interface LangInfo {
  code: Lang;
  /** Label on the language buttons. */
  label: string;
  /** Name used in sentences ("Limba: Rusă"). */
  name: string;
}

export const LANGS: LangInfo[] = [
  { code: 'RO', label: 'Română', name: 'Română' },
  { code: 'RU', label: 'Русский', name: 'Rusă' },
  { code: 'EN', label: 'Engleză', name: 'Engleză' },
];

export function langName(code: Lang | undefined): string {
  return LANGS.find((l) => l.code === code)?.name ?? 'Română';
}

export interface DiscussionTypeInfo {
  label: string;
  hint: string;
}

export const DISCUSSION_TYPES: Record<DiscussionType, DiscussionTypeInfo> = {
  medical: {
    label: 'Medical',
    hint: 'Acuze, evaluare, decizii clinice, indicații și note pe pacient.',
  },
  executive: {
    label: 'Executive',
    hint: 'Rezumat, decizii, sarcini și subiecte discutate.',
  },
  administrative: {
    label: 'Administrative',
    hint: 'Rezumat, poziții convenite, sarcini și punctele de pe agendă.',
  },
};

export function typeInfo(t: DiscussionType | undefined): DiscussionTypeInfo {
  return DISCUSSION_TYPES[t ?? 'executive'] ?? DISCUSSION_TYPES.executive;
}

export interface StageInfo {
  id: ProcessingStage;
  name: string;
  desc: string;
}

export const STAGES: StageInfo[] = [
  {
    id: 'audio_analysis',
    name: 'Analiză audio',
    desc: 'Normalizarea nivelurilor și detectarea vorbirii',
  },
  {
    id: 'speaker_identification',
    name: 'Identificarea vorbitorilor',
    desc: 'Separarea și etichetarea fiecărei voci',
  },
  {
    id: 'transcription',
    name: 'Transcriere',
    desc: 'Conversia vorbirii în text pentru fiecare vorbitor',
  },
  {
    id: 'mom_drafting',
    name: 'Redactarea procesului-verbal',
    desc: 'Redactarea rezumatului, deciziilor și sarcinilor',
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

/** Sample quotes shown next to each detected voice (UI mock). */
export const SNIPPETS = [
  'Să începem cu cele mai recente rezultate și continuăm de acolo.',
  'Sunt de acord, dar aș vrea să verific cifrele înainte să ne angajăm.',
  'Din partea mea totul e gata, ne mai trebuie doar o dată.',
  'Putem reveni la acest punct la finalul întâlnirii?',
  'Am notat, mă ocup eu și revin cu un răspuns până vineri.',
  'Mi se pare important să stabilim clar cine răspunde de ce.',
];
