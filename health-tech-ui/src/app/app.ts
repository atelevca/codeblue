import { Component, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter, map } from 'rxjs';
import { Breadcrumb } from './state/breadcrumb';
import { ProcessingTracker, jobTitle } from './state/processing-tracker';
import { Toast, Toasts } from './state/toasts';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  templateUrl: './app.html',
})
export class App {
  private readonly router = inject(Router);
  protected readonly crumb = inject(Breadcrumb);
  protected readonly tracker = inject(ProcessingTracker);
  protected readonly toasts = inject(Toasts);
  protected readonly title = jobTitle;

  /** "Istoric" stays highlighted while a record is open, as in the design. */
  protected readonly historyActive = toSignal(
    this.router.events.pipe(
      filter((e) => e instanceof NavigationEnd),
      map(() => /^\/(istoric|rec\/)/.test(this.router.url)),
    ),
    { initialValue: false },
  );

  protected openToast(t: Toast): void {
    this.toasts.dismiss(t.id);
    if (t.recordId) this.router.navigate(['/rec', t.recordId]);
  }
}
