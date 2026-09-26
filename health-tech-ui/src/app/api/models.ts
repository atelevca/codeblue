// Types from schema.md (the API contract with the backend).

export type ID = string;
export type ISODate = string;
export type Lang = 'RO' | 'RU' | 'EN';
export type RecordStatus = 'pending' | 'processing' | 'completed' | 'failed';
export type ExportFormat = 'DOCX' | 'PDF' | 'MD';
export type ProcessingStage =
  'audio_analysis' | 'speaker_identification' | 'transcription' | 'mom_drafting';
export type DiscussionType = 'medical' | 'executive' | 'administrative';

export interface ApiError {
  code: string;
  message: string;
  field?: string;
}

export interface RecordSummary {
  id: ID;
  title: string;
  fileName: string;
  sizeBytes: number;
  durationSec: number;
  speakersCount: number;
  discussionType: DiscussionType;
  momLang: Lang;
  status: RecordStatus;
  progress: number;
  createdAt: ISODate;
}

export interface RecordDetails extends RecordSummary {
  startedAt?: ISODate;
  completedAt?: ISODate;
  processingTimeSec?: number;
  estimatedProcessingSec?: number;
  error?: ApiError;
}

export interface ProcessingStatus {
  recordId: ID;
  status: RecordStatus;
  progress: number;
  stage?: ProcessingStage;
  stageProgress?: number;
  etaSec?: number;
  queuePosition?: number;
  error?: ApiError;
}

export interface UploadFileResponse {
  fileId: ID;
  fileName: string;
  sizeBytes: number;
  format: string;
  durationSec: number;
}

export interface SaveRecordRequest {
  fileId: ID;
  title: string;
  speakersCount: number;
  discussionType: DiscussionType;
  momLang: Lang;
}

export interface MomActionItem {
  task: string;
  ownerSpeakerIndex: number;
  due: string;
}

export interface MomTopic {
  startSec: number;
  title: string;
  note: string;
}

export interface Mom {
  recordId: ID;
  lang: Lang;
  discussionType: DiscussionType;
  generatedAt: ISODate;
  sectionTitles: { summary: string; decisions: string; actions: string; topics: string };
  summary: string;
  decisions: string[];
  actionItems: MomActionItem[];
  topics: MomTopic[];
}

/** 202 body of getMom while a translation is generated. */
export interface MomGenerating {
  recordId: ID;
  lang: Lang;
  status: 'generating';
}

export interface SpeakerAssociation {
  speakerIndex: number;
  name: string;
  email?: string;
  role?: string;
  isGuest: boolean;
}

export interface SendEmailRequest {
  to: string[];
  subject: string;
  message?: string;
  attachmentFormat: ExportFormat;
  lang: Lang;
  includeInline: boolean;
}

export interface SendEmailResponse {
  sentTo: string[];
  failed?: { email: string; reason: string }[];
}
