# cpx (command-proxy) — Specificație de cerințe

Stare: rezultat al etapei de brainstorm, 2026-10-07. Conține doar cerințe; arhitectura și designul vin în etapa următoare.

## 1. Scop

`cpx` este un proxy CLI scris în C# / .NET 10 care rulează o comandă, îi capturează output-ul și îl reduce înainte de a ajunge la un agent AI, ca să scadă consumul de tokeni. Este un echivalent al `rtk` (Rust), construit manual, cu două diferențe de intenție:

- regulile de filtrare sunt **date** (fișiere JSON), nu cod, oriunde se poate;
- acoperă inițial doar stack-ul utilizatorului, nu 100+ comenzi.

Referință pentru reguli și comportament: [rtk](https://github.com/rtk-ai/rtk).

## 2. Decizii confirmate

| Subiect | Decizie |
|---|---|
| Nume | proiect `command-proxy`, binar `cpx` (rulează în paralel cu `rtk` oficial, fără conflict de nume) |
| Runtime | .NET 10 (SDK instalat: 10.0.112) |
| Format reguli | JSON, cu `System.Text.Json`; fără TOML |
| Scope v1 | git, dotnet, npm / ng / tsc, căutare și fișiere (rg/grep, ls, find, tree) |
| Agenți | Claude Code și Copilot CLI |
| Platforme | Linux și Windows |
| Statistici | da, în v1, fișier JSONL append-only (fără SQLite) |
| Reguli de proiect | permise, dar aplicate doar după trust explicit (SHA-256 al fișierului) |

## 3. Constatări din repo-ul de referință

Acestea schimbă estimarea de efort și trebuie avute în vedere la design.

1. **Regulile declarative din rtk nu acoperă stack-ul.** Cele 59 de fișiere `src/filters/*.toml` sunt pentru unelte ca terraform, helm, make, brew. Din stack, doar `dotnet-build.toml` există ca regulă. Restul sunt module Rust: `git_cmd.rs` + `diff_cmd.rs` ≈ 14.000 de linii, `dotnet_cmd.rs` + `binlog.rs` + `dotnet_trx.rs` ≈ 5.600, `search.rs` ≈ 2.400, `find_cmd.rs` ≈ 1.600, `ls.rs` ≈ 1.300. Deci „referință pentru reguli” înseamnă în practică: schema și pipeline-ul din TOML, plus comportamentul observat al modulelor Rust, din care v1 portează un subset.
2. **Pipeline-ul declarativ rtk are 8 etape în ordine fixă:** `strip_ansi → replace → match_output → strip/keep_lines → truncate_lines_at → tail_lines → max_lines → on_empty`. `match_output` este un scurtcircuit (dacă output-ul se potrivește, tot output-ul devine un mesaj scurt, de ex. `ok (build succeeded)`).
3. **Fiecare regulă rtk are teste inline** (`input` / `expected`), rulate la build. Un filtru fără teste pică build-ul.
4. **Prioritate la căutare:** proiect → utilizator → embedded → passthrough; prima potrivire câștigă; o regulă de proiect care umbrește una embedded produce un avertisment.
5. **Modelul de trust rtk:** fișierele de reguli de pe disc sunt ignorate tăcut până la `rtk trust`; orice modificare a fișierului invalidează trust-ul. Este consimțământ plus detectare de modificări, nu sandbox.
6. **Copilot CLI: rescriere transparentă posibilă (corectat la design).** README-ul `hooks/copilot` din rtk spune că se poate doar „deny cu sugestie”, dar este depășit: sursa (`src/hooks/hook_cmd.rs`) arată că de la Copilot CLI 1.0.73 schema `PreToolUse` cu `updatedInput`, aceeași ca la Claude Code, este aplicată. Confirmat live pe Copilot CLI 1.0.93 (DESIGN.md §15).

## 4. Cerințe funcționale

### FR-1 Execuție proxy
- FR-1.1 `cpx <comandă> [argumente]` rulează comanda și afișează output-ul filtrat.
- FR-1.2 Exit code-ul comenzii este returnat neschimbat.
- FR-1.3 stderr nu se pierde niciodată; o regulă poate cere explicit includerea lui în filtrare.
- FR-1.4 Fără regulă potrivită, output-ul trece neschimbat (cel mult cu o limită de siguranță configurabilă).
- FR-1.5 `--raw` dezactivează orice filtrare.
- FR-1.6 Dacă filtrarea eșuează (regulă invalidă, excepție), se afișează output-ul brut și un avertisment pe stderr; comanda nu este blocată.
- FR-1.7 Ctrl+C și semnalele de terminare sunt propagate procesului copil.
- FR-1.8 Output corect în UTF-8 pe ambele platforme, inclusiv pe consola Windows.

### FR-2 Motor de reguli
- FR-2.1 O regulă are: nume, descriere, criteriu de potrivire pe comandă și argumente, pipeline pentru succes, pipeline pentru eșec.
- FR-2.2 Pipeline-urile `onSuccess` și `onFailure` sunt separate, alese după exit code.
- FR-2.3 Pași disponibili în v1: `stripAnsi`, `keep`, `drop`, `replace`, `dedupe`, `collapse` (N linii similare → o linie cu contor), `truncateLine`, `head`, `tail`, `maxLines`, `group` (pe fișier sau pe cod de eroare), `matchOutput` (scurtcircuit), `onEmpty` (mesaj când nu rămâne nimic).
- FR-2.4 O regulă poate adăuga argumente comenzii (`appendArgs`) pentru a obține output structurat (ex. `git status --porcelain`).
- FR-2.5 O regulă poate delega unui filtru scris în C# (`builtin`) pentru cazurile care nu se rezolvă cu regex.
- FR-2.6 Surse de reguli, în ordinea priorității: proiect (`.cpx/rules/*.json`), utilizator (director de config per OS), embedded în binar. Prima potrivire câștigă.
- FR-2.7 O regulă de proiect sau de utilizator care umbrește una embedded produce un avertisment.
- FR-2.8 Fișierele de reguli sunt validate la încărcare; erorile indică fișierul și câmpul.

### FR-3 Trust pentru reguli de pe disc
- FR-3.1 Regulile embedded sunt mereu de încredere.
- FR-3.2 Fișierele de reguli de proiect sunt ignorate până la `cpx trust`.
- FR-3.3 Trust-ul este legat de SHA-256 al conținutului; orice modificare îl invalidează.
- FR-3.4 `cpx trust` listează regulile detectate și cere confirmare; `cpx untrust` revocă.
- FR-3.5 Pe calea de execuție a comenzii, regulile netrustate sunt sărite fără mesaje în output.

### FR-4 Filtre pentru stack (v1)
| Zonă | Comenzi | Tip așteptat |
|---|---|---|
| git | `status`, `diff`, `log`, `add`, `commit`, `push`, `pull` | builtin C# pentru `status` și `diff`; reguli pentru restul |
| dotnet | `build`, `test`, `restore`, `ef migrations` | builtin pentru `build` (dedup pe cod, grupare pe fișier) și `test` (doar teste eșuate, cu stack trace); reguli pentru restul |
| JS | `npm install`, `npm run`, `ng build`, `ng test`, `tsc` | reguli; builtin doar dacă se dovedește necesar |
| Căutare | `rg`, `grep`, `ls`, `find`, `tree` | builtin pentru grupare pe fișier la `rg`/`grep`; reguli pentru restul |

Cerință comună: la succes, reducere agresivă; la eșec, se păstrează toate erorile și contextul necesar pentru a le repara.

### FR-5 Recuperarea informației
- FR-5.1 Când un filtru taie output la eșec, output-ul complet este salvat pe disc, iar calea apare în output-ul filtrat.
- FR-5.2 Fișierele salvate sunt rotite sau limitate ca număr și dimensiune.

### FR-6 Unelte de depanare a regulilor
- FR-6.1 `cpx explain <comandă>` arată ce regulă s-ar aplica, din ce sursă, și ce pași.
- FR-6.2 `cpx test-rule <regulă> <fișier>` aplică o regulă pe un output înregistrat.
- FR-6.3 Regulile pot conține teste inline (input / expected); `cpx` le poate rula pe toate, iar regulile embedded fără teste pică build-ul sau suita de teste.

### FR-7 Statistici
- FR-7.1 Fiecare execuție adaugă o linie într-un fișier JSONL: comandă, regulă aplicată, bytes înainte și după, durată, exit code, timestamp.
- FR-7.2 `cpx gain` afișează economia agregată; `--history` afișează execuțiile recente.
- FR-7.3 Scrierea statisticilor nu poate face comanda să eșueze și nu adaugă întârziere perceptibilă.
- FR-7.4 Fișierul are o politică de retenție.

### FR-8 Integrare cu agenți
- FR-8.1 Claude Code: hook `PreToolUse` pe Bash care rescrie transparent comanda în `cpx <comandă>`.
- FR-8.2 Copilot CLI: același hook cu rescriere transparentă (vezi §3.6); dacă versiunea instalată nu o aplică, hook care refuză comanda și sugerează varianta cu `cpx`.
- FR-8.3 Logica de rescriere este în binar (`cpx hook <agent>`), nu în scripturi shell, ca să meargă identic pe Linux și Windows.
- FR-8.4 Se rescriu doar comenzile pentru care există o regulă; restul rămân neatinse.
- FR-8.5 `cpx init <agent>` instalează hook-ul; operația este idempotentă și reversibilă.
- FR-8.6 Coexistență cu hook-ul `rtk` existent: instalarea nu îl șterge și nu produce dublă rescriere (`rtk cpx ...` sau `cpx rtk ...`).

## 5. Cerințe nefuncționale

### NFR-1 Performanță
- NFR-1.1 Overhead de pornire mic față de rularea directă a comenzii. Țintă propusă: sub 20 ms pe Linux pentru o comandă fără regulă; de validat prin măsurare.
- NFR-1.2 Publicare Native AOT, single-file, fără dependență de runtime instalat.
- NFR-1.3 Calea fierbinte (citire output, împărțire pe linii, aplicare pași) lucrează pe span-uri și buffere refolosite, fără un `string` alocat per linie acolo unde se poate evita.
- NFR-1.4 Se folosesc facilitățile moderne din .NET unde aduc câștig măsurabil: `ReadOnlySpan<char>` / `ReadOnlySpan<byte>`, `SearchValues<T>`, API-urile de regex pe span (`IsMatch`, `EnumerateMatches`), `[GeneratedRegex]` pentru filtrele builtin, `ArrayPool<T>`, colecții `Frozen*`, source generator pentru `System.Text.Json`.
- NFR-1.5 Regulile se încarcă leneș: doar regula potrivită comenzii curente este compilată.
- NFR-1.6 Afirmațiile de performanță sunt susținute de benchmark-uri (BenchmarkDotNet pe pipeline, măsurare de pornire pe binarul publicat).

Constrângere de reținut pentru design: sub Native AOT, regex-urile din fișierele JSON nu pot fi compilate la runtime (`RegexOptions.Compiled` cade pe interpretor) și nu pot folosi source generator-ul. Regex-urile din reguli vor rula interpretate sau `NonBacktracking`; doar filtrele builtin beneficiază de `[GeneratedRegex]`.

### NFR-2 Corectitudine și siguranță
- NFR-2.1 Agentul vede același succes sau eșec ca fără `cpx`.
- NFR-2.2 Niciun filtru nu ascunde o eroare la exit code diferit de zero.
- NFR-2.3 Regex-urile din reguli de pe disc au timeout, ca o regulă greșită să nu blocheze comanda.
- NFR-2.4 Fără telemetrie și fără acces la rețea.

### NFR-3 Portabilitate
- NFR-3.1 Linux x64 și Windows x64, aceeași funcționalitate.
- NFR-3.2 Căi de configurare și date conform convenției fiecărui OS (XDG pe Linux, `%APPDATA%` / `%LOCALAPPDATA%` pe Windows).

### NFR-4 Mentenabilitate
- NFR-4.1 Fiecare filtru, builtin sau regulă, are teste pe output-uri reale înregistrate (golden files).
- NFR-4.2 Adăugarea unei reguli noi nu cere recompilare.
- NFR-4.3 Fără dependențe NuGet la runtime în v1, cu excepția celor justificate explicit.

## 6. Criterii de acceptare (v1)

1. `cpx dotnet build` pe un build reușit fără avertismente produce o singură linie; pe un build eșuat produce fiecare eroare distinctă o singură dată, grupată pe fișier, și exit code diferit de zero.
2. `cpx dotnet test` cu teste eșuate afișează doar testele eșuate, cu mesaj și stack trace, plus sumarul.
3. `cpx git status` și `cpx git diff` produc output mai scurt decât cel brut și nu omit niciun fișier modificat.
4. `cpx --raw <comandă>` produce output identic byte cu byte cu rularea directă.
5. O comandă fără regulă se comportă ca rularea directă (output și exit code).
6. O regulă nouă pusă în directorul de utilizator este aplicată fără recompilare; una pusă în `.cpx/rules/` este ignorată până la `cpx trust` și din nou după modificarea fișierului.
7. `cpx explain dotnet build` numește regula și sursa ei.
8. `cpx gain` raportează economia pentru execuțiile anterioare.
9. În Claude Code, cu hook-ul instalat, `git status` este executat ca `cpx git status` fără intervenția utilizatorului.
10. Binarul publicat pornește fără runtime .NET instalat, pe Linux și pe Windows.
11. Economie măsurată de cel puțin 60% (bytes) pe comenzile din scope, la succes, pe un set de output-uri înregistrate din proiectele utilizatorului. Pragul este preluat din ghidul rtk; de confirmat.

## 7. În afara scope-ului v1

- docker, kubectl, sqlcmd, gh / glab (se adaugă dacă se dovedesc zgomotoase)
- Copilot Chat în VS Code, alți agenți
- macOS
- SQLite, telemetrie, `discover` / `learn` din rtk
- Format TOML și import automat al regulilor rtk

## 8. Întrebări deschise

1. **Comenzi compuse în hook:** `cd x && git status`, pipe-uri, redirecționări. rtk are un tokenizer de 60 KB pentru asta. Ce nivel este necesar în v1: doar comenzi simple, sau și lanțuri `&&`?
2. **Comenzi de lungă durată sau interactive** (`ng serve`, `dotnet watch`, `git commit` cu editor): passthrough cu streaming și fără filtrare? Trebuie o listă explicită.
3. **Ordinea stdout / stderr:** citite separat își pierd intercalarea. Este acceptabil, sau se cere un singur flux combinat?
4. **Coliziune de nume pentru meta-comenzi:** `cpx gain`, `cpx trust`, `cpx explain` față de comenzi reale cu același nume. Este nevoie de un prefix (`cpx --gain`) sau de un separator (`cpx -- gain`)?
5. **Portarea celor 59 de reguli TOML din rtk în JSON:** ieftină (conversie mecanică), dar în afara stack-ului. Se face în v1 ca bonus sau deloc?
6. **Copilot CLI:** modul „deny cu sugestie” este acceptabil, sau se preferă doar instrucțiuni în fișierul de context al agentului?
7. **Pragul de 60% și ținta de 20 ms** sunt propuneri; trebuie confirmate sau înlocuite după primele măsurători.
8. **Distribuție:** doar binar AOT, sau și `dotnet tool` (pornire mai lentă, instalare mai simplă)?
9. **`dotnet test`:** se acceptă ca `cpx` să adauge `--logger trx` și să citească fișierul TRX (mai robust, dar scrie fișiere în `TestResults/`), sau doar parsare de text?

## 9. Pasul următor

`/sc:design` pentru arhitectură (structura soluției, contractul pașilor, schema JSON a regulilor, modelul de date pentru tracking), apoi `/sc:workflow` pentru planul de implementare. Întrebările 1–4 din §8 trebuie închise înainte de design.
