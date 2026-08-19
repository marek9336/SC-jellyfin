# SC-Jellyfin — poznámky k projektu

> Živý dokument. Sem se zapisují rozhodnutí, API detaily a otevřené otázky,
> aby šel projekt kdykoli snadno upravit nebo předat.

## Co to je

Jellyfin plugin (C#/.NET), který nahrazuje Kodi addon **Stream Cinema** (repo cder.sk):
vyhledá film/seriál v katalogu Stream Cinema, **stáhne** vybraný stream z **kra.sk**
na TrueNAS do struktury složek, kterou Jellyfin rozpozná, a spustí sken knihovny.
Streamuje se pak lokálně z NASu → žádný buffering, plné převíjení, transkódování.

## Cílové prostředí

| Co | Hodnota |
|---|---|
| Jellyfin | **10.11.11** (TrueNAS SCALE, oficiální app) |
| Plugin ABI | `10.11.0.0` |
| .NET | **9.0** (`net9.0`) |
| NuGet | `Jellyfin.Controller 10.11.*` |
| Build | GitHub Actions (lokálně není .NET SDK; lze `winget install Microsoft.DotNet.SDK.9`) |
| Pozor | Další major verze Jellyfinu bude **12.0** s breaking changes → logika držená mimo Jellyfin API (viz Architektura) |

## Architektura (rozhodnuto: monolit)

Vše v jednom pluginu = jeden git repo, instalace přes plugin repozitář v Jellyfinu.

```
Jellyfin.Plugin.StreamCinema/
├── Plugin.cs                    # vstupní bod, registrace config stránky (VAZBA NA JF API)
├── PluginServiceRegistrator.cs  # DI registrace hosted service (VAZBA NA JF API)
├── Api/ScController.cs          # REST endpointy pro config stránku (VAZBA NA JF API)
├── Configuration/
│   ├── PluginConfiguration.cs   # nastavení (účet, cesty, throttling)
│   └── configPage.html          # GUI: hledání, fronta, nastavení
└── Core/                        # ★ ČISTÝ C#, ŽÁDNÉ Jellyfin API — přežije JF 12
    ├── KraskaClient.cs          # login, resolve, list, user info
    ├── ScCatalog.cs             # katalog SC (search, play, browse) + bootstrap tokenu
    ├── DownloadQueue.cs         # persistentní fronta (JSON na disku)
    ├── DownloadEngine.cs        # stahování: resume (Range), speed limit, pauzy
    ├── MediaOrganizer.cs        # film vs. seriál → cesty složek/souborů
    └── Models.cs                # datové typy
```

Pravidlo: **do `Core/` nikdy neimportovat Jellyfin namespace.** Až JF 12 rozbije API,
přepisuje se jen slupka (Plugin.cs, controller, registrátor).

## API mapa (vytaženo ze zdrojáků Kodi addonu)

Zdroj: `%APPDATA%\Kodi\addons\plugin.video.stream-cinema\resources\lib\`
(hlavně `api/kraska.py`, `api/sc.py`, `streamcinema.py`, `gui/item.py`, `constants.py`)

### Katalog Stream Cinema
- Base URL: `https://stream-cinema.online/kodi`, API verze `2.0`
- Hlavičky: `User-Agent` (viz Anti-ban), `X-Uuid` (stabilní UUID zařízení), `X-AUTH-TOKEN` (32 znaků)
- Výchozí query parametry: `ver=2.0`, `uid=<uuid>`, `lang=cs`, `skin=<skin>`, `pro=kraska` (když je kra.sk přihlášená)
  - pole parametry (`co`, `ca`, `ge`, `mu`) se posílají PHP stylem `klic[]=hodnota`
- **Hledání**: `GET /Search/search-movies?search=<dotaz>&id=search-movies` (filmy),
  `search-series` (seriály), `search-people` (+`ms=1`)
- **Streamy**: `GET /Play/{id}` (film), `GET /Play/{id}/{sezona}/{epizoda}` (epizoda)
  - odpověď obsahuje pole `strms`; každý stream: `ident`, `provider` (`kraska`),
    `lang`, `quality`, `size`, `vinfo`, `ainfo`, `subs` (URL, ident za `/file/`)
- Odpovědi jsou „menu-driven" JSON pro Kodi (položky s `url`, `info`, `unique_ids`, `mediatype`).
  Parsovat **defenzivně** – přesné tvary ověřovat za běhu. Config stránka funguje jako
  generický prohlížeč: zobrazí, co API vrátí, a následuje `url` položek.

### kra.sk
- Base: `https://api.kra.sk`
- `POST /api/user/login` `{data:{username, password}}` → `session_id` (dále se posílá v těle jako `session_id`)
- `POST /api/user/info` → `data.days_left`, `data.subscribed_until` (kontrola předplatného; <14 dní = upozornit)
- `POST /api/file/download` `{data:{ident}}` → `data.link` = **přímá HTTP URL souboru** (tu stahujeme)
- `POST /api/file/list` `{data:{parent, filter}}` → výpis úložiště (pro nalezení `sc.json`)
- Chyba → invalidovat session a **max 1× retry** s novým loginem (stejně jako addon)

### X-AUTH-TOKEN — KRITICKÉ
Token katalogu se **schvaluje ručně na serveru SC**. NIKDY negenerovat nový!
1. Přednost má **ručně zadaný token** v nastavení pluginu (textbox).
2. Jinak auto-bootstrap: kra.sk login → `/api/file/list` filter `sc.json` (exact match jména!)
   → resolve → stáhnout obsah → **obsah souboru = 32znakový token**.
3. Když bootstrap selže → zobrazit návod (viz GUI), NIC negenerovat.

Návod pro uživatele v GUI: „Přihlas se na kra.sk → Úložiště → soubor `sc.json` →
stáhni, otevři v poznámkovém bloku, zkopíruj 32 znaků do pole níže."
(Soubor tam vytvořil Kodi addon; bez něj token neexistuje → uživatel musí mít
funkční Kodi addon, nebo token získat od SC.)

## Anti-ban pravidla (simulace člověka)

Stahování = stejný request jako streamování; riziko je v OBJEMU a VZORU, ne v metodě.

- **1 souběžné stahování**, natvrdo, bez možnosti zvýšit
- Náhodná pauza mezi soubory: default **5–15 min** (konfigurovatelné)
- Volitelný limit rychlosti (default 50 Mbit/s; 0 = bez limitu)
- Denní strop GB (default 100 GB; 0 = vypnuto) — počítadlo se resetuje o půlnoci
- Volitelné časové okno stahování (např. jen 01:00–07:00)
- **Jedno UUID + jedna session**, recyklovat; login jen když session vyprší
- Hlavičky a parametry identické s Kodi addonem (viz API mapa)
- Nikdy nelogovat heslo/token — maskovat `***`
- Fronta se NEztrácí při restartu JF; rozdělané stahování navazuje přes HTTP Range

## Úložiště a pojmenování (TrueNAS)

Rozhodnuto: **filmy zůstávají trvale**; plugin jen hlídá volné místo
(default min. 50 GB volných → pod limit nezačne další stahování; rozdělané dokončí).

```
<MoviesPath>/Nazev (rok)/Nazev (rok) - [kvalita].mkv
<SeriesPath>/Nazev (rok)/Season 01/Nazev (rok) - S01E03.mkv
```
- Cesty konfigurovatelné v GUI (dvě knihovny: Filmy, Seriály — cesty uvnitř JF kontejneru!)
- Sanitizace názvů: odstranit `\/:*?"<>|`, trim teček/mezer na konci
- Přípona podle skutečného souboru z kra.sk (z URL / Content-Disposition; fallback `.mkv`)
- Nedokončený soubor má příponu `.part` → přejmenuje se až po úspěšném dokončení
- Titulky: pokud stream má `subs`, stáhnout vedle videa jako `<stejny-nazev>.<lang>.srt`
- Po dokončení: spustit sken knihovny Jellyfinu

## Bezpečnost

- Na git NIKDY žádné přihlašovací údaje/tokeny — vše se zadává v GUI pluginu
- JF ukládá config pluginu jako **plaintext XML** v `/config/plugins/configurations/`
  → uživatel: omezit přístup k config datasetu, nezálohovat nešifrovaně do cloudu
- REST endpointy pluginu: jen pro adminy (`RequiresElevation`)

## GUI (configPage.html) — MVP rozsah

1. **Nastavení**: kra.sk user+pass, ruční X-AUTH-TOKEN (fallback), cesty Filmy/Seriály,
   throttling (pauzy, rychlost, denní strop, min. volné místo, časové okno), tlačítko „Otestovat přihlášení"
2. **Hledání**: pole + přepínač Filmy/Seriály → výsledky (název, rok, plakát)
   - u seriálu: procházení sezón/epizod (generické následování `url` z API)
3. **Výběr streamu**: po kliknutí na položku vypsat `strms` (jazyk, kvalita, velikost) → „Přidat do fronty"
4. **Fronta**: tabulka (název, stav, progress, rychlost), akce: odebrat, retry, pauza/obnovit worker

## Stav / TODO

- [x] Analýza Kodi addonu, API mapa
- [x] Rozhodnutí: monolit, trvalé úložiště + hlídač místa, ruční token fallback
- [x] Skeleton projektu (csproj, Plugin.cs, config)
- [x] Core: KraskaClient, ScCatalog, DownloadQueue/Engine, MediaOrganizer
- [x] REST API + configPage.html
- [x] GitHub Actions build + plugin manifest (repozitář pro instalaci do JF)
- [x] **Build OK** (net9.0, Jellyfin.Controller 10.11.11, 0 chyb/0 varování) → viz `artifacts/streamcinema_0.1.0.0.zip`
- [ ] Test na reálném JF 10.11.11 (TrueNAS) — ověřit tvary JSON odpovědí katalogu
- [ ] Fáze 2: Trakt watcher (auto-fronta z Trakt seznamu)

### Poznámky k buildu
- Repo obsahuje `NuGet.Config` (nuget.org) — čisté SDK nemá zdroj nastavený.
- Lokální build: `dotnet build Jellyfin.Plugin.StreamCinema -c Release`.
- Sideload balíček (rychlý test bez GitHubu): rozbalit zip do
  `<jellyfin-config>/plugins/Stream Cinema/` a restartovat server.

## Otevřené otázky / ověřit za běhu

- Přesný tvar JSON odpovědí katalogu (parsujeme defenzivně, doladí se při prvním testu)
- Zda `/Play/{id}` u seriálu bez s/e vrací menu sezón (předpoklad: ano, menu-driven)
- Content-Disposition u kra.sk downloadů (kvůli příponě souboru)
- Jak nejčistěji spustit sken knihovny v JF 10.11 (ILibraryManager vs. task) — ověří CI/test
