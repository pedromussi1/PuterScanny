<p align="center"><img src="src/PuterScanny.App/Assets/PuterScanny.png" width="96" alt="PuterScanny icon"></p>

# PuterScanny

**A free Windows app that scans a drive or folder, explains what every file is, and recommends what to keep and what you can safely delete.**

Free to download and use, with no account, no ads and no subscription. It never sends anything about your files anywhere.

[**Download the latest release**](https://github.com/pedromussi1/PuterScanny/releases/latest) · Windows 10 (2004) or 11, 64-bit

---

## What it does

Pick a drive or folder and press **Scan**. For every file and folder, PuterScanny tells you:

- **What it is**, by its real content and not just its name. It reads file signatures, program details, publishers and digital signatures, and knows 992 file types.
- **What its name and dates really mean**, by well-known conventions instead of taking them literally. For example, 24 Dec 1996 23:32 is TorrentZIP's fixed date on ROM sets, *(USA) (En,Fr)* is a No-Intro release region and language list, *IMG_20230512_101112* and *WhatsApp Image 2024-02-01 at…* carry the date taken, *report (1).pdf* is a repeat download, and *__MACOSX* and *._* files are Mac leftovers. The list is plain JSON (`data/knowledge/known-details.json`), and each entry only applies on an exact match.
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
- **Ask**: ask questions about the scan in plain English (see below).
- **Export**: an HTML report, a CSV spreadsheet of every item, or JSON.
- **Dark mode**: the **Dark mode** button at the top right switches between light and dark. Until you use it, PuterScanny follows Windows' own setting.

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

### Ask about your scan

Ask questions about the scanned drive or folder and get answers from the scan itself, for example: *"What can I safely delete in Downloads?"*, *"Which videos haven't I watched in a year?"*, *"Are there any duplicates?"*, *"Why is OllamaSetup.exe marked for deletion?"* or *"What's taking the most space?"*. Press **Ask about this** in the Folders or Treemap tab to ask about one folder or file.

- **Ask what something is:** *"What is BmGame?"*, *"What are these files?"* or *"What is this folder?"* gets a full description: what's inside it (kinds of files, with example names), what it's part of (a program, a mod download, a game), what a readme next to it says, and PuterScanny's recommendation. The facts show at once. On a PC fast enough for it, the local AI then adds a short summary on top saying what they add up to (for example, that a mod's Bink videos are the game's cutscenes), checked like every other answer.
- **Topics:** *"Any files from a Mac?"*, *"Do I have any AI models?"*, *"Which folders are code projects?"*, *"Any screenshots?"*, *"Which of these are Game Boy games?"* look things up by the known conventions, file types and project files (a `.git` folder, `package.json`…), not by guessing.
- **Instant and exact:** questions the app reads with confidence are answered straight from the scan, with exact sizes and counts and no AI involved.
- **When it isn't sure, it says so, or asks the local AI.** If some words of a question don't fit how it was read ("Batman" in *"is it safe to remove the Batman mod files?"*), the answer says how it was read. With Local AI set up, the AI works out what the question means and picks the lookups instead; the answer still comes from the scan, so a wrong choice shows as what was looked up rather than as a wrong fact. Questions the app doesn't understand at all go the same way.
- **Checked:** anything the AI writes (summaries) must match the scan: every size, count, year, file name and recommendation. It can't say something doesn't exist unless a lookup actually searched for it. If it doesn't pass, it isn't shown.
- **Every answer lists the items it's based on.** From each one you can show it in Folders, open it in File Explorer, ask why it got its recommendation, or tick it into the clean-up basket.
- **Keep or delete still comes only from PuterScanny's own checks.** The chat reports PuterScanny's recommendation and can't delete anything itself.
- **Everything runs on this PC,** with no key, account or internet.

### Local AI explanations (optional, offline)

For the few items its own rules can't identify, PuterScanny can use a **free AI model that runs entirely on your PC** to write a short plain-English guess at what each one is.

- **Setup:** download a model in **Local AI** (Qwen3.5 4B, Apache-2.0, 2.6 GB, from Hugging Face, checked against a fixed fingerprint). A smaller 1.2 GB model is offered too, or you can use any GGUF model file you already have.
- **Automatic:** once a model is there, it's used only when it helps, and only as much as the PC can comfortably run. PuterScanny checks free memory, the processor, whether a laptop is on battery and how fast the model has actually been running: on a fast PC it can add summaries; on a slower one it only works out questions; with too little free memory it isn't used, and the answer says why. You can also turn it off completely.
- **Privacy:** after that it works offline. Nothing about your files leaves the PC, and there's no account, key or cost.
- **Limits:** the AI only *explains*. It isn't told the recommendation, any advice it writes is removed, and every answer is labelled as AI-written. Keep-or-delete decisions always come from PuterScanny's own checks.
- **Also used by Ask:** the Ask tab uses it to understand questions in your own words (see above).

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
| `PuterScanny.Chat` | "Ask about your scan": read-only lookups, question matching, and the local AI's checked answers |
| `PuterScanny.App` | The WPF app |
| `tests` | xUnit tests, including a safety suite that scans the real System32 and checks that nothing is ever recommended for deletion |

The knowledge base is plain JSON. Corrections and additions are welcome as pull requests.

## Licence

[MIT](LICENSE). PuterScanny is built on free software; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

PuterScanny gives advice, not guarantees. Review what you clean up; everything it removes can be restored from the app.
