---
name: timesheet
description: Build timesheet entries for a day from TimeCapture's recording and the day's Claude Code sessions. Use when the user asks what they worked on, or for timesheet entries, for a given day.
---

# Timesheet from TimeCapture

## Tool

`timecapture-cli` (project `src/Capture.Cli`). Run it with
`dotnet run --project src/Capture.Cli -c Release -- <command>` from the repo root, or the built
`timecapture-cli.exe`.

## Steps

1. **Read the day.** `timecapture-cli export --day yyyy-MM-dd` (default: today). It lists the
   visits (focused window and title, or idle, locked, asleep, paused), the screenshot ids per visit
   (`shots: <id>@HH:mm`), and the day's Claude Code sessions with folder, branch, active spans and
   prompts. `git log` in the repos named there can add what was committed.

2. **Assign the visits to projects** from window titles, session folders, branches and prompts.
   Leave out what is clearly not work.

3. **Ask for a screenshot only when that data cannot settle it**, for example a browser title that
   names no project, or a long stretch in one generic window. Take an id from `shots`, or a time:

   ```
   timecapture-cli screenshot <id>
   timecapture-cli screenshot --at "yyyy-MM-dd HH:mm"
   ```

   Run it with a Bash timeout of 600000 ms: it opens a window in which the user reviews the
   recognized text and the redacted image, and waits for them. Tell the user beforehand which
   screenshot you are asking for and why. If they share it, the output gives the path of the
   redacted PNG (read it) and the text with positions. Exit code 3 means they declined: do not ask
   for the same screenshot again, and work with what you have or ask the user.

   Ask for as few screenshots as possible, one at a time.

4. **Build the entries** (defaults the user has used, unless they say otherwise):
   - A visit lasts until the next event; idle, lock, sleep and pause end it.
   - Bridge gaps between visits to the same project of at most 10 minutes; never bridge idle or
     lock periods. Longer gaps split the block.
   - Round each block's start and end to the nearest 15 minutes; drop blocks that round to zero.
   - Output one entry per block: project, start, end, duration, and the total. Show the focused
     minutes next to each block.

## Rules

- Get screen content only through `timecapture-cli screenshot`. Do not read `capture.db`, the
  `captures` folder or the storage root directly, and do not try to decrypt or convert images
  yourself.
- Never try to operate the review window (no clicks, keystrokes or UI automation). The program
  ignores such input in any case.
