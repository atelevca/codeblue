import { Routes } from '@angular/router';
import { NewRecordPage } from './pages/new-record/new-record-page';
import { RecordPage } from './pages/record/record-page';
import { HistoryPage } from './pages/history/history-page';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'new' },
  { path: 'new', component: NewRecordPage },
  { path: 'istoric', component: HistoryPage },
  { path: 'rec/:id', component: RecordPage },
  { path: '**', redirectTo: 'new' },
];
