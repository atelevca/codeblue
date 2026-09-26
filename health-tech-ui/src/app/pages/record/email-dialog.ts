import {
  Component,
  ElementRef,
  OnInit,
  computed,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { Job } from '../../api/models';
import { CodeBlueApi, toApiError } from '../../api/codeblue-api';
import { emailOf, speakerColor } from '../../shared/catalog';
import { initials } from '../../shared/format';
import { jobTitle } from '../../state/processing-tracker';
import { SpeakerMap, resolveLink } from '../../state/speaker-map';
import { Toasts } from '../../state/toasts';

const isEmail = (x: string) => /^[^\s@,;]+@[^\s@,;]+\.[^\s@,;]{2,}$/.test(x);

/** Demo recipient: Mailpit catches every address, nothing is delivered outside this machine. */
const DEFAULT_RECIPIENT = 'doctor@demo.test';

/**
 * Sends the minutes through the backend's local SMTP server (Mailpit, http://localhost:8025):
 * the PDF is attached on the server, the minutes text optionally goes into the body.
 */
@Component({
  selector: 'app-email-dialog',
  templateUrl: './email-dialog.html',
  styleUrl: './email-dialog.scss',
})
export class EmailDialog implements OnInit {
  private readonly api = inject(CodeBlueApi);
  private readonly toasts = inject(Toasts);
  private readonly speakerMap = inject(SpeakerMap);
  private readonly toInputEl = viewChild<ElementRef<HTMLInputElement>>('toInput');

  readonly record = input.required<Job>();
  /** The minutes as plain text, put into the body when "include" is on. */
  readonly docText = input.required<string>();
  readonly closed = output<void>();
  /** "Asociază": close and open the speaker association panel. */
  readonly openMapping = output<void>();

  /** Speakers mapped to directory people (guests have no e-mail). */
  protected readonly people = computed(() =>
    this.speakerMap.of(this.record().id).flatMap((link, i) => {
      const p = resolveLink(link);
      return p?.person
        ? [
            {
              name: p.name,
              email: emailOf(p.person),
              av: initials(p.name),
              color: speakerColor(i),
              spk: 'V' + (i + 1),
            },
          ]
        : [];
    }),
  );

  protected readonly to = signal<string[]>([]);
  protected readonly toText = signal('');
  protected readonly toError = signal<string | null>(null);
  protected readonly subject = signal('');
  protected readonly message = signal('');
  protected readonly inline = signal(true);
  protected readonly sending = signal(false);

  private readonly title = computed(() => jobTitle(this.record()));
  protected readonly others = computed(() => {
    const mapped = new Set(this.people().map((p) => p.email));
    return this.to().filter((x) => !mapped.has(x));
  });
  protected readonly allOn = computed(
    () => this.people().length > 0 && this.people().every((p) => this.to().includes(p.email)),
  );
  protected readonly canSend = computed(
    () => this.to().length > 0 || isEmail(this.toText().trim()),
  );

  ngOnInit(): void {
    const mapped = this.people().map((p) => p.email);
    this.to.set(mapped.includes(DEFAULT_RECIPIENT) ? mapped : [DEFAULT_RECIPIENT, ...mapped]);
    this.subject.set('Proces-verbal: ' + this.title());
  }

  protected close(): void {
    this.closed.emit();
  }

  protected togglePerson(email: string): void {
    this.to.update((l) => (l.includes(email) ? l.filter((x) => x !== email) : [...l, email]));
    this.toError.set(null);
  }

  protected toggleAll(): void {
    const emails = this.people().map((p) => p.email);
    this.to.update((l) =>
      this.allOn()
        ? l.filter((x) => !emails.includes(x))
        : [...l, ...emails.filter((e) => !l.includes(e))],
    );
    this.toError.set(null);
  }

  protected remove(email: string, e: Event): void {
    e.stopPropagation();
    this.to.update((l) => l.filter((x) => x !== email));
  }

  protected focusTo(): void {
    this.toInputEl()?.nativeElement.focus();
  }

  protected onToInput(value: string): void {
    this.toText.set(value);
    if (/[,;]$/.test(value)) this.commit();
    else this.toError.set(null);
  }

  protected onToKey(e: KeyboardEvent): void {
    if (e.key === 'Enter') {
      e.preventDefault();
      this.commit();
    } else if (e.key === 'Backspace' && !this.toText() && this.others().length) {
      const last = this.others()[this.others().length - 1];
      this.to.update((l) => l.filter((x) => x !== last));
    }
  }

  /** Moves the typed addresses into chips; invalid ones stay in the input with an error. */
  protected commit(): void {
    const parts = this.toText()
      .split(/[,;\s]+/)
      .map((x) => x.trim())
      .filter(Boolean);
    if (!parts.length) {
      this.toError.set(null);
      return;
    }
    const good = parts.filter(isEmail),
      bad = parts.filter((x) => !isEmail(x));
    this.to.update((l) => [...l, ...good.filter((g) => !l.includes(g))]);
    this.toText.set(bad.join(', '));
    this.toError.set(bad.length ? `„${bad[0]}” nu este o adresă de e-mail validă.` : null);
  }

  protected send(): void {
    if (this.sending()) return;
    this.commit();
    if (!this.to().length) {
      this.toError.update((e) => e ?? 'Adăugați cel puțin un destinatar.');
      return;
    }
    const body = [this.message().trim(), this.inline() ? this.docText() : '']
      .filter(Boolean)
      .join('\n\n');
    const subject = this.subject().trim() || 'Proces-verbal: ' + this.title();
    const to = this.to();
    this.sending.set(true);
    this.api.sendEmail(this.record().id, { to, subject, body }).subscribe({
      next: () => {
        this.sending.set(false);
        this.toasts.success(
          'Scrisoarea a fost acceptată de serverul local de e-mail',
          to.length === 1 ? 'Către ' + to[0] : `Către ${to.length} destinatari`,
        );
        this.closed.emit();
      },
      error: (err) => {
        // The dialog stays open with everything typed, so the user can retry.
        this.sending.set(false);
        this.toasts.error('Trimiterea a eșuat', toApiError(err).message);
      },
    });
  }
}
