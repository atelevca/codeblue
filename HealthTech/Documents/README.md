# Document API și Quill

Documentele sunt asociate unui `jobId`. Editorul folosește **Quill Delta** ca sursă
de adevăr; PDF-ul este randat din același Delta salvat. Formatarea nu trece prin HTML.

| Metodă | Rută | Rezultat |
| --- | --- | --- |
| POST | `/document/save/{jobId}` fără corp | Extrage faptele, generează MOM, convertește în Delta, verifică textul final față de transcript și salvează. |
| POST | `/document/save/{jobId}` cu `{ "delta": { "ops": [...] } }` | Verifică și salvează documentul editat. |
| GET | `/document/get/{jobId}` | Returnează `jobId`, `delta`, `minutesMarkdown`, `verification`, `savedAt`. |
| GET | `/document/downloadpdf/{jobId}` | Descarcă ultima versiune salvată, `application/pdf`. |

`minutesMarkdown` este o reprezentare textuală derivată pentru verificarea LLM;
editorul trebuie să citească și să trimită `delta`. Citatele raportului de verificare
se referă la această reprezentare textuală. Documentele cu discrepanțe se salvează
împreună cu raportul; antetul PDF indică necesitatea revizuirii. Erorile de verificare
nu înlocuiesc versiunea salvată anterior. Salvarea este sincronă și poate dura cât inferența LLM.

## Integrarea editorului

```javascript
const quill = new Quill('#editor', {
  theme: 'snow',
  formats: ['header', 'bold', 'italic', 'underline', 'list', 'indent', 'align', 'link'],
  modules: {
    toolbar: [
      [{ header: [1, 2, 3, false] }],
      ['bold', 'italic', 'underline'],
      [{ list: 'ordered' }, { list: 'bullet' }],
      [{ indent: '-1' }, { indent: '+1' }],
      [{ align: [] }], ['link'], ['clean']
    ]
  }
});

async function readResponse(response) {
  if (!response.ok) {
    const problem = await response.json();
    throw new Error(problem.detail ?? problem.title ?? `HTTP ${response.status}`);
  }
  return response.json();
}

async function generateDocument(jobId) {
  const document = await readResponse(await fetch(`/document/save/${jobId}`, {
    method: 'POST'
  }));
  quill.setContents(document.delta);
  return document.verification;
}

async function loadDocument(jobId) {
  const document = await readResponse(await fetch(`/document/get/${jobId}`));
  quill.setContents(document.delta);
  return document.verification;
}

async function saveDocument(jobId) {
  const document = await readResponse(await fetch(`/document/save/${jobId}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ delta: quill.getContents() })
  }));
  return document.verification;
}

async function downloadCurrentPdf(jobId) {
  await saveDocument(jobId); // Exportul include ultimele editări numai după salvare.
  window.location.assign(`/document/downloadpdf/${jobId}`);
}
```

Se acceptă snapshot-ul complet returnat de `getContents()`, terminat cu newline.
Nu se acceptă operațiile `retain`/`delete` din evenimentul `text-change`, embed-uri
(imagini/video/formule) sau formate în afara listei de mai sus; acestea produc 400,
fără eliminarea tăcută a conținutului. Listele suportate sunt `ordered` și `bullet`.
Tabelele MOM generate sunt convertite în paragrafe cu etichete pentru compatibilitate
cu Quill standard; nu este necesar un plugin de tabele.

## Stocare și erori

Documentul și raportul sunt salvate atomic în
`<Transcripts:OutputFolder>/<jobId>/minutes.document.json`.
404: job/document inexistent; 409: transcriere nefinalizată sau lipsă;
400: Delta invalid; 422: transcript invalid sau limite LLM depășite.
Limitele `Minutes` din SemanticKernel se aplică și generării/verificării prin API.
Citirea și descărcarea nu încarcă modelul LLM. Fluxul audio nu generează automat MOM;
clientul apelează `save` după finalizarea transcrierii.

PDF-ul folosește PDFsharp/MigraDoc și fonturi locale: Arial pe Windows sau DejaVu Sans
pe Linux. `Documents:FontDirectory` poate indica directorul fonturilor; în lipsa lor
exportul returnează 503. Nu sunt descărcate fonturi sau resurse externe la export.

Aspectul PDF urmează blanchetul Medpark (`Ghid-de-pregatire-pentru-ecografie-final.pdf`):
antet pe fiecare pagină cu logo-ul în stânga și motto-ul în dreapta (`Branding/logo.png`,
`Branding/tagline.png`, incluse ca resurse în assembly), linie turcoaz `#007C84` sub antet,
titluri turcoaz, texte auxiliare gri `#625C5B`. Conținutul documentului nu este modificat.
