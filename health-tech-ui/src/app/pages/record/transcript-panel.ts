import { Component, input, output } from '@angular/core';

export interface TranscriptLine {
  time: string;
  name: string;
  color: string;
  text: string;
}

/** Slide-over with the record's transcript (`GET /jobs/{id}/result`, loaded by `MomView`). */
@Component({
  selector: 'app-transcript-panel',
  templateUrl: './transcript-panel.html',
  styleUrl: './transcript-panel.scss',
  host: { '(document:keydown.escape)': 'closed.emit()' },
})
export class TranscriptPanel {
  readonly title = input.required<string>();
  readonly turns = input.required<TranscriptLine[]>();
  /** false while `GET /jobs/{id}/result` is pending. */
  readonly loaded = input.required<boolean>();
  readonly error = input<string | null>(null);
  readonly closed = output<void>();
}
