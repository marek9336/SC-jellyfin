# HANDOFF — SC-Jellyfin

> **Začni tady, když pokračuješ na jiném stroji (stolní PC ↔ notebook) nebo v nové konverzaci s Claude.**
> Tento dokument je záměrně samostatný a nezávislý na konkrétním počítači.
> Detaily viz [NOTES.md](NOTES.md) (technika/API/rozhodnutí) a [README.md](README.md) (instalace pro uživatele).

---

## 1) TL;DR — kde to je a co dělat dál

**Co to je:** monolitický **Jellyfin plugin** (C# / .NET 9), který nahrazuje Kodi addon
**Stream Cinema** (repo cder.sk). Vyhledá film/seriál v katalogu Stream Cinema, **stáhne**
vybraný stream z **kra.sk** do knihovny na TrueNAS a spustí sken. Sleduje se pak lokálně
z NASu → žádný buffering, plné převíjení, transkódování.

**Stav k 2026-08-19:** ✅ kompletní kód, ✅ **build OK** (0 chyb), ✅ lokální git repo
(1. commit), ✅ sideload zip vyroben. ⏳ **Chybí jediné: reálný test na živých datech.**

**Nejbližší krok:** pushnout na GitHub (viz §4) a spustit **první test** na Jellyfinu
(viz §5). Tvary JSON odpovědí katalogu se parsují defenzivně, ale nebyly ověřeny naživo —
proto je to MVP a čeká se doladění po prvním běhu.

---

## 2) Kde projekt žije

| Umístění | Cesta / URL |
|---|---|
| Pracovní složka (syncuje Nextcloud) | `…\Nextcloud\IT\AI\Claude\SC-jellyfin\` |
| Na tomto notebooku | `C:\Users\marek.bor\Nextcloud\IT\AI\Claude\SC-jellyfin\` |
| GitHub | `https://github.com/<TVUJ-UCET>/SC-jellyfin` *(po pushnutí — doplnit)* |
| Sestavený plugin (sideload) | `SC-jellyfin\artifacts\streamcinema_0.1.0.0.zip` *(build výstup, není v gitu)* |

> ⚠️ **Sync mezi stroji:** složka je v Nextcloudu, takže se syncuje sama — ALE `.git/`
> a build výstup (`bin/`, `obj/`, `publish/`, `artifacts/`) přes cloud syncovat je
> nespolehlivé. **Spolehlivá cesta mezi PC je GitHub** (`git pull` / `git push`).
> Před prací na druhém stroji si udělej `git pull`.

---

## 3) Rozjetí na novém/čistém stroji

**Předpoklady** (jednorázově na každém stroji):

```bash
# .NET 9 SDK
winget install --id Microsoft.DotNet.SDK.9 --silent --accept-source-agreements --accept-package-agreements
# Git (pokud chybí)
winget install --id Git.Git --silent
```

Po instalaci SDK může být potřeba nový terminál (kvůli PATH). Ověření:

```bash
dotnet --version   # očekává se 9.0.x
```

**Získání kódu** (preferuj git; složka v Nextcloudu je záložní):

```bash
git clone https://github.com/<TVUJ-UCET>/SC-jellyfin.git
cd SC-jellyfin
```

**Build:**

```bash
dotnet build Jellyfin.Plugin.StreamCinema -c Release
```

> Repo obsahuje `NuGet.Config` (zdroj nuget.org) — čisté SDK ho jinak nemá nastavený
> a restore by spadl na „nelze vyřešit Jellyfin.Controller". Tímhle je to ošetřené.

**Vyrobit sideload balíček** (volitelné, pro ruční instalaci bez GitHub Releases):

```bash
dotnet publish Jellyfin.Plugin.StreamCinema -c Release -o publish
# do publish\ přidat meta.json (viz .github/workflows/build.yml, sekce meta.json)
# a zabalit Jellyfin.Plugin.StreamCinema.dll + meta.json do zipu
```

---

## 4) Push na GitHub

Lokální repo už existuje (1. commit hotový). Na novém stroji přeskoč `init`.

```bash
cd "C:\Users\marek.bor\Nextcloud\IT\AI\Claude\SC-jellyfin"

# 1) na GitHubu vytvoř PRÁZDNÝ repo SC-jellyfin (bez README/gitignore)
# 2) propojit a pushnout:
git remote add origin https://github.com/<TVUJ-UCET>/SC-jellyfin.git
git push -u origin main

# 3) vydání verze → GitHub Actions vyrobí release + manifest.json:
git tag v0.1.0
git push origin v0.1.0
```

Po pushnutí nahraď `<TVUJ-UCET>` v [README.md](README.md) (instalační URL manifestu)
a v §2 tohoto dokumentu.

---

## 5) Instalace do Jellyfinu a první test

**Jellyfin:** 10.11.11 na TrueNAS SCALE (oficiální app). Plugin ABI `10.11.0.0`, .NET 9.

**Cesta A — rychlý sideload (test hned):**
1. Rozbal `artifacts\streamcinema_0.1.0.0.zip`.
2. V Jellyfin config datasetu vytvoř `plugins\Stream Cinema\` a nakopíruj tam
   `Jellyfin.Plugin.StreamCinema.dll` + `meta.json`.
   (Typicky `…/jellyfin/config/plugins/Stream Cinema/` — podle mapování `/config`.)
3. Restart Jellyfinu → **Dashboard → Pluginy → Stream Cinema**.

**Cesta B — přes plugin repozitář (auto-instalace/update):**
1. **Dashboard → Pluginy → Repozitáře → Přidat**, URL:
   `https://raw.githubusercontent.com/<TVUJ-UCET>/SC-jellyfin/main/manifest.json`
   *(manifest vznikne až po pushnutí tagu `v*`, viz §4)*
2. Katalog → nainstaluj „Stream Cinema" → restart.

**Nastavení (v GUI pluginu):**
1. Záložka **Nastavení** → kra.sk jméno + heslo → Uložit → „Otestovat přihlášení".
2. „Načíst token z kra.sk (sc.json)" — načte schválený X-AUTH-TOKEN z tvého úložiště.
   Když selže → zadej token ručně (kra.sk → Úložiště → `sc.json` → 32 znaků).
3. Cesty ke složkám Filmy/Seriály = cesty **uvnitř Jellyfin kontejneru**
   (musí odpovídat knihovnám Jellyfinu, např. `/media/movies`, `/media/tvshows`).

**Co sledovat při prvním testu (a poslat Claudovi):**
- Vrátí hledání výsledky? Jdou u seriálu procházet sezóny/epizody?
- Nabídne výběr streamu (jazyk/kvalita/velikost)?
- Stáhne se soubor do správné složky a rozpozná ho Jellyfin?
- **Log Jellyfinu, řádky s prefixem `StreamCinema`** — nejcennější pro ladění.
- Klidně screenshot výsledků hledání / fronty.

---

## 6) Aktuální stav (checklist)

- [x] Analýza Kodi addonu + API mapa (kra.sk, Stream Cinema) → v [NOTES.md](NOTES.md)
- [x] Architektura: monolit, Core/ izolované od Jellyfin API
- [x] Core: KraskaClient, ScCatalog, DownloadQueue (persistentní), DownloadEngine (resume+throttle), MediaOrganizer
- [x] Jellyfin slupka: Plugin, DI registrátor, ScState, DownloadWorkerService
- [x] GUI: configPage.html (hledání, výběr streamu, fronta, nastavení) + REST API
- [x] GitHub Actions (build + release + manifest)
- [x] **Build OK** (net9.0, Jellyfin.Controller 10.11.11, 0 chyb/0 varování)
- [x] Lokální git repo + 1. commit
- [ ] **Push na GitHub** *(na tobě — §4)*
- [ ] **Reálný test na JF 10.11.11** + doladění parsování katalogu na živá data
- [ ] **Fáze 2: Trakt watcher** — automatické stahování z Trakt seznamu

---

## 7) Klíčová rozhodnutí a zásady (proč je to takhle)

- **Monolit místo dvou služeb** — chtěl jsi jednoduchý import na čistý Jellyfin.
  Odolnost vůči budoucímu **Jellyfin 12** (breaking changes) řešíme tím, že složka
  **`Core/` nesmí importovat Jellyfin API** → při migraci se přepíše jen tenká slupka.
- **Stažení na NAS místo on-the-fly resolveru** — tvůj nápad; řeší buffering,
  umožní plné převíjení a jednu společnou knihovnu. Filmy zůstávají trvale,
  plugin jen **hlídá volné místo** (pod práh nezačne další stahování).
- **X-AUTH-TOKEN se NIKDY negeneruje** — schvaluje se ručně na serveru SC.
  Plugin ho jen načte ze zálohy `sc.json` na kra.sk úložišti (vytvořil Kodi addon),
  nebo ho zadáš ručně. Když chybí, zobrazí návod — nikdy nevyrobí nový.
- **Anti-ban = napodobit legitimní klient + mírné tempo.** Stahování je stejný
  request jako streamování; riziko je v OBJEMU a VZORU. Proto: 1 soubor naráz,
  náhodné pauzy mezi soubory, volitelný limit rychlosti, denní strop, jedno UUID +
  jedna session, stejné hlavičky jako Kodi addon. Ban chytíš objemem/velocity,
  ne existencí automatizace.

---

## 8) Bezpečnost — DŮLEŽITÉ

- **Na git NIKDY nepatří přihlašovací údaje ani tokeny.** Zadávají se výhradně
  v GUI pluginu. `.gitignore` má i pojistku (`*.local.json`, `secrets.*`).
- Claude tvé kra.sk heslo **nikdy nevidí ani nevkládá** — zadáváš ho jen ty v GUI.
- Jellyfin ukládá konfiguraci pluginů jako **plaintext XML**
  (`/config/plugins/configurations/`). Heslo tam je čitelné → omez přístup
  ke config datasetu na TrueNASu a nezálohuj ho nešifrovaně do cloudu.
- Plugin heslo ani token nikdy neloguje (maskuje `***`).

---

## 9) Cheat sheet příkazů

```bash
# refresh po instalaci SDK (když dotnet není vidět v aktuálním terminálu) — PowerShell:
$env:PATH = [Environment]::GetEnvironmentVariable("PATH","Machine") + ";" + [Environment]::GetEnvironmentVariable("PATH","User")

dotnet --version
dotnet build   Jellyfin.Plugin.StreamCinema -c Release
dotnet publish Jellyfin.Plugin.StreamCinema -c Release -o publish

git pull                          # PŘED prací na druhém stroji
git add -A && git commit -m "…"
git push
git tag v0.1.1 && git push origin v0.1.1   # nové vydání (CI udělá release+manifest)
```

---

## 10) Kontext pro Claude v nové konverzaci

Když otevřeš nové sezení (na kterémkoli stroji) a chceš pokračovat, stačí říct:
*„Pokračujeme na projektu SC-jellyfin, přečti si HANDOFF.md a NOTES.md ve složce
`…\Nextcloud\IT\AI\Claude\SC-jellyfin\`."* Na tomto notebooku je navíc uložená
poznámka v paměti Claude (na jiném stroji paměť není — proto je tento dokument
hlavní přenosový bod).
