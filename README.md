<p align="center"><img src="src/PuterScanny.App/Assets/PuterScanny.png" width="96" alt="PuterScanny icon"></p>

# PuterScanny

**A free Windows app that scans a drive or folder, explains what every file is, and recommends what to keep and what you can safely delete.**

Free to download and use, with no account, no ads and no subscription. It never sends anything about your files anywhere.

[**Download the latest release**](https://github.com/pedromussi1/PuterScanny/releases/latest) · Windows 10 (2004) or 11, 64-bit

---

## What it does

Pick a drive or folder and press **Scan**. For every file and folder, PuterScanny tells you:

- **What it is**, by its real content and not just its name. It reads file signatures, program details, publishers and digital signatures, and knows 861 file types.
- **Who it belongs to**: which installed program, game, store app or package manager put it there. Portable programs (unzipped tools, emulators, game builds that run without being installed) are recognised too, and their folders are kept whole, so the files they need, such as a game's disc image, are never offered for deletion on their own.
- **Whether Windows needs it**: system files protected by Windows, drivers, services, scheduled tasks, startup entries, fonts, and files that running programs are using right now.
- **When you last used it**, from Windows' own records: program launches, recent files, apps' recent lists, Office, Steam and, as administrator, Prefetch run counts.
- **A recommendation, with its reasons and how sure it is**: *Required by Windows*, *Keep*, *Keep (your file)*, *Review*, *Safe to delete* or *Recommended to delete*. Items where nothing points either way are shown as *No reason to remove*, so they aren't mistaken for things that must stay.

It also finds duplicate files (compared byte for byte), leftovers from uninstalled programs, caches, old installers, game-mod downloads from Nexus Mods, crash dumps, empty folders and broken shortcuts. Programs you no longer use get an "uninstall it properly" suggestion instead of having their files deleted.

### Views

- **Overview**: where the space goes, what can be freed, and what to look at first.
- **Treemap**: every box is a folder or file, sized by space used and coloured by recommendation.
- **Folders**: the full tree, biggest first, with a details panel explaining each item.
- **Clean up**: delete, review, Windows tools, programs to uninstall, duplicates.
- **Also**: largest files, programs, file types, usage history, "must keep", and everything that was skipped and why.
- **Export**: an HTML report, a CSV spreadsheet of every item, or JSON.

### Safe clean-up

- Nothing is ever deleted without you choosing it and confirming.
- Items go to the **Recycle Bin** or PuterScanny's **quarantine**, and every clean-up can be **undone** from the app.
- Each item is checked again right before it's moved. Anything that changed since the scan, is in use, or is protected is skipped.
- Windows' own files, program files and anything a service, driver or running program needs can never be deleted from PuterScanny.
- As administrator, it can create a System Restore point first.

### Deep scan (optional, administrator)

Tick **Deep scan** and Windows asks for permission. PuterScanny then reads the drive's file table directly:

- every corner is covered, including System Volume Information
- sizes are exact, and hard links are counted once
- it's fast: about 2 million files in a few seconds
- it adds Windows' program-run records (Prefetch, Background Activity Moderator)

If you say no, it runs a normal scan and the report lists what it couldn't check.

### Local AI explanations (optional, offline)

For the few items its own rules can't identify, PuterScanny can use a **free AI model that runs entirely on your PC** to write a short plain-English guess at what each one is.

- **Setup:** turn it on in **Local AI**. The model (Qwen3.5 4B, Apache-2.0, 2.6 GB) is downloaded once from Hugging Face and checked against a fixed fingerprint. A smaller 1.2 GB model is offered too, or you can use any GGUF model file you already have.
- **Privacy:** after that it works offline. Nothing about your files leaves the PC, and there's no account, key or cost.
- **Limits:** the AI only *explains*. It isn't told the recommendation, any advice it writes is removed, and every answer is labelled as AI-written. Keep-or-delete decisions always come from PuterScanny's own checks.

## Install

1. Download `PuterScanny-<version>-setup.exe` from [Releases](https://github.com/pedromussi1/PuterScanny/releases/latest) and run it. It installs for your account only and doesn't need administrator rights. Prefer no installer? Use the portable `.zip`: unzip it anywhere and run `PuterScanny.exe`.
2. **"Windows protected your PC"?** The app isn't code-signed yet (signing certificates cost money; free signing for open-source projects may come later). Click **More info**, then **Run anyway**. You can check your download against `SHA256SUMS.txt` on the release page:
   ```powershell
   Get-FileHash .\PuterScanny-*-setup.exe -Algorithm SHA256
   ```

Uninstall from **Settings > Apps**. The uninstaller asks whether to remove saved scans and AI models. Files in quarantine are always kept.

## Good to know

- **Gentle by default:** "Go easy on this PC" checks fewer files at once, at low disk priority, so antivirus and drivers are never overwhelmed. A full large drive can take an hour or two this way. Untick it for speed. Interrupted scans continue where they stopped.
- **Where data is kept:** scans, settings, quarantine and AI models are in `%LOCALAPPDATA%\PuterScanny`.
- **Read-only scanning:** scanning never changes a file. Cloud-only files (OneDrive and similar) are never downloaded, and links are recorded but not followed.

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows.

```powershell
dotnet build PuterScanny.slnx
dotnet test PuterScanny.slnx
dotnet run --project src\PuterScanny.App
```

To make a release (installer, portable zip and checksums in `dist\`), install [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`) and run:

```powershell
powershell -ExecutionPolicy Bypass -File build\release.ps1
```

### How it's organised

| Project | What it does |
|---|---|
| `PuterScanny.Core` | Shared models (entries, verdicts, protection levels) and helpers |
| `PuterScanny.Scanning` | Lists every file: Win32 enumeration, plus the raw NTFS file table for Deep scan |
| `PuterScanny.Storage` | Each scan is a SQLite database (crash-safe, resumable) |
| `PuterScanny.Knowledge` | Loads the curated knowledge base in `data/knowledge` (file types, file signatures, known Windows and app locations) |
| `PuterScanny.Signals` | Gathers evidence: file contents, signatures, installed programs, system references, usage history |
| `PuterScanny.Analysis` | The pipeline, the recommendation engine, duplicates, leftovers, reports |
| `PuterScanny.Cleanup` | Recycle Bin, quarantine, undo, safety re-checks, restore points |
| `PuterScanny.AI` | Optional offline explanations (LLamaSharp / llama.cpp), model downloads |
| `PuterScanny.App` | The WPF app |
| `tests` | xUnit tests, including a safety suite that scans the real System32 and checks that nothing is ever recommended for deletion |

The knowledge base is plain JSON. Corrections and additions are welcome as pull requests.

## Licence

[MIT](LICENSE). PuterScanny is built on free software; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

PuterScanny gives advice, not guarantees. Review what you clean up; everything it removes can be restored from the app.
