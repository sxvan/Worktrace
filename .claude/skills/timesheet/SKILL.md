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

1. **Read the day.**
   - `timecapture-cli export --day yyyy-MM-dd` (default: today): the visits (focused window and
     title, or idle, locked, asleep, paused) and the screenshot ids per visit (`shots: <id>@HH:mm`).
   - `timecapture-cli sessions --day yyyy-MM-dd`: the day's Claude Code sessions with folder,
     branch, active spans, prompts, and the transcript file of each.
   - `git log` in the repos named there can add what was committed.

2. **Assign the visits to projects** from window titles, session folders, branches and prompts.
   If a session's prompts do not say enough, read that one session's transcript (the
   `transcript:` path), not all of them: they are large. Do not decide what counts as work and
   drop the rest: every activity of the day goes into the report (step 4). The user decides.

3. **Ask for a screenshot only when that data cannot settle it**, for example a browser title that
   names no project, or a long stretch in one generic window. Take an id from `shots`, or a time:

   ```
   timecapture-cli screenshot <id>
   timecapture-cli screenshot --at "yyyy-MM-dd HH:mm"
   ```

   Run it with a Bash timeout of 600000 ms: it opens a window in which the user reviews the
   recognized text and the redacted image, and waits for them. Tell the user beforehand which
   screenshot you are asking for and why. If they share it, the output gives the path of the
   redacted PNG (read it) and the text with positions. Exit code 3 means they declined that one
   screenshot: do not ask for it again. You may ask for one from a different moment (another id
   or time) if it would settle the question, or ask the user directly.

   Ask for as few screenshots as possible, one at a time.

4. **Build the entries** (defaults the user has used, unless they say otherwise):
   - A visit lasts until the next event; idle, lock, sleep and pause end it.
   - First group the visits and session spans by project into blocks, bridging gaps of at most
     10 minutes (never across idle or lock). Then round each block's start and end to the
     nearest 15 minutes; a block is at least 15 minutes long.
   - After rounding, merge consecutive blocks of the same project whose rounded times touch (one
     ends where the next starts) into one block.
   - Report every block of the day in one table, in time order, including games, video, private
     browsing and tool setup. Columns: project, start, end, duration, focused minutes, source
     (visits, sessions, screenshot), and the entry text.
   - Mark blocks that are possibly not work with "⚠ evtl. nicht Arbeit" and a few words why
     (e.g. game, YouTube). Do not leave them out and do not count them in the work total.
   - End the table with summary rows, computed from the rounded entries in the table (not from
     the raw visits): "Total Arbeit" (all unmarked blocks; where blocks overlap, count the time
     once, as you would book it) and "Total evtl. nicht Arbeit" (the marked blocks), each with
     its duration in the Dauer column.
   - Overlapping blocks (parallel sessions in two projects) are reported as they are. Mark each
     of them in the project column with "↔ überschneidet <other project>" (no other symbols, no
     legend needed), and say below the table which one you would book.
   - The entry text is in Swiss Standard German (de-CH: "ss", never "ß"), short and concrete,
     what was done, not how: a few words to one short sentence, no filler. For example:
     "CI-Gate für PRs eingerichtet, release-please aufgesetzt" or "Glove: Umbenennung geprüft".

## Rules

- Get screen content only through `timecapture-cli screenshot`. Do not read `capture.db`, the
  `captures` folder or the storage root directly, and do not try to decrypt or convert images
  yourself.
- Never try to operate the review window (no clicks, keystrokes or UI automation). The program
  ignores such input in any case.
