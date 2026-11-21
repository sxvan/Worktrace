# open-timesheet

Tools for building a timesheet from what you actually did on your computer.

So far the repo contains one component, **TimeCapture**: a Windows tray application that records which
application and window you work in and, if you want, keeps encrypted screenshots of the windows you
allow. Everything stays on your machine. The application contains no network code.

## What TimeCapture records

For each application, a rule in `config.json` decides how much is recorded:

| Rule | Recorded |
|---|---|
| `capture` | Application name, window title and screenshots |
| `log` with `logTitles` | Application name and window title |
| `log` | Application name only |
| no rule | Only that "another application" was in use, without name or title |

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
has to go through that on-demand redaction first. Until it exists, the only way to see screenshots
is **Export day…** in the tray menu, which writes them unredacted for you to look at.

Some things are never recorded, whatever the rules say:

- Private browser windows (InPrivate, Incognito, Private Browsing).
- The screen while a password field has focus (`skipPasswordFields`).

Screenshots are stored as lossless PNG files at full size, encrypted (`encryptImages`), and deleted
after `retentionDays` days. Window titles and events are kept.

Databases created by earlier versions (which stored recognized text) are not migrated: the app
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
recovery key would be a possible addition later. Screenshots from earlier versions (`.avif`, `.png`)
are not encrypted and stay readable. Turning `encryptImages` off stores new screenshots unencrypted;
encrypted ones stay readable.

## Build and run

You need Windows (x64) and the .NET 10 SDK.

```powershell
dotnet build OpenTimesheet.slnx
dotnet test --project tests/Capture.Core.Tests
dotnet run --project src/Capture.App
```

TimeCapture appears as an icon in the notification area. Its menu lets you pause and resume recording,
open the captures folder, open the config file, and turn "Start with Windows" on or off. Started from a
terminal, it also prints what it does there.

## Configuration

TimeCapture reads `config.json` from the folder of `TimeCapture.exe` and does not start without it.
Changes take effect when TimeCapture is restarted. When you build and run from the repo, that is
`src/Capture.App/config.json`.

### Rules

```json
"rules": [
  { "process": "Code.exe" },
  { "process": "msedge.exe", "mode": "log", "logTitles": true },
  { "process": "Outlook.exe", "mode": "log" },
  { "process": "chrome.exe", "titlePatterns": ["*GitHub*", "*Stack Overflow*"] }
]
```

| Field | Meaning |
|---|---|
| `process` | Executable name. Upper and lower case do not matter and `.exe` is optional. `*` applies to every application that has no rule of its own. |
| `mode` | `capture` (the default) or `log`. A rule with any other value is ignored. |
| `logTitles` | For `log` rules: also store the window title. Leave it off for password managers, mail and the like. |
| `titlePatterns` | Optional. The rule only applies to windows whose whole title matches one of these patterns. `*` stands for any text, `?` for one character. |

When an application has rules of its own, only those decide. A `log` rule for one application therefore
exempts it from a `*` rule that captures everything else. If several rules match a window, `capture`
wins over `log`. The `*` rule skips windows without a title. Invisible overlays, such as the NVIDIA overlay, are
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
| `dedupeThreshold` | 4 | How similar a screenshot must be to the previous one to count as a duplicate. Higher values treat more screenshots as duplicates. |
| `stripUrlQueries` | false | Cut query strings and fragments off URLs in stored window titles, also where the secret check sees nothing. |
| `skipPasswordFields` | true | Take no screenshot while a password field has focus. |

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
| `src/Capture.Windows` | Window inspection, screen capture, text recognition, password field detection |
| `src/Capture.Storage` | SQLite database, image files, retention |
| `src/Capture.App` | The tray application |
| `tests/Capture.Core.Tests` | Unit tests |

## License

[MIT](LICENSE)
