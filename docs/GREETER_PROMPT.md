# Prompt: build the TaskHub Greeter

> Paste everything below the line into a new session. Fill in the three **[choose]** items first
> (or delete them and let the assistant propose).

---

Build a small **Greeter** app for Windows 10/11 that welcomes me when I start or return to my PC and
shows what matters today, using data from **TaskHub** — my local to-do/reminder app that is already
running in the background. The Greeter must not store tasks itself; TaskHub is the single source of
truth, reached only through its local HTTP API described below.

## What the Greeter should do

1. **When it appears**: at sign-in, when the PC is unlocked, and when it resumes from sleep
   (Windows session/power events). Also on demand from a tray icon or global hotkey. Don't show it
   more than once per 30 minutes automatically.
2. **What it shows** (a small, good-looking window, centred, closes with Esc or after ~20 s idle):
   - A greeting by time of day ("Good morning, <name>") and today's date.
   - **Overdue** tasks (red), **today's** tasks (time-ordered), and the **next reminder** time.
   - A one-line summary: "3 due today · 1 overdue · next reminder 14:30".
   - Each task row: tick to complete, title, due time, priority marker, tags.
   - A quick-add box that accepts TaskHub's natural-language syntax
     (e.g. `Pay rent tomorrow 9am #home !high @Finance`).
   - An "Undo" for the last action.
3. **If TaskHub isn't running**: show the greeting with a friendly "TaskHub isn't running" line and a
   "Start TaskHub" button (runs the exe path from settings); never crash or hang.
4. Optionally (setting): instead of its own window, show the summary as a **TaskHub banner** via
   `POST /v1/notifications` (see below), with a "Show today" button.

**[choose] Language / UI stack:** e.g. C# WPF, C#/WinUI 3, Python (PySide6), Rust (Tauri), Electron.
**[choose] My name for the greeting:** ________
**[choose] Extras:** e.g. weather, calendar, quote of the day — or none.

## TaskHub local API (what exists today)

**Finding it.** TaskHub writes `%LOCALAPPDATA%\TaskHub\api.json` while it runs (deleted on exit):

```json
{ "port": 47821, "tokenFile": "C:\\Users\\me\\AppData\\Local\\TaskHub\\api.token", "processId": 1234, "version": "0.1.0" }
```

If the environment variable `TASKHUB_HOME` is set, `api.json` is at `%TASKHUB_HOME%\api.json` instead
(that's TaskHub's dev mode). Read the token from `tokenFile` (plain text, trim it). Re-read both on
401/connection errors — the port or token can change between TaskHub restarts.

**Security rules (requests are rejected otherwise):**
- Base URL `http://127.0.0.1:<port>` (loopback only, plain HTTP).
- Header `Authorization: Bearer <token>` on every request.
- `Host` must be `127.0.0.1:<port>` or `localhost:<port>` — normal HTTP clients do this automatically.
- **No `Origin` header** — requests that carry one get 403. So call the API from native code / a main
  process, **not** from browser JavaScript (e.g. in Electron/Tauri, make requests in the main/Rust
  side, not via `fetch` in the renderer/webview).
- Send `X-TaskHub-Client: greeter` so TaskHub labels the changes you make (`source: "api:greeter"`).

**Responses** are JSON, camelCase, null fields omitted. Errors: `{"error": "message"}` with
400 (bad input), 401 (token), 403 (Origin), 404 (not found), 409 (nothing to undo), 421 (bad Host).

**Task object** (`TaskDto`):

```json
{
  "id": "01a19d7f-318d-7041-9a9b-f8782e183a8f",
  "title": "Pay rent",
  "notes": "…",
  "status": "open",                 // or "completed"
  "priority": "high",               // none | low | medium | high | urgent
  "dueDate": "2026-11-08",          // local date
  "dueTime": "09:00",               // local HH:mm, absent for all-day
  "dueAtUtc": "2026-11-08T03:30:00+00:00",
  "listId": "…", "tags": ["home"],
  "recurrence": "FREQ=WEEKLY;BYDAY=MO", "recurrenceText": "Every Monday",
  "reminderOffsets": [0], "repeatReminderMinutes": 10,
  "nextReminderUtc": "2026-11-08T03:30:00+00:00",
  "createdUtc": "…", "updatedUtc": "…", "completedUtc": "…", "deletedUtc": "…"
}
```

**Endpoints:**

| Method | Path | Use in the Greeter |
|---|---|---|
| GET | `/v1/status` | Is TaskHub up? → `{version, processId, now, timeZone}` |
| GET | `/v1/tasks?view=today` | Today **including overdue**, already sorted (dated → timed → priority) |
| GET | `/v1/tasks?view=overdue` | Overdue only |
| GET | `/v1/tasks?view=upcoming` | Future-dated |
| | other params | `q=` text search, `tag=`, `list=<listId>`, `includeCompleted=true`, `limit=` |
| POST | `/v1/tasks` | Quick add: `{"text":"Call mom friday 6pm #family"}` → 201 + task |
| POST | `/v1/tasks/{id}/complete` | Tick a task. Repeating tasks move to their next date and stay open |
| POST | `/v1/tasks/{id}/reopen` | Untick |
| POST | `/v1/tasks/{id}/snooze` | `{"minutes":30}` or empty body for the user's default |
| PATCH | `/v1/tasks/{id}` | Merge-patch, e.g. `{"dueDate":"2026-11-09","dueTime":null}` |
| POST | `/v1/undo` | Undo the last change (any client's) → `{description}` or 409 |
| GET | `/v1/lists`, `/v1/tags` | Names/counts for display |
| POST | `/v1/notifications` | Show a TaskHub banner: `{"title":"Good morning!","body":"3 due today","priority":"normal","seconds":10,"buttons":[{"action":"greeter-open","label":"Show today"}]}` → 202 `{id}` |
| GET | `/v1/notifications/dnd` | `{"enabled":true}` — respect Do Not Disturb: don't pop up while it's on |

The "next reminder" for the summary = the smallest `nextReminderUtc` among today's/upcoming open tasks.

**Live updates:** TaskHub has an internal event bus (`task.created`, `task.completed`,
`reminder.due`, `notification.action`, …), but the **WebSocket for outside apps is not built yet**
(planned: `ws://127.0.0.1:<port>/v1/events?topics=task.*&access_token=…`, each message an envelope
`{id, type, time, source, data}`). For now: refresh when the window opens and after each action;
while visible, poll `/v1/tasks?view=today` every 30 s. Structure the code so a WebSocket listener
can replace polling later (one "TaskHubClient" with an `Updated` event).

## Requirements

- Single-instance, per-user, starts at sign-in (HKCU Run key), tray icon with Show / Settings / Quit.
- Settings in a small JSON/TOML file: name, auto-show triggers, min minutes between auto-shows,
  TaskHub exe path, "use TaskHub banner instead of window".
- Never blocks the UI on network calls; 3 s timeout per request; clear error states.
- Follow Windows light/dark theme; keyboard: ↑/↓ to move, Space to complete, Enter on quick add,
  Ctrl+Z undo, Esc close.
- A thin, testable `TaskHubClient` (discovery, auth, all calls above) with unit tests against a fake
  HTTP server; and a short README with build/run steps.
- Build in small steps and show me runnable code at the end of each step:
  1) `TaskHubClient` + tests, 2) window with today/overdue + complete/undo, 3) quick add,
  4) auto-show on sign-in/unlock/resume + tray + settings, 5) banner mode + DND, 6) polish.
