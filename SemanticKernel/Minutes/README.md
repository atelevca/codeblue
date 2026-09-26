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

1. **Fact Extraction and Summary**: transcriptul este transformat în JSON cu
   `summary`, `decisions`, `actions` și `issues`. Fiecare acțiune are
   `description`, `responsible` și `deadline`.
2. **Minutes Generation**: un apel separat, cu istoric nou, primește exclusiv
   faptele validate și rezumatul. Rezultatul este un proces-verbal Markdown cu
   secțiunile Rezumat, Decizii, Acțiuni și Probleme.
3. **Minutes Verification**: un nou apel al modelului compară documentul final
   (inclusiv tabelul acțiunilor) direct cu transcriptul original. Raportul în română
   conține concluzia și discrepanțele: afirmații fără suport, omisiuni și contradicții,
   cu citate exacte și corectări sugerate. Citatele sunt verificate în cod față de
   sursele primite, tolerând spațiile, majusculele, ş/ș, ţ/ț și ghilimelele tipografice; citatul
   returnat este fragmentul exact din sursă. O constatare al cărei citat nu se regăsește este
   eliminată și numărată în `DiscardedFindings`, fără a anula restul verificării.
   `IsConsistent` este adevărat numai când nu există discrepanțe și nicio constatare eliminată.
   Discrepanțele sunt returnate pentru revizuire; documentul nu este rescris automat.
   Verificarea separată este disponibilă prin `VerifyMinutesAsync(transcript, markdown)`.

Toate etapele produc text în română, cu excepția citatelor păstrate în limba sursei. Responsabilul și termenul necunoscute sunt
`Nespecificat`; nu se propun valori. Etapele pot fi apelate separat prin
`ExtractFactsAsync` și `GenerateMinutesAsync`, de exemplu pentru revizuirea
faptelor înainte de redactare. Apelantul decide unde salvează rezultatele.
Tabelul final al acțiunilor este construit direct din faptele normalizate, astfel
încât etapa a doua să nu poată modifica responsabilii și termenele din acest tabel.

Configurarea este în secțiunea `Minutes` din `appsettings.llm.json`; configurația
aplicației și variabilele de mediu o pot suprascrie, de exemplu
`Minutes__ExtractionMaxTokens`. Limitele inițiale sunt 6000 de caractere pentru
transcript și 6000 pentru JSON-ul faptelor. Textele mai lungi sunt respinse explicit,
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
