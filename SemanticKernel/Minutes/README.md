# Proces-verbal în două etape, urmat de verificare

`IMeetingMinutesGenerator` este înregistrat prin apelul existent
`AddMedicalTermCorrection(configuration)` și folosește același model local.

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
2. **Minutes Generation**: un apel separat, cu istoric nou, primește exclusiv
   faptele validate. Rezultatul este un proces-verbal Markdown cu secțiunile
   Participanți, Ordinea de zi, Desfășurarea ședinței, Decizii, Acțiuni, Probleme deschise,
   Următoarea ședință, Rezumat și Semnături; titlurile sunt validate în această ordine
   (Semnături este opțional). Lista din Ordinea de zi (cu nota despre agenda dedusă) și
   tabelele Decizii, Acțiuni și Probleme deschise, cu coloana „Punct” (agenda_id sau „–”),
   sunt construite în cod din faptele normalizate, nu de model: etapa a doua nu poate omite un element, îl poate muta la alt punct sau înlocui
   un responsabil/termen `Nespecificat` cu o presupunere.
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
   când nu există discrepanțe și nicio constatare eliminată. Discrepanțele sunt returnate
   pentru revizuire; documentul nu este rescris automat. Verificarea separată este
   disponibilă prin `VerifyMinutesAsync(transcript, markdown, metadata)`.

Toate etapele produc text în română, cu excepția citatelor păstrate în limba sursei. Responsabilul și termenul necunoscute sunt
`Nespecificat`; nu se propun valori. Etapele pot fi apelate separat prin
`ExtractFactsAsync` și `GenerateMinutesAsync`, de exemplu pentru revizuirea
faptelor înainte de redactare. Apelantul decide unde salvează rezultatele.
Tabelele finale (decizii, acțiuni, probleme deschise) sunt construite direct din faptele
normalizate, astfel încât etapa a doua să nu poată modifica conținutul sau atribuirile lor.

Configurarea este în secțiunea `Minutes` din `appsettings.llm.json`; configurația
aplicației și variabilele de mediu o pot suprascrie, de exemplu
`Minutes__ExtractionMaxTokens`. Limitele inițiale sunt 6000 de caractere pentru
transcript și 8000 pentru JSON-ul faptelor (structura cu agendă și discuții este mai
lungă decât vechea listă); extragerea și generarea au 3072 de tokenuri de răspuns. Textele mai lungi sunt respinse explicit,
nu trunchiate; împărțirea automată a transcriptelor nu este implementată.
La creșterea limitelor, ajustați și `Llm:ContextSize` pentru a permite atât promptul,
cât și răspunsul. Numărul de caractere nu garantează încadrarea în numărul de tokenuri.
Verificarea are limite separate: `MaxVerificationCharacters` (implicit 14000 pentru
JSON-ul combinat transcript + document) și `VerificationMaxTokens` (2048).
Dimensionați contextul modelului și pentru această intrare combinată; nu se trunchiază sursele.

Răspunsurile goale sau cu format invalid sunt reîncercate de `Minutes:MaxRetries`
ori (implicit o reîncercare). Dacă extragerea eșuează, etapa a doua nu pornește.
Anularea și erorile de inferență sunt propagate apelantului. Un raport al verificării
cu format sau citate invalide produce o eroare după reîncercări, nu un verdict de conformitate.
Anularea verificării nu returnează un rezultat prezentat drept verificat. Formatul JSON și
secțiunile Markdown sunt validate; fidelitatea semantică a textului generat
necesită revizuire umană. Conținutul medical nu este scris în loguri de acest serviciu.

Apelurile de inferență ale corectorului și generatorului sunt serializate pentru
același serviciu de chat. Fluxul și endpointurile din proiectul HealthTech nu sunt
modificate: integrarea automată după transcriere necesită un apel al serviciului
din acel proiect.
