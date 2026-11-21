# open-timesheet

Tools for building a timesheet from what you actually did on your computer.

So far the repo contains one component, **TimeCapture**: a Windows tray application that records which
application and window you work in, reads the text in the windows you allow and, if you want, keeps
screenshots of them. Everything stays on
your machine. The application contains no network code.

## What TimeCapture records

For each application, a rule in `config.json` decides how much is recorded:

| Rule | Recorded |
|---|---|
| `capture` | Application name, window title and screenshots |
| `log` with `logTitles` | Application name and window title |
| `log` | Application name only |
| no rule | Only that "another application" was in use, without name or title |

It also records when you are idle, when the screen is locked and when the computer sleeps.

What is kept of a captured window comes in three layers. Each layer is safer to hand on than the next:

| Layer | Content | Setting | If text recognition misses a secret |
|---|---|---|---|
| 1 | Application name, window title, timing | always | Titles are checked as text, so nothing is missed by recognition |
| 2 | Recognized text with its position in the window | `storeText` | The text was never read, so it is not stored either |
| 3 | The screenshot | `storeImages` | It stays visible in the image |

Titles and recognized text pass the same secret check as the screenshots: such words are replaced
by `[redacted]`. With `stripUrlQueries`, URLs also lose their query string and fragment
(`?code=...`, `#access_token=...`).

Some things are never recorded, whatever the rules say:

- Private browser windows (InPrivate, Incognito, Private Browsing).
- The screen while a password field has focus (`skipPasswordFields`).
- Text that looks like a secret. Every captured window is read with the Windows text recognition
  before anything of it is saved; such text is blacked out in the screenshot and replaced in the
  stored text. If the text cannot be read, nothing of the window's content is kept (`redactSecrets`).

Screenshots are stored as lossless PNG files at full size and deleted after `retentionDays` days, recognized text after
`textRetentionDays` days. Window titles and events are kept.

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
| `storeText` | true | Store the recognized text of captured windows (layer 2). Needs `redactSecrets`. |
| `textRetentionDays` | 30 | Recognized text older than this is deleted. |
| `storeImages` | true | Keep the screenshots (layer 3). Off keeps titles and recognized text only. |
| `maxImageDimension` | 0 | Screenshots are scaled down so the longer side has at most this many pixels. 0 keeps the original size, which keeps small text readable. |
| `dedupeThreshold` | 4 | How similar a screenshot must be to the previous one to count as a duplicate. Higher values treat more screenshots as duplicates. |
| `redactSecrets` | true | Black out text that looks like a secret, as described above. |
| `stripUrlQueries` | false | Cut query strings and fragments off URLs in stored titles and text, also where the secret check sees nothing. |
| `skipPasswordFields` | true | Take no screenshot while a password field has focus. |

## Where the data goes

Inside the storage root:

- `capture.db`: a SQLite database with the activity events, one entry per captured window and the
  recognized text.
- `captures\`: the screenshots, one folder per day.
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
