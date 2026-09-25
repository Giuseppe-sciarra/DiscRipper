# DiscRipper

Estrazione di CD audio in **MP3, FLAC, WAV, AAC (M4A), OGG Vorbis e Opus** con riconoscimento automatico del disco e tag completi. Stessa famiglia di DVDRescue / Tape2MP3 / mp4todvd (C# WinForms .NET 8, build su GitHub Actions).

## Cosa fa
- Legge il CD appena lo inserisci (TOC + CD-Text) e cerca il disco su **MusicBrainz** (gratis, senza account). Copertina da **Cover Art Archive**. Riserva opzionale: **GnuDB** (serve solo un'email nelle impostazioni). Se non trova niente usa il CD-Text o ti fa scrivere i dati a mano.
- Lettura **veloce + verifica AccurateRip**. Se una traccia non coincide (o il disco non è nel database) la rilegge una seconda volta e poi **rilegge ostinatamente solo i settori che differiscono** finché due letture coincidono (modalità paranoia). Opzione "Paranoia sempre" per i CD rovinati.
- **Offset del lettore rilevato da solo** alla prima estrazione di un CD presente in AccurateRip, e salvato per quel modello di lettore.
- Salva in `Artista\Album (Anno)\01 - Titolo.ext` sulla cartella che scegli (anche unità di rete / `\\server\share`). Per le compilation: `01 - Artista - Titolo`. Per i dischi multipli: sottocartella `CD 1`, `CD 2`.
- Tag: titolo, artista, artista album, album, anno, genere, traccia n/tot, disco n/tot, copertina incorporata, ID MusicBrainz. ID3v2.3 per gli MP3.
- `cover.jpg` e file `.log` dell'estrazione (checksum CRC32 e AccurateRip per traccia) nella cartella dell'album.
- Tema chiaro / scuro / come il sistema.

## Build
Metadati: MusicBrainz, CUETools DB (MusicBrainz + Discogs + freedb) e GnuDB opzionale, tutti insieme. Impostazioni in `%APPDATA%\DiscRipper\settings.json`, salvate subito a ogni modifica (anche posizione della finestra).

Ogni push su `main` → la GitHub Action compila, gira i test e pubblica da sola la release `v1.0.<n. build>` con lo zip (DiscRipper.exe self-contained + ffmpeg.exe). Si può lanciare anche a mano da Actions → Run workflow.

Locale: `dotnet publish src/DiscRipper -c Release -r win-x64 --self-contained -p:PublishSingleFile=true` e metti `ffmpeg.exe` accanto all'exe.

Test degli algoritmi (disc ID MusicBrainz/CDDB/AccurateRip su vettori noti, CRC AccurateRip, rilevamento offset, lettura sicura con lettore simulato):
`dotnet run --project tests/DiscRipper.Tests` (aggiungi `--online` per provare MusicBrainz/AccurateRip dal vivo, `--encode` per codifica+tag con ffmpeg nel PATH).

## Impostazioni
Salvate in `%APPDATA%\DiscRipper\settings.json`.
