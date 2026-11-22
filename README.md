# Worktrace

Tools for building a timesheet from what you actually did on your computer.

So far the repo contains one component, **TimeCapture**: a Windows tray application that records which
application and window you work in and, if you want, keeps encrypted screenshots of the windows you
allow. Everything stays on your machine. The application contains no network code.

## What TimeCapture records

Rules in `config.json` decide how much is recorded for each window:

| Level | Recorded |
|---|---|
| `capture` | Application name, window title and screenshots |
| `logWithTitles` | Application name and window title |
| `log` | Application name only |
| nothing | Only that "another application" was in use, without name or title |

Include rules grant a level, exclude rules take one away (see [Rules](#rules)). A window that no include rule
covers gets nothing.

It also records when you are idle, when the screen is locked and when the computer sleeps.

What is kept of a captured window comes in two layers. The first is safer to hand on than the second:

| Layer | Content | Setting | Secrets |
|---|---|---|---|
| 1 | Application name, window title, timing | always | Titles are checked at capture time; words that look like a secret become `[redacted]` |
| 2 | The screenshot, encrypted | `storeImages` | Stored as captured; checked only when it is read (see below) |

With `stripUrlQueries`, URLs in titles also lose their query string and fragment (`?code=...`,
`#access_token=...`).

**No text recognition at capture time.** TimeCapture does not read the text in the screenshots when
it takes them, and the database never holds recognized text. Text and secret redaction are produced
on demand from the stored image, so they exist exactly as long as the image (`retentionDays`). Every
path that takes a screenshot or its text out of storage (export for a model, building the timesheet)
has to go through that on-demand redaction first. `timecapture-cli screenshot` does this, and hands
nothing on without your approval (see [Handing a day to Claude](#handing-a-day-to-claude)). To look
at screenshots yourself, use **Export day…** in the tray menu, which writes them unredacted.

Some things are never recorded, whatever the rules say:

- Private browser windows (InPrivate, Incognito, Private Browsing).
- The Windows shell (desktop, taskbar) and TimeCapture's own windows.

Screenshots are stored as lossless PNG files at full size, encrypted (`encryptImages`), and deleted
after `retentionDays` days. Window titles and events are kept. A window that looks exactly as in
its last stored screenshot is not stored again: same title, and no visible change when the two are
compared pixel by pixel at full size (a blinking text cursor does not count).

Databases created by versions before 1.0.0 (which stored recognized text) are not migrated: the app
refuses to start with one. Delete `capture.db` (and `capture.db-wal`, `capture.db-shm`) in the
storage root.

### Protection on disk

- **Folder permissions.** On start, the storage root is restricted to your Windows account and
  SYSTEM, without inherited permissions, as long as it holds only this app's files. It is also
  excluded from Windows Search indexing.
- **Synced folders.** If the storage root lies in a OneDrive, Dropbox, Google Drive or iCloud folder,
  the app warns in its log and console. Move it out: the database is not encrypted.
- **Encrypted screenshots** (`encryptImages`). Every screenshot is encrypted in memory with AES-256-GCM
  before it is written, as `.png.enc`; no unencrypted image touches the disk. Each day has its own
  random key, stored in `capture.db` wrapped with Windows DPAPI, so only your Windows login can unwrap
  it. Retention deletes the keys of expired days, which makes their images unrecoverable even where
  the disk still holds the old data. To look at screenshots, use **Export day…** in the tray menu:
  it decrypts one day into a folder you choose (delete it when you are done).

What encryption protects against: other accounts on the machine, the disk read offline (stolen
laptop, old drive), backups and sync tools that copy the files, and the Explorer thumbnail cache.
What it does not protect against: malware running under your account, which can ask DPAPI for the
keys just like the app does. The data is tied to your Windows profile: after a reinstall, or if an
administrator resets your password, the encrypted screenshots can no longer be read. A separate
recovery key would be a possible addition later. Turning `encryptImages` off stores new screenshots
as plain `.png`; encrypted ones stay readable.

## Build and run

You need Windows (x64) and the .NET 10 SDK.

```powershell
dotnet build Worktrace.slnx
dotnet test --project tests/Capture.Core.Tests
dotnet run --project src/Capture.App
dotnet run --project src/Capture.Cli -- export
dotnet publish src/Capture.App -c Release
```

`dotnet publish` produces a self-contained `TimeCapture.exe` with its native libraries and
`config.json` next to it.

TimeCapture appears as an icon in the notification area. Its icon and menu show the current state
(capturing, idle, paused, not capturing). The menu lets you pause for 15 minutes, for an hour or until
resumed, export a day's screenshots (**Export day…**), open the config file, and turn "Start with
Windows" on or off. Started from a terminal, it also prints what it does there.

## Configuration

TimeCapture reads `config.json` from the folder of `TimeCapture.exe` and does not start without it.
Changes take effect when TimeCapture is restarted. When you build and run from the repo, that is
`src/Capture.App/config.json`.

### Rules

There are two lists of rules, `include` and `exclude`, and their rules look the same:

```json
"include": [
  { "process": "Code.exe" },
  { "process": "msedge.exe", "mode": "logWithTitles" },
  { "process": "Outlook.exe", "mode": "log" },
  { "process": "chrome.exe", "titlePatterns": ["*GitHub*", "*Stack Overflow*"] }
],
"exclude": [
  { "process": "msedge.exe", "titlePatterns": ["*YouTube*", "*Netflix*"] },
  { "process": "*", "mode": "log", "titlePatterns": ["*Bank*"] }
]
```

| Field | Meaning |
|---|---|
| `process` | Executable name. Upper and lower case do not matter and `.exe` is optional. `*` applies to every application. |
| `mode` | `capture` (the default), `logWithTitles` or `log`. A rule with any other value is ignored. |
| `titlePatterns` | Optional. The rule only applies to windows whose whole title matches one of these patterns. `*` stands for any text, `?` for one character. |

The level of a window is decided like this:

1. **Include:** the strongest matching include rule grants its level. Without one, nothing is recorded.
   The include rules add up: a `*` rule and an application's own rules all count.
2. **Exclude:** every matching exclude rule takes its level, and everything above it, away. Excludes
   always win over includes.

| Exclude `mode` | Takes away | At most left |
|---|---|---|
| `capture` | Screenshots | `logWithTitles` |
| `logWithTitles` | Window title and screenshots | `log` |
| `log` | Everything | nothing |

`capture` is the default in both lists, so an exclude rule without `mode` only stops screenshots;
the window is still logged with its title. Use `"mode": "log"` to leave a window out entirely.

In the example, YouTube and Netflix in Edge are logged with their title but never captured, and
windows with "Bank" in their title are not recorded at all. A browser's window title is the title of
its active page, so this works per site, as far as the site names itself in its title.

An include `*` rule skips windows without a title. Invisible overlays, such as the NVIDIA overlay, are
never captured and do not hide the windows below them.

### Settings

| Setting | Default | Meaning |
|---|---|---|
| `storageRoot` | `""` | Folder for the database, screenshots and logs. Empty means `%LOCALAPPDATA%\TimeCapture`. Environment variables such as `%USERPROFILE%` are expanded. |
| `inputPollIntervalMs` | 1000 | How often keyboard and mouse activity is checked. |
| `windowSwitchDelayMs` | 1000 | Wait after a window switch or title change before taking a screenshot. |
| `typingPauseMs` | 2000 | A pause of this length after input triggers a screenshot. |
| `fallbackIntervalSeconds` | 60 | Take a screenshot at least this often while you are active. |
| `minCaptureIntervalSeconds` | 5 | Minimum time between two screenshots. |
| `idleTimeoutSeconds` | 180 | No input for this long counts as idle. |
| `retentionDays` | 14 | Screenshots older than this are deleted. |
| `storeImages` | true | Keep the screenshots (layer 2). Off keeps titles and timing only. |
| `encryptImages` | true | Encrypt the screenshots on disk, see [Protection on disk](#protection-on-disk). |
| `maxImageDimension` | 0 | Screenshots are scaled down so the longer side has at most this many pixels. 0 keeps the original size, which keeps small text readable. |
| `stripUrlQueries` | false | Cut query strings and fragments off URLs in stored window titles, also where the secret check sees nothing. |

## Handing a day to Claude

`timecapture-cli` gives Claude Code what it needs to work out a timesheet. `export` and `sessions`
contain no screen content; `screenshot` passes through your hands one screenshot at a time.

```powershell
timecapture-cli export [--day 2026-11-20] [--root DIR]
timecapture-cli sessions [--day 2026-11-20] [--transcripts DIR]
timecapture-cli screenshot <id> [--out DIR] [--root DIR]
timecapture-cli screenshot --at "2026-11-20 14:30" [--out DIR] [--root DIR]
```

`--day` defaults to today. The storage root comes from `--root`, else the `TIMECAPTURE_ROOT`
environment variable, else `storageRoot` in the `config.json` next to `timecapture-cli.exe`, else
`%LOCALAPPDATA%\TimeCapture`.

The repo includes a Claude Code skill, [`timesheet`](.claude/skills/timesheet/SKILL.md), that walks
Claude through these commands to draft a day's timesheet entries.

**`export`** prints what TimeCapture recorded for one day: the visits (which window had focus, from
when to when, with the window title as stored, that is, scrubbed) and the ids of the screenshots
taken during each visit.

**`sessions`** summarizes the Claude Code sessions of that day from the transcripts in
`%USERPROFILE%\.claude\projects` (`--transcripts` names another folder): folder, branch, active
times, the prompts you typed, and each session's transcript file. The transcripts themselves are
large, so Claude reads this summary first and opens a single transcript only when it needs more.
It is a separate command because it is not TimeCapture's data, and the transcript format belongs to
Claude Code.

**`screenshot`** is how Claude asks for a screenshot when titles and transcripts are not enough. It
takes an id from the export, or `--at` a time (the focused window captured closest to it, within five
minutes). Then:

1. The image is decrypted in memory. Nothing unredacted is written to disk.
2. PaddleOCR (PP-OCRv5, Latin script, running locally with bundled models) reads the text and its
   position in the image.
3. The redaction rules (the same secret detector as for window titles) replace secrets in the text
   with `[redacted]` and paint them black in the image.
4. A window shows you the redacted image and the redacted text, exactly as Claude would get them.
5. Only if you click **Share with Claude** does the tool write the redacted PNG (to
   `%TEMP%\timecapture-shared`, or `--out`) and print its path and the text with positions. Anything
   else (**Don't share**, Escape, closing the window) prints that you declined and exits with code 3.

The approval is enforced by the program, not by instructions to the model. There is no option to
skip the review. The **Share** button reacts only to a real mouse click: not to the keyboard, not to
UI Automation, not to messages sent to the window, and not to clicks injected with `SendInput` (a
low-level mouse hook checks that the click was not injected). It stays disabled for a moment after
the window opens, so a click meant for another window cannot land on it. This matters because Claude
runs commands under your account and could otherwise press the button with an automation tool.

What this does not cover: Claude runs as you, so it could in principle read `capture.db` or decrypt
the screenshots itself the way this tool does. Deny it access to the storage root in your Claude
Code permissions, and treat the review window as the gate for what you *intend* to share, not as
protection against a model working against you.

The text recognition adds about 400 MB of native libraries and models to the tool's folder, and
takes a few seconds per screenshot.

## Where the data goes

Inside the storage root:

- `capture.db`: a SQLite database with the activity events, one entry per captured window and the
  wrapped day keys of the screenshots. It holds no recognized screen text.
- `captures\`: the screenshots, one folder per day, encrypted (`.png.enc`).
- `logs\`: the application's log files. They contain no window titles and no screen content.

## Repository layout

| Path | Content |
|---|---|
| `src/Capture.Core` | Rules, capture triggers, redaction and image handling, independent of Windows |
| `src/Capture.Windows` | Window inspection and screen capture |
| `src/Capture.Storage` | SQLite database, image files, retention |
| `src/Capture.App` | The tray application |
| `src/Capture.Cli` | `timecapture-cli`: daily export, on-demand text recognition and the review before sharing |
| `tests/Capture.Core.Tests` | Unit tests |

## License

[MIT](LICENSE)
