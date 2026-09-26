# Proces-verbal: extragerea faptelor, randare în cod, verificare

`IMeetingMinutesGenerator` este înregistrat prin apelul existent
`AddMedicalTermCorrection(configuration)` și folosește modelul local cu rolul
`LlmModelRole.Minutes` (`Llm:Minutes:ModelFile`, implicit Qwen2.5-7B; corectarea termenilor
rulează pe modelul mai mic din `Llm:ModelFile`).

```csharp
using SemanticKernel.Minutes;

// Injectați IMeetingMinutesGenerator în serviciul apelant.
MeetingMinutesResult result = await generator.GenerateAsync(transcript, cancellationToken);
MeetingFacts facts = result.Facts;
string markdown = result.MinutesMarkdown;
MinutesVerification verification = result.Verification;
bool consistent = verification.IsConsistent;
```

1. **Fact Extraction and Summary**: transcriptul (și, dacă există, metadatele:
   titlul înregistrării și persoanele asociate vorbitorilor, `MeetingMetadata`) este
   transformat în JSON cu structura unui proces-verbal: `meeting` (title, date, time,
   location), `participants` (chair, secretary, present cu name/role, absent),
   `agenda_explicit`, `agenda` (id, topic, discussion), `decisions`, `actions`
   (description, responsible, deadline), `open_issues`, `next_meeting`, `summary`.
   Deciziile, acțiunile și problemele poartă `agenda_id`, punctul din ordinea de zi la
   care se referă (sau `null`). Numele câmpurilor sunt snake_case, ca în prompturi
   (`JsonNamingPolicy.SnakeCaseLower`). Normalizarea (`NormalizeFacts`) respinge listele
   lipsă sau intrările goale, pune `Nespecificat` în orice câmp text gol, renumerotează
   agenda 1..n și anulează referințele `agenda_id` care nu indică un punct existent.
2. **Randarea documentului** (`RenderMinutes` → `MinutesRenderer`, fără apel la model): procesul-verbal
   Markdown este construit integral în cod din faptele normalizate - antetul (temă, dată, oră, loc),
   Participanți, Ordinea de zi (cu nota despre agenda dedusă), Desfășurarea ședinței (un
   subtitlu „### id. topic” cu textul din `discussion`), tabelele Decizii, Acțiuni și Probleme
   deschise cu coloana „Punct” (agenda_id sau „–”), Următoarea ședință, Rezumat și Semnături, în
   ordinea și cu textele fixe ale șablonului. Vechea etapă „redactează procesul-verbal din aceste
   fapte” costa ~96 s de 7B pe CPU și nu adăuga nimic ce faptele nu conțineau deja; acum fiecare
   valoare este copiată, deci niciun element nu poate fi omis, mutat la alt punct sau completat cu
   o presupunere, iar lungimea documentului nu este limitată de contextul modelului.
3. **Minutes Verification**: un nou apel al modelului compară documentul final direct
   cu transcriptul original și metadatele. Raportul în română conține concluzia și
   discrepanțele: afirmații fără suport, omisiuni, contradicții și atribuiri greșite
   (`Unsupported`, `Omission`, `Contradiction`, `Misattribution`), fiecare cu secțiunea
   documentului (`section`, opțional), citate exacte și corectări sugerate. Citatele sunt
   verificate în cod: `transcriptQuote` trebuie să apară în transcript sau într-o valoare
   din metadate, `documentQuote` în document. Compararea tolerează spațiile, majusculele,
   ş/ș, ţ/ț, ghilimelele tipografice și punctuația de la capete; citatul returnat este
   fragmentul exact din sursă. Răspunsul ca întreg (`summary` + `findings`) trebuie să fie
   valid, altfel etapa se reia; o constatare individuală al cărei citat nu se regăsește
   sau căreia îi lipsește un câmp obligatoriu este eliminată și numărată în
   `DiscardedFindings`, fără a anula restul verificării. `IsConsistent` este adevărat numai
   când nu există discrepanțe și nicio constatare eliminată. Câmpul `Pending` este adevărat
   cât timp verificarea este în coadă sau rulează în fundal (vezi HealthTech/Documents). Discrepanțele sunt returnate
   pentru revizuire; documentul nu este rescris automat. Verificarea separată este
   disponibilă prin `VerifyMinutesAsync(transcript, markdown, metadata)`.

Toate etapele produc text în română, cu excepția citatelor păstrate în limba sursei. Responsabilul și termenul necunoscute sunt
`Nespecificat`; nu se propun valori. Etapele pot fi apelate separat prin
`ExtractFactsAsync` și `RenderMinutes`, de exemplu pentru revizuirea
faptelor înainte de randare. Apelantul decide unde salvează rezultatele.
Tabelele finale (decizii, acțiuni, probleme deschise) sunt construite direct din faptele
normalizate, astfel încât etapa a doua să nu poată modifica conținutul sau atribuirile lor.

Configurarea este în secțiunea `Minutes` din `appsettings.llm.json`; configurația
aplicației și variabilele de mediu o pot suprascrie, de exemplu `Minutes__ExtractionMaxTokens`.
Fiecare cerere este dimensionată în tokenuri reale, numărate cu tokenizatorul modelului
(`ITokenCounter`), față de `Llm:ContextSize` minus răspunsul etapei. Nimic nu este trunchiat:

- **Extragerea**: un transcript care nu încape într-o singură fereastră este împărțit în
  ferestre de replici întregi (`TranscriptWindows`), de cel mult `Minutes:MaxWindowTokens`
  (implicit 4000) tokenuri; o replică mai lungă decât o fereastră este tăiată la granița
  propozițiilor, fiecare parte păstrând eticheta vorbitorului. Faptele sunt extrase pe fiecare
  fereastră (cu temele deja găsite, ca titlurile să coincidă), unite în cod
  (`MeetingFactsMerger`), apoi un apel mic de consolidare primește doar titlurile temelor și
  rezumatele fragmentelor, grupează temele identice și scrie rezumatul general. Dacă
  consolidarea eșuează, documentul se construiește din faptele unite.
- **Verificarea**: dacă transcriptul și documentul nu încap împreună, întregul document este
  verificat pe rând față de fiecare fragment al transcriptului. `Unsupported` nu poate fi
  judecat pe un fragment și nu este raportat în acest caz; rezumatul verificării o spune.
  Un fragment eșuat lasă `Completed = false`. Dacă documentul singur nu lasă loc pentru o
  fereastră minimă, verificarea nu se face și rezumatul explică motivul.
- La prima încărcare a modelului, `KernelFactory` verifică faptul că contextul modelului
  (`Llm:ContextSize`, respectiv `Llm:Minutes:ContextSize` pentru modelul procesului-verbal)
  cuprinde cel mai mare prompt al rolului, cel mai mare răspuns și o fereastră de 1000 de
  tokenuri; altfel aruncă `LlmConfigurationException` cu setarea de mărit.

Răspunsurile goale sau cu format invalid ale extragerii și consolidării sunt reîncercate de
`Minutes:MaxRetries` ori (implicit o reîncercare); verificarea are propriul
`Minutes:VerificationMaxRetries`, implicit 0: o verificare care a eșuat o dată eșuează de regulă
la fel și a doua oară, iar fiecare încercare costă minute. Dacă extragerea (a oricărui fragment)
eșuează, documentul nu este construit.
Anularea și erorile de inferență sunt propagate apelantului. Un raport al verificării
cu format sau citate invalide produce o eroare după reîncercări, nu un verdict de conformitate.
Anularea verificării nu returnează un rezultat prezentat drept verificat. Formatul JSON și
documentul este construit în cod; fidelitatea semantică a textului generat
necesită revizuire umană. Conținutul medical nu este scris în loguri de acest serviciu.

Apelurile de inferență ale corectorului și generatorului sunt serializate pentru
același serviciu de chat. Fluxul și endpointurile din proiectul HealthTech nu sunt
modificate: integrarea automată după transcriere necesită un apel al serviciului
din acel proiect.
