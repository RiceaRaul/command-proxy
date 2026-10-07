# cpx — Design (v1)

Stare: implementat pe Linux, 2026-10-07 (vezi §15 pentru ce diferă de propunerea inițială și ce a rămas). Pornește de la `REQUIREMENTS.md`. Principiu director: YAGNI. Fiecare componentă există pentru că o cere un criteriu de acceptare din v1; restul este listat la §12 ca „tăiat”.

## 1. Rezumat

- **Un singur proiect executabil** (`Cpx`) plus un proiect de teste. Fără biblioteci separate, fără DI, fără bibliotecă de parsare a argumentelor, fără pachete NuGet la runtime.
- **Două căi de execuție:** passthrough (procesul copil moștenește consola, zero procesare) și capture (stdout capturat, filtrat, afișat).
- **Fișierul de reguli se numește după comandă** (`git.json`, `dotnet.json`). Găsirea regulii este o căutare directă după nume, fără scanare de directoare și fără parsarea regulilor altor comenzi.
- **Aproape tot stack-ul se rezolvă cu reguli JSON**, fiindcă uneltele pot fi rugate să producă singure output compact (`git status --short`, `rg --heading`, `dotnet build -clp:NoSummary`). Rămâne un singur filtru scris în C#: `dotnet-test`.
- **Un singur format de hook** pentru Claude Code și Copilot CLI.

## 2. Întrebările deschise din cerințe, închise

| # | Întrebare | Decizie v1 |
|---|---|---|
| 1 | Comenzi compuse în hook | Se rescriu doar comenzile simple. Orice comandă care conține `\| & ; < > ( ) $` sau backtick rămâne neatinsă. Prima extensie probabilă: împărțire pe `&&`. |
| 2 | Comenzi lungi sau interactive | Rezolvat prin passthrough: fără regulă potrivită, copilul moștenește stdin/stdout/stderr, deci streaming, TTY și editor funcționează. Consecință: nu se scriu reguli pentru comenzi care nu se termină (`ng serve`, `dotnet watch`, `npm start`). |
| 3 | Ordinea stdout / stderr | Implicit se filtrează doar stdout; stderr merge direct la consolă, nefiltrat. O regulă poate cere `mergeStderr`, caz în care stderr este adăugat după stdout (intercalarea se pierde). |
| 4 | Coliziune meta-comenzi | Nume rezervate: `explain`, `test-rule`, `trust`, `untrust`, `gain`, `hook`, `init`. Pentru o comandă reală cu același nume: `cpx -- gain`. |
| 5 | Portarea celor 59 de reguli TOML | Nu în v1. |
| 6 | Copilot CLI | Vezi §9: constatarea din cerințe era greșită, rescrierea transparentă este posibilă. |
| 8 | Distribuție | Doar binar Native AOT. |
| 9 | `dotnet test` cu TRX | Nu. Parsare de text, un singur filtru builtin. |

Pragurile de la întrebarea 7 (60% economie, 20 ms pornire) rămân ținte de măsurat, nu decizii de design.

## 3. Structura soluției

```
command-proxy/
├─ Cpx.slnx
├─ src/Cpx/
│  ├─ Cpx.csproj
│  ├─ Program.cs            dispatch: meta-comandă sau proxy
│  ├─ Runner.cs             pornire proces, passthrough și capture
│  ├─ Rules/
│  │  ├─ RuleModel.cs       RuleFile, Rule, StepSpec + JsonSerializerContext
│  │  ├─ RuleStore.cs       căutare proiect → utilizator → embedded
│  │  └─ Trust.cs           trust.json, SHA-256
│  ├─ Pipeline/
│  │  ├─ LineBuffer.cs      output-ul ca listă de felii peste un singur buffer
│  │  ├─ Steps.cs           pașii declarativi
│  │  └─ DotnetTestFilter.cs  singurul builtin
│  ├─ History.cs            JSONL + `gain`
│  ├─ Hook.cs               `hook` și `init`
│  ├─ Paths.cs              directoare per OS
│  └─ BuiltinRules/*.json   EmbeddedResource (nume logic rules/<comandă>.json)
└─ tests/Cpx.Tests/
   ├─ fixtures/<regulă>/<exit>-<caz>.in.txt | .out.txt
   └─ ...
```

Proiectul de benchmark nu există în v1; vezi §10.

## 4. Fluxul de execuție

```
argv
 │
 ├─ primul argument este meta-comandă ──────────► Explain / Trust / Gain / Hook / Init
 │
 └─ proxy
     │
     RuleStore.Find(comandă, argumente)
     │
     ├─ nicio regulă, sau --raw ─► Runner.Passthrough ─► exit code
     │                             (stdio moștenit, nimic capturat, nimic înregistrat)
     │
     └─ regulă
         │
         Runner.Capture(argumente + addArgs)
         │        stdout → buffer;  stderr → consolă (sau buffer, dacă mergeStderr)
         │
         alege pipeline după exit code (onSuccess / onFailure)
         │
         Pipeline.Run(LineBuffer)
         │        la orice excepție: se scrie output-ul brut + avertisment pe stderr
         │
         scrie rezultatul pe stdout
         │
         exit ≠ 0 și output redus ─► salvează brut, adaugă linia „[cpx] full output: <cale>”
         │
         History.Append
         │
         exit code al copilului
```

## 5. Interfața CLI

```
cpx [--raw] <comandă> [argumente...]     rulează prin proxy
cpx -- <comandă> [argumente...]          forțează proxy (nume rezervate)
cpx explain <comandă> [argumente...]     regula aleasă, sursa, pașii, reguli umbrite
cpx test-rule --input <fișier> [--exit N] [--cwd <dir>] <comandă> [argumente...]
                                         aplică regula pe un output înregistrat
cpx trust | untrust                      reguli de proiect din directorul curent
cpx gain [--history]                     economie agregată / ultimele execuții
cpx hook                                 citește payload-ul agentului de pe stdin
cpx init <claude|copilot> [--remove]     instalează sau scoate hook-ul
cpx --version | --help
```

Argumentele se parsează manual; sunt opt forme fixe.

## 6. Reguli

### 6.1 Localizare

| Prioritate | Loc | Condiție |
|---|---|---|
| 1 | `.cpx/rules/<comandă>.json`, căutat din directorul curent în sus | doar dacă fișierul este trustat |
| 2 | Linux: `$XDG_CONFIG_HOME/cpx/rules/<comandă>.json` (implicit `~/.config`); Windows: `%APPDATA%\cpx\rules\<comandă>.json` | mereu |
| 3 | resursă embedded `rules/<comandă>.json` | mereu |

`<comandă>` este numele de fișier al executabilului, fără director și fără extensie (`/usr/bin/git`, `git.exe` → `git`).

În fiecare fișier câștigă prima regulă al cărei `args` se potrivește. Dacă fișierul de la un nivel nu are nicio regulă potrivită, căutarea continuă la nivelul următor. Astfel un proiect poate suprascrie doar `dotnet test` fără să copieze restul regulilor `dotnet`.

Umbrirea unei reguli embedded nu produce avertisment la execuție (ar fi tokeni irosiți la fiecare comandă); apare în `cpx explain`. Este o abatere deliberată de la FR-2.7.

### 6.2 Schema

```json
{
  "rules": [
    {
      "name": "dotnet-build",
      "description": "Doar diagnostice, fără duplicate",
      "args": ["build"],
      "addArgs": ["-nologo", "-tl:off", "-clp:NoSummary"],
      "mergeStderr": false,
      "onSuccess": [
        { "step": "keep", "contains": [": warning "] },
        { "step": "dedupe" },
        { "step": "maxLines", "n": 30 },
        { "step": "onEmpty", "message": "ok (build succeeded)" }
      ],
      "onFailure": [
        { "step": "keep", "contains": [": error ", ": warning "] },
        { "step": "dedupe" },
        { "step": "maxLines", "n": 80 }
      ]
    }
  ]
}
```

| Câmp | Tip | Semnificație |
|---|---|---|
| `name` | string | identificator, apare în `explain` și în istoric |
| `description` | string, opțional | |
| `args` | string[] | prefix comparat cu argumentele care nu încep cu `-`. `[]` se potrivește cu orice. |
| `unless` | string[], opțional | regula nu se potrivește dacă vreun argument începe cu unul dintre aceste prefixe. Folosit când agentul a cerut deja un format anume (`git status --porcelain`) sau când comanda nu se termină (`tsc --watch`). |
| `addArgs` | string[], opțional | inserate imediat după prefixul potrivit, nu la sfârșit (altfel `git diff -- cale` le-ar trata drept căi). Un argument deja prezent nu se adaugă din nou; de aceea fiecare element trebuie să fie de sine stătător (`--max-count=30`, nu `-n`, `30`). |
| `mergeStderr` | bool, implicit `false` | stderr capturat și adăugat după stdout |
| `steps` | StepSpec[], opțional | pipeline folosit și la succes, și la eșec |
| `onSuccess`, `onFailure` | StepSpec[], opțional | înlocuiesc `steps` pentru cazul respectiv |

Limită cunoscută a potrivirii pe `args`: `git -C cale status` nu se potrivește cu `["status"]`, fiindcă `cale` este luat drept subcomandă. Rezultatul este passthrough, deci un eșec sigur.

### 6.3 Pași

`StepSpec` este o singură înregistrare plată (`step` plus câmpuri opționale), nu o ierarhie polimorfică: se deserializează trivial cu source generator și se transformă în pas executabil printr-un `switch` pe nume.

| `step` | Parametri | Efect |
|---|---|---|
| `stripAnsi` | | elimină secvențele de escape |
| `dropBlank` | | elimină liniile goale |
| `relativePaths` | | căile absolute de sub directorul curent devin relative (MSBuild le tipărește întregi) |
| `keep` | `regex` sau `contains` | păstrează liniile care se potrivesc |
| `drop` | `regex` sau `contains` | elimină liniile care se potrivesc |
| `replace` | `regex`, `with` | substituție pe fiecare linie |
| `dedupe` | `adjacent` (bool, implicit `false`) | elimină duplicatele, global sau doar consecutive |
| `truncateLine` | `n` | taie liniile mai lungi de `n` caractere, adaugă `…` |
| `head` / `tail` | `n` | primele / ultimele `n` linii |
| `maxLines` | `n` | primele `n` linii, plus o linie `… +K lines` |
| `matchOutput` | `regex` sau `contains`, `message` | dacă vreo linie se potrivește, tot output-ul devine `message` și pipeline-ul se oprește |
| `onEmpty` | `message` | dacă nu a rămas nicio linie, output-ul devine `message` |
| `builtin` | `name` | rulează un filtru C# (în v1: `dotnet-test`) |

`contains` este o listă de subșiruri literale, comparate ordinal; este varianta rapidă și se preferă oricând nu e nevoie de regex. Liniile goale de la capăt se elimină mereu. La citire, spațiile de la sfârșitul liniei se taie, iar dintr-o linie rescrisă cu `\r` (bare de progres) rămâne doar ultima versiune.

**Plasă de siguranță la eșec:** dacă exit code-ul este diferit de zero și pipeline-ul nu lasă nicio linie, se afișează output-ul nefiltrat. Un `keep` care nu recunoaște formatul unei erori nu poate deci ascunde eșecul (NFR-2.2). Din același motiv pipeline-urile `onFailure` nu folosesc `onEmpty`.

Validare la încărcare: pas necunoscut, parametru lipsă sau regex invalid produc o eroare cu fișierul, regula și indexul pasului. Pe calea de execuție, o regulă invalidă înseamnă output brut plus un avertisment pe stderr (FR-1.6).

### 6.4 Trust

- Stocare: `trust.json` în directorul de date, o mapare cale absolută → SHA-256.
- La execuție: dacă există `.cpx/rules/<comandă>.json`, i se calculează hash-ul și se compară. Nepotrivire sau absență înseamnă că fișierul este sărit, fără mesaj.
- `cpx trust` listează fișierele din `.cpx/rules/`, cu numele regulilor și pașii lor, cere confirmare, apoi scrie hash-urile. `cpx untrust` le șterge.
- Regulile din directorul utilizatorului nu cer trust.

## 7. Componente

### 7.1 Runner

Passthrough: `Process.Start` fără redirecționare, așteaptă, întoarce exit code-ul. Capture: redirecționează stdout (și stderr, la `mergeStderr`), citește octeții până la EOF, așteaptă ieșirea.

- **Ctrl+C:** copilul este în același grup de procese și primește semnalul direct. `cpx` îl ignoră și așteaptă copilul, ca să poată afișa output-ul parțial și exit code-ul corect.
- **SIGTERM:** `cpx` oprește arborele copilului și iese.
- **Windows:** `npm`, `ng`, `tsc` sunt fișiere `.cmd`, pe care `Process.Start` nu le găsește după numele scurt. Runner-ul rezolvă comanda prin `PATH` × `PATHEXT` și pornește calea completă, lăsând sistemul să lanseze `cmd.exe` pentru fișierele batch. Netestat pe Windows; citarea argumentelor cu caractere speciale pentru `cmd.exe` este punctul fragil.
- **Codare:** octeții copilului se decodează ca UTF-8; rezultatul se scrie ca UTF-8 direct în stream-ul stdout, ocolind `Console.Out`.

### 7.2 LineBuffer și pipeline

```csharp
// O linie este o felie peste bufferul decodat, sau peste un string nou (după replace).
sealed class LineBuffer
{
    public List<ReadOnlyMemory<char>> Lines { get; }
    public void RemoveWhere<TState>(TState state, Func<ReadOnlySpan<char>, TState, bool> predicate);
    public void ReplaceAll(string message);
}

interface IStep
{
    /// Întoarce false pentru a opri pipeline-ul (matchOutput).
    bool Apply(LineBuffer buffer);
}
```

Output-ul este decodat o singură dată într-un `char[]` închiriat din `ArrayPool`. Liniile sunt felii peste el. `keep`, `drop`, `dedupe`, `head`, `tail`, `maxLines` doar rearanjează felii; nu se alocă niciun `string` per linie. Doar `replace`, `truncateLine` și `stripAnsi` alocă, și numai pentru liniile pe care le modifică efectiv.

### 7.3 Builtin: `dotnet-test`

Singurul caz din v1 care are nevoie de stare între linii: un test eșuat ocupă un bloc (nume, mesaj, stack trace). Filtrul păstrează blocurile testelor eșuate, liniile de eroare de compilare și sumarul; elimină restul. Formatul exact se fixează pe output-uri reale din proiectele utilizatorului (VSTest și, dacă e folosit, Microsoft.Testing.Platform au output diferit).

Builtin-urile se înregistrează printr-un `switch` pe nume. Un mecanism de plugin nu este justificat pentru un singur filtru.

### 7.4 Salvarea output-ului brut

Condiție: regulă aplicată, exit code diferit de zero, output filtrat mai scurt decât cel brut. Fișier: `<date>/raw/<timestamp>-<comandă>.log`. Se păstrează ultimele 20; curățarea se face la scriere. Calea este adăugată ca ultimă linie a output-ului.

### 7.5 Istoric

`<date>/history.jsonl`, o linie per execuție filtrată, scrisă în mod append:

```json
{"ts":"2026-10-07T10:15:02Z","cmd":"dotnet build","rule":"dotnet-build","in":18432,"out":212,"ms":2140,"exit":0}
```

`cmd` este trunchiat la 200 de caractere. Execuțiile passthrough nu se înregistrează. `cpx gain` agregă la citire (total, pe regulă; tokeni estimați ca bytes / 4). Retenție: când `gain` găsește fișierul peste 5 MB, îl rescrie cu jumătatea mai recentă. Orice eroare de scriere este înghițită (FR-7.3).

Director de date: Linux `$XDG_DATA_HOME/cpx` (implicit `~/.local/share`), Windows `%LOCALAPPDATA%\cpx`.

## 8. Regulile embedded din v1

| Fișier | Regulă | Abordare |
|---|---|---|
| `git.json` | `status` | `addArgs: --short --branch`; output-ul este deja compact |
| | `diff`, `show` | `drop` pe liniile `diff --git` și `index`; `maxLines` |
| | `log` | `truncateLine`, `maxLines` |
| | `add`, `commit`, `push`, `pull`, `fetch` | `mergeStderr`, `drop` pe progres, `onEmpty: ok` |
| `dotnet.json` | `build` | vezi exemplul din §6.2 |
| | `test` | `builtin: dotnet-test` |
| | `restore` | `keep` pe erori și avertismente, `onEmpty: ok` |
| | `ef` | `drop` pe liniile de build și banner |
| `npm.json` | `install`, `ci` | `drop` pe deprecated / funding / audit; păstrează sumarul |
| `ng.json`, `npx.json` | `build`, `test` / `ng build`, `ng test` | `mergeStderr`, `drop` pe liniile de progres, `relativePaths`; la `test` în plus `stripAnsi` și `drop` pe tabelul de bundle al spec-urilor și pe liniile separatoare |
| `tsc.json`, `npx.json` | (orice) / `tsc` | `dedupe`, `maxLines`, `onEmpty: ok` |
| `rg.json` | (orice) | `addArgs: --heading --line-number`, `truncateLine`, `maxLines` |
| `grep.json` | (orice) | `truncateLine`, `maxLines` |
| `ls.json` | (orice) | `drop` pe `total`, `replace` care scoate permisiuni și proprietar |
| `find.json`, `tree.json` | (orice) | `maxLines` |

Conținutul exact al fiecărei reguli se stabilește la implementare, pe fixture-uri înregistrate. Abatere față de FR-4: filtrele builtin pentru `git status`, `git diff`, `dotnet build` și `rg` nu mai sunt necesare, pentru că uneltele produc singure formatul compact; gruparea erorilor de build pe fișier se taie, deduplicarea rămâne.

## 9. Hook-uri

**Corecție față de cerințe (§3.6, FR-8.2).** Codul sursă rtk (`src/hooks/hook_cmd.rs`) arată că Copilot CLI de la versiunea 1.0.73 acceptă schema `PreToolUse` folosită de Claude Code și aplică `updatedInput`; README-ul din `hooks/copilot`, pe care s-a bazat constatarea, este depășit. Deci un singur format de hook acoperă ambii agenți, cu rescriere transparentă. Confirmat live pe Copilot CLI 1.0.93 (§15).

`cpx hook`:

1. Citește JSON de pe stdin, extrage `tool_input.command`.
2. Nu rescrie dacă: comanda conține un metacaracter de shell (§2, întrebarea 1), primul cuvânt conține `=`, sau primul cuvânt este `cpx` ori `rtk`.
3. Împarte pe spații, apelează `RuleStore.Find`. Fără regulă: nu scrie nimic, exit 0.
4. Cu regulă: scrie

```json
{"hookSpecificOutput":{"hookEventName":"PreToolUse","updatedInput":{"command":"cpx git status"}}}
```

`updatedInput` conține tot `tool_input` original, cu `command` înlocuit. Nu se emite `permissionDecision`: fluxul de permisiuni al agentului rămâne neatins. Consecință: regulile de permisiune existente scrise pentru `git status` s-ar putea să nu se aplice comenzii rescrise; utilizatorul adaugă o regulă pentru `cpx`. rtk are un modul de 50 KB care traduce permisiunile; nu se portează.

`cpx init claude` adaugă în `~/.claude/settings.json` o intrare `PreToolUse` cu matcher `Bash` și comanda `cpx hook`; `--remove` o scoate. Fișierul se modifică prin `JsonNode`, păstrând restul conținutului. Locul fișierului de hook pentru Copilot CLI se preia din `src/hooks/init` din rtk, la implementare.

**Coexistența cu rtk.** Două hook-uri care rescriu aceeași comandă se calcă reciproc. `cpx init` detectează hook-ul rtk și avertizează; nu îl modifică. Soluția este ca rtk să excludă comenzile preluate de cpx (rtk are o opțiune de excludere în config, de verificat) sau să fie dezinstalat hook-ul lui.

## 10. Performanță

Ținta care contează este overhead-ul de pornire, plătit la fiecare comandă.

**Build**

```xml
<TargetFramework>net10.0</TargetFramework>
<PublishAot>true</PublishAot>
<InvariantGlobalization>true</InvariantGlobalization>
<UseSystemResourceKeys>true</UseSystemResourceKeys>
<OptimizationPreference>Speed</OptimizationPreference>
```

`InvariantGlobalization` evită încărcarea ICU la pornire; `cpx` nu are nevoie de comparații dependente de cultură.

**Calea passthrough** (majoritatea comenzilor): o căutare de resursă embedded, cel mult două verificări de existență pe disc, apoi pornirea procesului. Fără JSON, fără regex, fără `Console.Out`.

**Calea capture**

| Tehnică | Unde |
|---|---|
| Un singur buffer din `ArrayPool`, linii ca `ReadOnlyMemory<char>` | `LineBuffer` |
| `MemoryExtensions.EnumerateLines` / `IndexOfAny` vectorizat pentru împărțire | `LineBuffer` |
| `SearchValues<string>` cu `ContainsAny` | `keep` / `drop` / `matchOutput` cu `contains` |
| `Regex.IsMatch(ReadOnlySpan<char>)`, `EnumerateMatches` | pașii cu `regex` |
| `[GeneratedRegex]` | `stripAnsi`, `dotnet-test` |
| `HashSet<ReadOnlyMemory<char>>` cu comparator pe span | `dedupe`, fără string-uri |
| Source generator `System.Text.Json` | reguli, istoric, trust |
| `Utf8JsonWriter` direct în stream | `History.Append` |
| Scriere UTF-8 direct în stream-ul stdout | output |

**Limită de reținut.** Regex-urile din fișierele JSON rulează în interpretor: Native AOT nu poate genera cod la runtime, iar source generator-ul cere pattern-uri cunoscute la compilare. De aceea există `contains`, iar regulile embedded îl folosesc oriunde pot. Fiecare regex din reguli are timeout de 250 ms (NFR-2.3).

**Măsurare.** Pornirea s-a măsurat pe binarul publicat, comparând `cpx <comandă>` cu rularea directă în buclă (rezultate în §15 și §16); scripturile de măsurare nu sunt păstrate în repo. Un proiect BenchmarkDotNet se adaugă abia când o măsurătoare arată un punct fierbinte în pipeline; până atunci ar fi infrastructură fără întrebare la care să răspundă.

## 11. Testare

- **Fixture-uri:** `tests/Cpx.Tests/fixtures/<regulă>/<exit>-<caz>.in.txt` și `.out.txt`. Un singur test parametrizat le enumeră, aplică regula embedded și compară. Un al doilea test verifică că fiecare regulă embedded are cel puțin un fixture.
- **Abatere de la FR-6.3:** fără teste inline în JSON. Output-urile reale au zeci de linii; ca string JSON escapat devin de necitit, iar un al doilea mecanism de testare nu se justifică. Pentru regulile proprii există `cpx test-rule`.
- **Teste unitare:** fiecare pas, potrivirea pe `args`, inserarea `addArgs`, decizia de rescriere din hook, trust.
- **Teste de integrare:** binarul rulat pe comenzi reale mici: egalitate byte cu byte la `--raw` și la passthrough, propagarea exit code-ului, comportament la regulă invalidă.
- Framework: xUnit.

## 12. Tăiat din v1

| Element | Motiv | Când revine |
|---|---|---|
| Proiecte separate (Core, Filters, Tracking, Hooks) | un singur consumator | la un al doilea executabil |
| Pașii `group` și `collapse` | nicio regulă din §8 nu îi cere | la prima regulă care are nevoie |
| Builtin-uri pentru `git status`, `git diff`, `dotnet build`, `rg` | uneltele produc output compact cu flag-uri | dacă fixture-urile arată economie insuficientă |
| TRX / binlog pentru dotnet | parsarea de text ajunge | dacă output-ul text se dovedește instabil |
| Rescrierea comenzilor compuse | tokenizer de shell | după v1, începând cu `&&` |
| Traducerea permisiunilor agentului | modul mare, beneficiu mic pentru un singur utilizator | dacă prompturile de permisiune devin deranjante |
| Teste inline în reguli | fixture-urile acoperă nevoia | dacă regulile ajung să fie distribuite |
| Avertisment de umbrire la execuție | cost în tokeni | — |
| Proiect BenchmarkDotNet | nimic de optimizat încă | la primul punct fierbinte măsurat |
| Limită de memorie pentru output capturat | comenzile din scope au output mărginit | la prima regulă pentru o comandă cu output mare |

## 13. Riscuri și verificări înainte de implementare

1. **Build Native AOT.** Pe Linux cere `clang` și `zlib` instalate. Compilarea încrucișată Linux → Windows nu este suportată: binarul de Windows se construiește pe Windows (mașină sau CI).
2. **Copilot CLI și `updatedInput`.** Dedus din sursa rtk, nu testat. Dacă versiunea instalată nu îl aplică, FR-8.2 revine la varianta „deny cu sugestie”.
3. **Două hook-uri `PreToolUse` care rescriu aceeași comandă.** Comportamentul agentului nu este verificat; coexistența cu rtk depinde de excluderea comenzilor în rtk.
4. **`addArgs` schimbă comanda rulată.** `git status --short` și `dotnet build -clp:NoSummary` sunt sigure; fiecare `addArgs` nou trebuie verificat că nu schimbă semantica și că nu intră în conflict cu flag-urile date de agent.
5. **Formatul `dotnet test`** diferă între VSTest și Microsoft.Testing.Platform; filtrul se scrie după fixture-uri din proiectele reale.

## 14. Ordinea de implementare propusă

1. Schelet, `Runner` (passthrough + capture), exit code, `--raw`. Măsurarea pornirii pe binarul AOT.
2. `RuleStore` (doar embedded), `LineBuffer`, pașii, `explain`, `test-rule`, infrastructura de fixture-uri.
3. Regulile `git` și `dotnet`, inclusiv `dotnet-test`.
4. Salvare brut, istoric, `gain`.
5. `hook`, `init claude`; apoi verificarea Copilot CLI.
6. Reguli de utilizator și de proiect, trust.
7. Regulile `npm`, `ng`, `tsc`, `rg`, `grep`, `ls`, `find`, `tree`.
8. Windows: rezolvarea comenzilor `.cmd`, căi, build.

## 15. Starea implementării

**Făcut și verificat pe Linux:** toate comenzile din §5, motorul de reguli cu cele trei surse și trust, salvarea output-ului brut, istoricul și `gain`, hook-ul și `init` pentru ambii agenți, binarul Native AOT. 122 de teste (pași, potrivire, hook, fixture-uri, integrare prin proces); binarul AOT reproduce toate cele 38 de fixture-uri.

**Diferențe față de propunere**

- Câmpul `unless` și pașii `dropBlank` și `relativePaths` au apărut din reguli concrete, nu erau în lista inițială.
- Plasa de siguranță la eșec (§6.3).
- `-clp:NoSummary` nu elimină recapitularea diagnosticelor la `dotnet build`; duplicatele sunt scoase de `dedupe`.
- `tsc` are și o regulă în `npx.json`, fiindcă de obicei este pornit prin `npx tsc`.
- `npm` are trei reguli identice (`install`, `i`, `ci`): potrivirea se face pe un singur prefix.

**Nefăcut**

- **Windows:** codul pentru căi și pentru rezolvarea shim-urilor `.cmd` există, dar nu a fost compilat sau rulat pe Windows.
- **Copilot CLI: verificat live** pe versiunea 1.0.93, vezi mai jos.
- **Hook-ul în Claude Code:** `cpx hook` este testat pe payload-uri, `cpx init claude` pe o copie a fișierului de setări; nu a fost instalat în setările reale.
- Fixture-urile `dotnet-ef` și `tree` sunt sintetice (uneltele lipsesc local).

**Copilot CLI, verificare live (1.0.93, Linux)**

Rulat cu `copilot -p "... git status"` într-un repo de probă, cu `COPILOT_HOME` de probă și hook-ul în `<COPILOT_HOME>/hooks/cpx-rewrite.json`:

- Hook-ul înregistrat ca `PreToolUse` primește `tool_name: "Bash"` și `tool_input` (`command`, `description`, `mode`, `initial_wait`), plus `cwd`.
- Răspunsul `hookSpecificOutput.updatedInput` este aplicat: Copilot a executat `cpx git status`, a primit 5 linii în loc de 15, iar execuția apare în istoricul `cpx`. Rescrierea este transparentă.
- Hook-urile înregistrate ca `preToolUse` (schema nativă, documentată) primesc `toolName: "bash"` și `toolArgs` ca obiect. `cpx hook` răspunde la ele cu `modifiedArgs`; forma payload-ului este capturată live, dar răspunsul `modifiedArgs` nu a fost încercat într-o sesiune.
- Câmpul hook-ului poate fi `command` sau `bash`; ambele pornesc.
- **Hook-urile de repo (`.github/hooks/*.json`) nu au pornit** în repo-ul de probă în modul `-p`, în nicio variantă de schemă. Cauza nu este stabilită (posibil folderul nu era în `trustedFolders`). De aceea `cpx init copilot` instalează la nivel de utilizator.
- Testul a permis explicit și `shell(git status)`, și `shell(cpx:*)`. Care dintre ele este cerută după rescriere nu a fost izolat.

**Măsurători** (o singură mașină, binarul AOT, 300 de rulări)

| Caz | Timp per rulare |
|---|---|
| `cpx true` (fără regulă) | 3,0 ms |
| `git status` direct | 1,1 ms |
| `cpx git status` (cu regulă) | 4,3 ms |
| `cpx hook` cu rescriere, pornit din `bash -c` | 4,0 ms (din care 1,3 ms `bash`) |

Binar: 4,6 MB.

Economie în bytes pe proiectele de probă, comandă simplă față de `cpx`:

| Comandă | Brut | Filtrat |
|---|---|---|
| `git status` (5 fișiere modificate) | 320 | 39 |
| `git log` (2 commit-uri) | 467 | 114 |
| `git diff` (mic) | 158 | 128 |
| `ls -la` | 386 | 182 |
| `dotnet build` reușit | 301 | 21 |
| `dotnet test` cu 2 teste eșuate | 1867 | 807 |
| `npm install` cu avertismente | 674 | 92 |

Sunt proiecte de câteva fișiere; cifrele arată că regulile funcționează, nu economia pe un proiect real. `git diff` și `git show` câștigă puțin prin construcție: conținutul diff-ului este informația.

**Limite observate**

- `cpx gain` compară output-ul comenzii așa cum a fost rulată (cu `addArgs`) cu output-ul filtrat. Pentru `git status` și `git log`, unde câștigul vine din `addArgs`, raportează aproape zero deși economia reală este mare.
- La `dotnet test`, xUnit scrie liniile `[xUnit.net ...] ... [FAIL]` pe stderr, care trece nefiltrat. Regula nu folosește `mergeStderr`, ca un crash al test host-ului să rămână vizibil.
- `rg` fără cale citește stdin când acesta nu este terminal; se întâmplă și fără `cpx`, care doar moștenește stdin.

## 16. Verificare pe proiecte publice

Rulat pe `serilog/serilog` (multi-targeting, 816 teste) și `gothinkster/angular-realworld-example-app` (Angular 21), brut față de `cpx` față de `rtk 0.42.4`, o rulare per caz. Bytes de output, stdout și stderr împreună.

| Comandă | brut | cpx | rtk |
|---|---|---|---|
| serilog: `dotnet build` la zi | 3 038 | 21 | 64 |
| serilog: `dotnet build --no-incremental` | 3 063 | 21 | 64 |
| serilog: `dotnet build` cu 3 erori (15 în brut, câte una per framework) | 11 899 | 544 | 12 374 |
| serilog: `dotnet test`, 1 test eșuat | 2 773 | 1 369 | 4 234 |
| serilog: `dotnet test`, 4 teste eșuate | 4 685 | 2 397 | 7 609 |
| serilog: `git log` | 52 564 | 2 470 | 1 994 |
| serilog: `git show HEAD` | 2 103 | 2 036 | 1 220 |
| serilog: `rg -n ILogEventSink src` | 7 816 | 5 315 | 7 816 |
| serilog: `ls -la src/Serilog` | 1 667 | 817 | 483 |
| serilog: `find src -name "*.cs"` | 6 031 | 6 031 | 1 349 |
| angular: `npm install` (prima dată) | 930 | 200 | nemăsurat |
| angular: `npm install` la zi | 412 | 187 | 370 |
| angular: `npx ng build` reușit | 2 356 | 2 221 | 2 351 |
| angular: `npx ng build` cu 4 erori | 1 876 | 1 839 | 1 859 |
| angular: `npx tsc` cu 2 erori | 195 | 195 | 263 |
| angular: `git status` | 499 | 127 | 126 |
| angular: `git diff` | 771 | 711 | 513 |
| angular: `git log` | 10 519 | 2 409 | 970 |

Constatări:

- La `dotnet build` și `dotnet test` care eșuează, `rtk` a produs mai mult decât output-ul brut (îl redă integral și adaugă un rezumat); `cpx` a redus de 2 până la 20 de ori. Observat cu acest SDK și această versiune de `rtk`.
- `rtk` comprimă mai mult la `git log`, `git show`, `git diff`, `ls`, `find`. Acolo taie conținut: liniile de context din diff, corpul commit-urilor, lista de fișiere grupată. Regulile `cpx` păstrează deliberat acest conținut.
- `ng build` și `tsc` au output aproape fără zgomot; câștigul este de 1–5% și vine doar din liniile de progres.
- Rularea pe serilog a scos la iveală un defect: filtrul `dotnet-test` elimina orice cadru `System.Reflection.*`, inclusiv cel în care fusese aruncată excepția. Acum elimină doar cadrele de invocare prin reflecție.

### `ng test` (Angular CLI 22.2.2, builder `@angular/build:unit-test`, vitest 5)

Proiect nou creat cu `ng new`, o rulare per caz, bytes:

| Caz | brut | cpx | rtk |
|---|---|---|---|
| toate testele trec | 1 000 | 285 | 1 001 |
| 3 teste eșuate | 4 132 | 2 380 | 4 132 |

`rtk` nu are filtru pentru `ng test` și redă output-ul neschimbat. Vitest scrie coduri de culoare și când output-ul nu merge la terminal; o bună parte din câștig vine din eliminarea lor. Rezultatele, mesajele de aserție, diff-urile și fragmentele de cod rămân întregi.

Fără terminal, `ng test` rulează o singură dată și se oprește, deci regula nu blochează. Într-un terminal interactiv pornește în mod watch: acolo `cpx ng test` nu ar afișa nimic până la oprire; se folosește `--watch=false` sau comanda directă.
