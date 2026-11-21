---
name: timesheet
description: Build timesheet entries for a day or a time range from what TimeCapture recorded. Use when asked for a timesheet, time entries, hours per project, or what was worked on at a given time.
---

# Timesheet from TimeCapture data

Everything you read from the capture data is sent off this machine. Read it only through the
export, which contains nothing but scrubbed window titles and scrubbed recognized text.

## Data access

- Read the data only with
  `dotnet run --project src/Capture.Cli -- export --day yyyy-MM-dd`
  (or `--from "yyyy-MM-dd HH:mm" --to "yyyy-MM-dd HH:mm"`). Times are local.
- Start with `--layer 1` (titles only). Use the default layer 2 (titles and recognized text) only
  for the visits that layer 1 cannot assign, with `--from`/`--to` narrowed to them.
- Never open `capture.db`, the `captures` folder or the storage root directly: not with Read,
  not with shell commands, not with sqlite.
- Screenshots are the last resort. If a visit stays ambiguous after layer 2, tell the user which
  visit and why, and ask before running
  `dotnet run --project src/Capture.Cli -- image <id>` (ids from the `images:` line). Delete the
  PNG after looking at it.

## Method

1. Export the range. Each line is a visit: a stretch during which one window had focus, or a
   break (`[idle]`, `[locked]`, `[asleep]`, `[recording paused]`, `[other application]`).
2. Assign each window visit to a project from the user's project list, to "not work", or to
   "unknown". Ask for the project list if you do not have it. Ask instead of guessing when a
   title and its text do not settle it.
3. Collect the visits per project.
4. Bridge gaps between visits of the same project when the gap is at most 10 minutes. Longer gaps
   split the block and are not booked. Idle, locked, asleep and paused stretches are never bridged.
5. Round each block's start and end to the nearest 15 minutes. Drop blocks that round to zero.
6. Output one entry per block: project, start, end, duration, and the focused minutes inside it,
   plus the total per project and overall.

The 10-minute bridge and the 15-minute grid are defaults; use other values when the user asks.
List the unknown visits with their times at the end, so the user can assign them.
