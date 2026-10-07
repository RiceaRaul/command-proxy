# Reguli cpx

O regulă spune cum se reduce output-ul unei comenzi. Regulile sunt fișiere JSON; pentru a adăuga sau schimba una nu se recompilează nimic.

Principiu de urmat când scrii o regulă: **scoți zgomot, nu conținut.** Bannere, progres, duplicate și sfaturi generice pot dispărea; erorile, locațiile, diff-urile și rezultatele rămân. Un detaliu ratat de agent costă mai mult decât tokenii economisiți.

## Pornire rapidă

Vrei ca `pip install` să nu mai afișeze liniile „Requirement already satisfied”.

1. Creează `~/.config/cpx/rules/pip.json` (numele fișierului este numele comenzii):

   ```json
   {
     "rules": [
       {
         "name": "pip-install",
         "args": ["install"],
         "onSuccess": [
           { "step": "drop", "contains": ["Requirement already satisfied"] },
           { "step": "dropBlank" }
         ]
       }
     ]
   }
   ```

2. Verifică ce se aplică: `cpx explain pip install requests`
3. Încearcă: `cpx pip install requests`

Atât. Dacă hook-ul de agent este instalat, `pip install ...` va fi rescris automat în `cpx pip install ...`, fiindcă acum există o regulă pentru el.

## Unde stau regulile

| Prioritate | Loc | Observații |
|---|---|---|
| 1 | `<proiect>/.cpx/rules/<comandă>.json` | căutat din directorul curent în sus; aplicat doar dacă fișierul este de încredere (vezi [Încredere](#încredere)) |
| 2 | Linux: `~/.config/cpx/rules/<comandă>.json`<br>Windows: `%APPDATA%\cpx\rules\<comandă>.json` | reguli personale, aplicate mereu |
| 3 | incluse în binar (`src/Cpx/BuiltinRules/`) | implicite |

`<comandă>` este numele executabilului, fără director: `/usr/bin/git` → `git.json`. Pe Windows extensia (`.exe`, `.cmd`, `.bat`, `.com`) se ignoră și numele se scrie cu litere mici. Sunt acceptate doar litere, cifre și `. _ + -`.

Consecințe ale faptului că fișierul poartă numele executabilului:

- `npm run build` se scrie în `npm.json`, cu `"args": ["run", "build"]`.
- `npx tsc` se scrie în `npx.json`, cu `"args": ["tsc"]`; regula din `tsc.json` se aplică doar când `tsc` este pornit direct.

**Cum se alege regula.** În fiecare fișier câștigă prima regulă care se potrivește, deci regulile mai specifice se pun primele. Dacă fișierul de la un nivel nu are nicio regulă potrivită, căutarea continuă la nivelul următor. Poți astfel suprascrie doar `dotnet test` într-un proiect și lăsa restul regulilor `dotnet` cele implicite.

Fără nicio regulă potrivită, comanda rulează nefiltrată, cu terminalul moștenit direct.

### Suprascrierea regulilor implicite

Regulile incluse în binar pot fi înlocuite fără recompilare: pui o regulă cu aceleași `args` într-un fișier cu același nume, în directorul personal sau în proiect. Suprascrierea se face **pe regulă, nu pe fișier**: celelalte reguli implicite pentru aceeași comandă rămân active.

De exemplu, ca `git log` să păstreze formatul complet și doar să fie plafonat, creezi `~/.config/cpx/rules/git.json`:

```json
{
  "rules": [
    {
      "name": "my-git-log",
      "args": ["log"],
      "steps": [ { "step": "maxLines", "n": 300 } ]
    }
  ]
}
```

`git status`, `git diff` și restul folosesc în continuare regulile implicite. `cpx explain git log` arată regula ta și, pe linia `shadows:`, pe cea implicită pe care o acoperă.

Punctul de plecare cel mai simplu este copia regulii implicite din `src/Cpx/BuiltinRules/`, modificată.

Pentru a **neutraliza** o regulă implicită, o suprascrii cu una fără pași (`"steps": []`). Output-ul trece atunci aproape neschimbat: se taie doar spațiile de la sfârșitul liniilor și liniile goale de la capete. Nu este identic cu rularea directă: output-ul tot este capturat și afișat la sfârșit, nu pe măsură ce apare. O dezactivare completă, cu terminal moștenit, există doar per rulare, prin `cpx --raw`.

## Structura unui fișier

```json
{
  "rules": [
    {
      "name": "dotnet-build",
      "description": "Doar diagnostice, fiecare o singură dată",
      "args": ["build"],
      "unless": ["--help"],
      "addArgs": ["-nologo", "-tl:off"],
      "mergeStderr": false,
      "onSuccess": [ ... ],
      "onFailure": [ ... ]
    }
  ]
}
```

Comentariile (`//`, `/* */`) și virgulele finale sunt permise. O proprietate necunoscută este eroare, ca greșelile de tastare să nu treacă neobservate.

| Câmp | Tip | Obligatoriu | Semnificație |
|---|---|---|---|
| `name` | text | da | apare în `cpx explain` și în `cpx gain` |
| `description` | text | nu | afișat de `cpx explain` |
| `args` | listă de texte | nu (implicit `[]`) | prefixul de argumente pe care se potrivește regula |
| `unless` | listă de texte | nu | regula nu se aplică dacă vreun argument începe cu unul dintre aceste texte |
| `addArgs` | listă de texte | nu | argumente adăugate comenzii |
| `mergeStderr` | boolean | nu (implicit `false`) | filtrează și stderr |
| `steps` | listă de pași | nu | pașii aplicați indiferent de rezultat |
| `onSuccess` | listă de pași | nu | pașii pentru exit code 0; înlocuiește `steps` |
| `onFailure` | listă de pași | nu | pașii pentru exit code diferit de 0; înlocuiește `steps` |

### `args`: potrivirea

`args` se compară cu argumentele comenzii care **nu** încep cu `-`, în ordine, de la primul.

| Regulă | Comandă | Rezultat |
|---|---|---|
| `["status"]` | `git status` | se potrivește |
| `["status"]` | `git --no-pager status -v` | se potrivește (opțiunile sunt sărite) |
| `["status"]` | `git stash` | nu |
| `["ef", "migrations", "add"]` | `dotnet ef migrations add Init` | se potrivește (prefix din mai multe cuvinte) |
| `[]` | orice | se potrivește mereu |

Limite de reținut:

- `git -C alt/director status` nu se potrivește cu `["status"]`: `alt/director` nu începe cu `-`, deci este luat drept primul argument. Comanda rulează nefiltrată, ceea ce este un eșec sigur.
- Dacă apare `--` înainte ca prefixul să fie complet, regula nu se potrivește.
- Prefixul se compară exact, cu litere mari și mici diferite.

### `unless`: excepții

Regula este sărită dacă vreun argument **începe cu** unul dintre texte. Două folosiri tipice:

- agentul a cerut deja un format anume, pe care nu vrei să-l strici: `"unless": ["--porcelain", "--format"]`;
- comanda nu se termină în acel mod: `"unless": ["--watch", "-w"]`.

Fiind potrivire pe început, `"--watch"` exclude și `--watch=false`.

### `addArgs`: output compact direct de la sursă

Multe unelte știu să scrie mai puțin dacă le ceri. Este mai robust decât filtrarea ulterioară.

```json
{ "name": "git-status", "args": ["status"], "addArgs": ["--short", "--branch"] }
```

- Argumentele se inserează **imediat după prefixul potrivit**, nu la sfârșit: `git diff -- fișier` devine `git diff <addArgs> -- fișier`.
- Un argument deja prezent în comandă nu se adaugă a doua oară. De aceea fiecare element trebuie să fie de sine stătător: `"--max-count=30"`, nu `"-n", "30"` (dacă agentul a scris deja `-n 5`, ar rămâne un `30` rătăcit).
- `addArgs` schimbă comanda rulată. Verifică să nu-i schimbe sensul și combină-l cu `unless` pentru opțiunile cu care intră în conflict.

### `mergeStderr`

Implicit doar stdout este filtrat; stderr ajunge neschimbat la terminal. Cu `"mergeStderr": true`, stderr este capturat și **adăugat după** stdout, apoi filtrat împreună cu el. Ordinea în care cele două s-au intercalat se pierde.

Folosește-l când unealta scrie pe stderr lucruri pe care vrei să le filtrezi (`git push`, `npm install`, erorile din `ng build`).

### `steps`, `onSuccess`, `onFailure`

Se alege `onSuccess` sau `onFailure` după exit code; dacă cel potrivit lipsește, se folosește `steps`; dacă lipsește și acela, output-ul rămâne neschimbat.

Regula de bază: la succes poți tăia mult, la eșec păstrezi tot ce ajută la reparat.

## Pași

Un pas este un obiect cu `"step"` și parametrii lui. Pașii se aplică în ordinea scrisă, linie cu linie.

| `step` | Parametri | Efect |
|---|---|---|
| `stripAnsi` | – | elimină codurile de culoare și de cursor |
| `relativePaths` | – | căile absolute de sub directorul curent devin relative |
| `dropBlank` | – | elimină liniile goale |
| `keep` | `regex` sau `contains` | păstrează doar liniile care se potrivesc |
| `drop` | `regex` sau `contains` | elimină liniile care se potrivesc |
| `replace` | `regex`, `with` | înlocuiește în fiecare linie; `with` acceptă `$1`, `$2` |
| `dedupe` | `adjacent` (implicit `false`) | elimină liniile repetate; cu `adjacent: true` doar pe cele consecutive |
| `truncateLine` | `n` | taie liniile mai lungi de `n` caractere și adaugă `…` |
| `head` | `n` | păstrează primele `n` linii |
| `tail` | `n` | păstrează ultimele `n` linii |
| `maxLines` | `n` | păstrează primele `n` linii și adaugă `… +K lines` |
| `matchOutput` | `regex` sau `contains`, `message` | dacă vreo linie se potrivește, tot output-ul devine `message` și pipeline-ul se oprește |
| `onEmpty` | `message` | dacă nu a mai rămas nimic, output-ul devine `message` |
| `builtin` | `name` | rulează un filtru scris în C# (există unul: `dotnet-test`) |

### `regex` sau `contains`

Pașii `keep`, `drop` și `matchOutput` primesc unul dintre ele, niciodată ambele.

**`contains`** este o listă de texte literale; linia se potrivește dacă o conține pe oricare. Comparația ține cont de litere mari și mici.

```json
{ "step": "keep", "contains": [": error ", ": warning "] }
```

**`regex`** este o expresie regulată în sintaxa .NET, aplicată pe fiecare linie în parte. Ține cont de litere mari și mici; pentru contrariu, începe cu `(?i)`.

```json
{ "step": "drop", "regex": "^index [0-9a-f]+\\.\\.[0-9a-f]+" }
```

Preferă `contains` când poți: este mai rapid (binarul este compilat nativ, iar expresiile din fișiere rulează interpretate) și mai greu de greșit. Fiecare expresie are o limită de 250 ms; la depășire, output-ul este afișat nefiltrat.

În JSON, `\` se scrie dublat: `\\d`, `\\.`.

### Exemple pe pași

```json
// doar erorile, fără sufixul [Proiect.csproj] și fără duplicate
{ "step": "keep", "contains": [": error "] },
{ "step": "replace", "regex": "\\s+\\[[^\\]]+proj\\]$", "with": "" },
{ "step": "dedupe" }

// „ok” dacă nu a rămas nimic de spus
{ "step": "dropBlank" },
{ "step": "onEmpty", "message": "ok" }

// răspuns scurt când apare o linie anume
{ "step": "matchOutput", "contains": ["Already up to date."], "message": "ok (up to date)" }

// mai multe linii goale la rând devin una
{ "step": "dedupe", "adjacent": true }
```

Atenție la `dedupe` fără `adjacent`: elimină orice linie identică cu una anterioară, oriunde ar fi. Într-un output cu blocuri repetitive (de exemplu „Error Message:” la fiecare test eșuat) șterge linii care contează.

## Ce face cpx mereu, fără să ceri

Înainte de pași:

- output-ul este citit ca UTF-8;
- spațiile de la sfârșitul fiecărei linii sunt tăiate;
- dintr-o linie rescrisă cu `\r` (bare de progres) rămâne doar ultima versiune.

După pași:

- liniile goale de la început și de la sfârșit sunt eliminate.

Plase de siguranță:

- **Un eșec nu iese niciodată gol.** Dacă exit code-ul este diferit de 0 și pașii nu lasă nicio linie, se afișează output-ul nefiltrat. De aceea `onFailure` nu are nevoie de `onEmpty`, iar un `keep` care nu recunoaște formatul unei erori nu o poate ascunde.
- **O regulă stricată nu blochează comanda.** Pas necunoscut, parametru lipsă, expresie invalidă sau prea lentă: output-ul apare nefiltrat, cu un avertisment pe stderr.
- **Output-ul complet se păstrează la eșec.** Dacă s-au tăiat cel puțin 256 de bytes, output-ul brut este salvat și calea apare pe ultima linie (`[cpx] full output: ...`). Se păstrează ultimele 20 de fișiere.
- **Exit code-ul comenzii este întors neschimbat.**
- `cpx --raw <comandă>` ocolește orice regulă.

## Unelte pentru scris reguli

**`cpx explain <comandă> [argumente]`** arată regula aleasă, sursa ei, comanda care va rula efectiv (cu `addArgs`), pașii, regulile pe care le umbrește și erorile de validare.

```
$ cpx explain dotnet build -c Release
command:  dotnet build -c Release
rule:     dotnet-build (embedded: rules/dotnet.json)
runs:     dotnet build -nologo -tl:off -clp:NoSummary -c Release
stderr:   passed through
onSuccess:
  1. keep contains=[": warning "]
  ...
```

**`cpx test-rule`** aplică regula pe un output salvat, fără să ruleze comanda:

```bash
dotnet build > /tmp/build.txt 2>&1
cpx test-rule --input /tmp/build.txt --exit 1 dotnet build
```

| Opțiune | Rol |
|---|---|
| `--input <fișier>` | output-ul înregistrat (obligatoriu) |
| `--exit <N>` | exit code simulat; alege între `onSuccess` și `onFailure` (implicit 0) |
| `--cwd <director>` | directorul folosit de `relativePaths` (implicit cel curent) |

Rezultatul filtrat merge pe stdout, iar pe stderr apare câți bytes s-au economisit.

Mod de lucru recomandat: salvezi output real pentru un caz reușit și unul eșuat, scrii regula, iterezi cu `test-rule` până arată bine, apoi verifici cu `explain` și cu o rulare reală.

## Încredere

O regulă de proiect vine odată cu repo-ul și decide ce vede agentul. Un repo clonat de la altcineva ar putea ascunde astfel erori. De aceea fișierele din `.cpx/rules/` sunt ignorate până le accepți explicit.

```bash
cpx trust        # listează regulile din .cpx/rules și cere confirmare
cpx trust --yes  # fără întrebare
cpx untrust      # retrage acordul
```

- Acordul este legat de conținutul fișierului (SHA-256). **Orice modificare îl anulează**; rulezi din nou `cpx trust`.
- Un fișier fără acord este sărit fără niciun mesaj în timpul comenzilor. `cpx explain` îl semnalează.
- Regulile din directorul personal nu cer acord.
- Este consimțământ plus detectare de modificări, nu izolare: cine îți poate scrie în directorul de date poate scrie și lista de acorduri.

## Legătura cu hook-ul de agent

Hook-ul rescrie o comandă în `cpx <comandă>` doar dacă găsește o regulă pentru ea, cu aceleași criterii ca mai sus (`args`, `unless`, încredere). Deci:

- o regulă nouă este preluată automat, fără reinstalarea hook-ului;
- o comandă fără regulă nu este atinsă;
- comenzile compuse (`cd x && git status`, cu `|`, `>`, `$( )`) nu sunt rescrise deloc, chiar dacă au regulă.

## Ce să nu faci

- **Nu scrie reguli pentru comenzi care nu se termină** (`ng serve`, `dotnet watch`, `npm start`). Cu o regulă, cpx așteaptă sfârșitul comenzii ca să filtreze și nu ar afișa nimic. Folosește `unless` pentru modurile de tip watch.
- **Nu scrie reguli pentru comenzi interactive.** stdout este capturat, deci un editor sau un prompt nu se mai vede. (`git commit` fără `-m` este un astfel de caz în regulile implicite; agenții folosesc `-m`.)
- **Nu pune `onEmpty` în `onFailure`.** Ar înlocui plasa de siguranță cu un mesaj fix.
- **Nu tăia la eșec ce nu înțelegi.** Dacă nu știi toate formele pe care le poate lua o eroare, lasă `onFailure` mai permisiv decât `onSuccess`.

## Limite cunoscute

- `cpx gain` compară output-ul comenzii **așa cum a fost rulată** (cu `addArgs`) cu cel filtrat. Pentru regulile al căror câștig vine din `addArgs` (`git status`, `git log`) raportează aproape zero, deși economia reală este mare.
- Pașii lucrează pe câte o linie. Nu se poate exprima „păstrează blocul de la linia X până la linia Y”; pentru asta este nevoie de un filtru `builtin`.
- Potrivirea vede doar numele comenzii și argumentele, nu directorul sau variabilele de mediu.

## Extindere în cod

Pentru ce nu se poate face din JSON. Toate cele trei cazuri cer recompilare.

### O regulă inclusă în binar

1. Adaugă sau editează `src/Cpx/BuiltinRules/<comandă>.json`. Fișierele din acest director sunt incluse automat.
2. Adaugă cel puțin un caz de test în `tests/Cpx.Tests/fixtures/<nume-regulă>/`:

   ```
   <exit>-<caz>.in.txt     output real al comenzii (rulată cu addArgs)
   <exit>-<caz>.out.txt    ce trebuie să producă regula
   ```

   Exemplu: `fixtures/dotnet-build/1-errors.in.txt` și `1-errors.out.txt`. Numărul din față este exit code-ul. Dacă regula are `mergeStderr`, fișierul `.in.txt` conține stdout urmat de stderr.

3. Căile absolute din `.in.txt` se înlocuiesc cu `/work/app`; testele rulează cu acest director drept director curent, ca `relativePaths` să dea același rezultat pe orice mașină. Scoate și numele de utilizator sau alte date personale.
4. Generează `.out.txt` cu binarul compilat, apoi **citește-l** înainte să-l accepți:

   ```bash
   dotnet build src/Cpx
   src/Cpx/bin/Debug/net10.0/cpx test-rule --input caz.in.txt --exit 1 --cwd /work/app <comandă> <argumente> > caz.out.txt
   ```

5. `dotnet test`. Suita verifică singură că fiecare regulă inclusă are cel puțin un caz, că numele sunt unice și că toți pașii sunt valizi.

Folosește output real, înregistrat, nu scris din memorie: formatele diferă între versiuni mai mult decât pare.

### Un pas nou

În `src/Cpx/Pipeline/Steps.cs`:

1. scrie o clasă care implementează `IStep` (metoda `Apply` primește `LineBuffer` și întoarce `false` doar dacă pipeline-ul trebuie oprit);
2. adaugă numele în `switch`-ul din `Steps.Create`, cu validarea parametrilor (erorile se aruncă drept `RuleException`, cu textul `where` în față);
3. dacă are un parametru nou, adaugă proprietatea în `StepSpec` (`src/Cpx/Rules/RuleModel.cs`) și afișarea ei în `Commands.Describe`;
4. adaugă teste în `tests/Cpx.Tests/StepsTests.cs` și un rând în tabelul de pași de mai sus.

O linie este un `ReadOnlyMemory<char>` peste bufferul comun. Pașii care doar aleg linii nu ar trebui să creeze text nou; creează doar pentru liniile pe care le modifică efectiv.

### Un filtru `builtin`

Pentru logică ce ține minte ceva de la o linie la alta (blocuri, grupări). Modelul este `src/Cpx/Pipeline/DotnetTestFilter.cs`.

1. Scrie o clasă `IStep` în `src/Cpx/Pipeline/`.
2. Înregistreaz-o după nume în ramura `"builtin"` din `Steps.Create`.
3. Folosește-o dintr-o regulă: `{ "step": "builtin", "name": "numele-tău" }`. Se combină liber cu ceilalți pași.
4. Fixture-uri, ca la orice regulă.

Două obiceiuri de păstrat din filtrul existent: dacă nu recunoaște nimic din output, îl lasă neatins; iar expresiile regulate fixe se declară cu `[GeneratedRegex]`, ca să fie compilate la build.

## Regulile incluse

| Fișier | Reguli |
|---|---|
| `git.json` | `status`, `diff`, `show`, `log`, `add`, `commit`, `push`, `pull`, `fetch` |
| `dotnet.json` | `build`, `test`, `restore`, `ef` |
| `npm.json` | `install`, `i`, `ci` |
| `ng.json` | `build`, `test` |
| `npx.json` | `tsc`, `ng build`, `ng test` |
| `tsc.json`, `rg.json`, `grep.json`, `ls.json`, `find.json`, `tree.json` | orice argumente |

Conținutul exact al oricăreia: `cpx explain <comandă> <argumente>` sau fișierele din `src/Cpx/BuiltinRules/`, care sunt și cele mai bune exemple de pornire.
