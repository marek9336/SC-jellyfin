# SC-Jellyfin — Stream Cinema plugin pro Jellyfin

Jellyfin plugin, který nahrazuje Kodi addon **Stream Cinema**: vyhledá film/seriál
v katalogu Stream Cinema, **stáhne** vybraný stream z **kra.sk** do knihovny
Jellyfinu (např. na TrueNAS) a spustí sken. Sleduje se pak lokálně — žádný
buffering, plné převíjení, transkódování.

> **Předpoklady:** platné předplatné kra.sk a schválený X-AUTH-TOKEN katalogu
> Stream Cinema (viz níže). Plugin je pro osobní použití; na git nikdy nepatří
> žádné přihlašovací údaje — vše se zadává v GUI.

## Požadavky

- Jellyfin **10.11.x** (plugin ABI `10.11.0.0`, .NET 9)
- Účet kra.sk s aktivním předplatným
- Funkční Kodi addon Stream Cinema (kvůli existující záloze `sc.json` — viz Token)

## Instalace

1. V Jellyfinu: **Dashboard → Pluginy → Repozitáře → Přidat**
   a vlož URL manifestu:
   ```
   https://raw.githubusercontent.com/marek9336/SC-jellyfin/main/manifest.json
   ```
2. **Katalog** → nainstaluj „Stream Cinema" → restartuj Jellyfin.
3. **Dashboard → Pluginy → Stream Cinema** → záložka **Nastavení**:
   - vyplň kra.sk jméno + heslo, ulož, klikni „Otestovat přihlášení",
   - klikni **„Načíst token z kra.sk (sc.json)"** — načte schválený token
     z tvého úložiště. Když selže, zadej token ručně (viz níže),
   - nastav cílové složky (cesty **uvnitř Jellyfin kontejneru**, např.
     `/media/movies` a `/media/tvshows` — musí odpovídat knihovnám Jellyfinu).

### X-AUTH-TOKEN ručně

Token katalogu se schvaluje ručně na serveru Stream Cinema — plugin proto
**nikdy negeneruje nový**. Když automatické načtení selže:

1. Přihlas se na **kra.sk** → Úložiště.
2. Najdi soubor **`sc.json`** (vytvořil ho Kodi addon) a stáhni ho.
3. Otevři ho v poznámkovém bloku — obsahem je 32 znaků.
4. Zkopíruj je do pole **„Ruční token"** v nastavení pluginu a ulož.

## Použití

- **Hledání**: najdi film/seriál, u seriálů procházej sezóny a epizody,
  klikni na položku → vyber stream (jazyk/kvalita/velikost) → **Stáhnout**.
- **Fronta**: sleduj průběh, pozastav worker, odeber nebo zopakuj položky.
- Stahuje se **vždy jen jeden soubor** s náhodnými pauzami mezi soubory —
  záměrně, aby provoz vypadal jako běžné sledování. Výchozí limity
  (rychlost, denní strop, volné místo) měň s rozmyslem.

## Bezpečnostní poznámky

- Jellyfin ukládá konfiguraci pluginů jako **plaintext XML**
  (`/config/plugins/configurations/`). Tvé kra.sk heslo tam je čitelné —
  omez přístup ke config datasetu a nezálohuj ho nešifrovaně do cloudu.
- Plugin heslo ani tokeny nikdy neloguje.
- REST endpointy pluginu vyžadují admin oprávnění.

## Build (vývoj)

Lokálně: .NET 9 SDK → `dotnet build Jellyfin.Plugin.StreamCinema`.
CI: GitHub Actions builduje každý push; release vznikne pushnutím tagu
`v*` (např. `v0.1.0`) — workflow vytvoří zip, GitHub Release a aktualizuje
`manifest.json`.

Architektura a poznámky pro vývoj: [NOTES.md](NOTES.md). Klíčové pravidlo:
složka `Core/` nesmí importovat Jellyfin API (kvůli snadné migraci na JF 12).

## Stav

Rané MVP — tvary odpovědí katalogu se ověřují za běhu (parsují se defenzivně).
Plánované: Trakt.tv watcher (automatické stahování z playlistu).
