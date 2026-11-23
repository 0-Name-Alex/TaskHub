# TaskHub

Local-first to-do, reminder and laptop automation hub for Windows (.NET 10, WPF).
Architecture: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). Hands-on checks to do after a build: [docs/CHECKS.md](docs/CHECKS.md).

## Status

| Phase | State |
|---|---|
| 0 – App shell (host, single instance, tray, config, logging, event bus, autostart, hotkeys) | ✅ |
| 1 – Core + storage (tasks, SQLite, undo, search, import/export, local API slice, CLI, task UI) | ✅ |
| 2 – Reminders, recurrence, snooze, missed-reminder catch-up, daily backup | ✅ |
| 3 – Custom notification banners (acrylic, animations, stacking, theming, reply, DND, history, presets) | ✅ |
| 4 – Automation (rules, reminder actions, clipboard/screenshot capture, launcher, file watcher, system triggers) | ✅ |
| 5 – Live events over WebSocket, MCP server for AI assistants (`th mcp`) | ✅ |
| 6 – Phone reminders (Telegram/ntfy), calendar, local AI, voice; plugins later | next |

## Requirements

- Windows 10 1809+ or Windows 11
- .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`)

## Build, test, run

| Do this | How |
|---|---|
| Build + test | double-click `build.cmd` (log in `build-log.txt`) |
| Run a dev instance (separate data in `.devhome`) | double-click `run-dev.cmd` |
| Self-contained exe | `.\build.ps1 -Publish` → `artifacts\TaskHub\TaskHub.exe` + `th.exe` |
| After changing the data model | nothing — `build.cmd` adds a migration automatically (name from `src\TaskHub.Storage\next-migration.txt`); or `new-migration.cmd <Name>` |

## Using TaskHub

**Window** — sidebar (Inbox, Today, Upcoming, Overdue, All, Completed, Trash, your lists and tags),
quick-add box on top, task list in the middle. Click a task to open the editor on the right
(Esc or ✕ closes it and saves). Tasks are grouped into Overdue / Today / Tomorrow / Next 7 days /
Later / No date; the round check shows priority (blue medium, orange high, red urgent); hover a
row for *move to tomorrow* and *delete*. The Today heading shows how much of today is done.

**List colours and icons** — right-click a list in the sidebar (or click the icon next to a list's
heading) → *Colour* or *Icon*. The colour shows in the sidebar, on tasks in other views and in the
editor; the icon in the sidebar and the heading.

**Quick add** — type naturally, press Enter:

```
Pay rent tomorrow 9am #home !high @Finance
Call mom friday evening
Dentist 20 nov 3:30pm !urgent
Standup every weekday 9:30am
Water plants every 3 days
"Monday" meeting notes          ← quotes keep words literal
```

| Syntax | Meaning |
|---|---|
| `#tag` | tag |
| `@List` / `@Two_Words` | list (created if missing) |
| `!low` `!med` `!high` `!urgent`, `!1`–`!4`, `!!`, `!!!` | priority |
| today, tonight, tomorrow, mon…sun, next fri, weekend, next week, in 3 days, 20 nov, nov 20, 2026-11-20 | date |
| 9am, 9:30pm, 21:00, at 5, noon, morning, afternoon, evening, in 2 hours | time |
| daily, weekdays, weekly, monthly, yearly, every monday, every mon,wed,fri, every 2 weeks, every other month | repeat |

Adding while a list, tag or *Today* is selected puts the task there automatically.

**Reminders** — tasks with a due time remind at that time by default (tasks with only a date: at
09:00). In the editor: *Remind me* (at due … 1 day before), *Until done, remind* every N minutes,
and *Repeat* (friendly text or an iCalendar RRULE). Completing a repeating task moves it to its
next occurrence. Reminders missed while the laptop slept or TaskHub was closed show up when it
wakes/starts — more than 3 are grouped into one banner. Banner buttons: **Done**, **Snooze**
(10 min, configurable), **Open**.

**Notifications** — TaskHub draws its own notifications (not Windows toasts) on the monitor your mouse
is on, and they never steal keyboard focus. Two styles (`style` under `[notifications]`, switchable live):
- **`island`** (default): a black pill at the top of the screen. A notification appears as a small circle
  that grows into the card, then shrinks back and disappears — or the next one grows out of it. One at a
  time; waiting ones show as **+2** on the card. Cards wait while a full-screen app, game or presentation
  is running (urgent ones don't).
- **`banner`**: the classic frosted-glass cards that slide down and stack (the options below).

**Camera & microphone** — when an app starts using the camera or mic, the island briefly shows
*"Zoom is using your camera"* (green) or *…microphone* (orange) and goes away. `indicator_dot = true`
keeps a small dot while it stays in use; `camera_mic_indicator = false` turns it off. Using the laptop's
**camera switch or key** shows *Camera off* / *Camera on* (`camera_switch_indicator`; rules get
`camera.disabled` / `camera.enabled`). TaskHub reads the camera's own privacy state (what e.g. Lenovo's
overlay uses) and also notices a camera that disconnects; a plastic slide-over cover can't be detected. Rules can react too:
`camera.started`, `camera.stopped`, `microphone.started`, `microphone.stopped` (fields `app`, `appId`) —
e.g. `when = "camera.started"`, `then = ["dnd on"]`.

Banner style details:
- **Hover** pauses the countdown bar; **click** or **drag up** dismisses; buttons: Done / Snooze / Open.
- **Reply box** on reminders: type `snooze 10m`, `1h`, `done`, or a new time like `tomorrow 9am`, then Enter.
- **Priorities:** high = orange accent, urgent = red accent + stronger bounce (and bypasses Do Not Disturb).
- **Stack** (up to 3 at once) or **queue** (one at a time); newest on top.
- **Do Not Disturb** (tray menu, Notifications tab, optional hotkey `toggle_dnd`): hold banners until
  it ends (grouped if many) or silence them.
- **History**: the *Notifications* tab lists everything shown, with what you did. It also has buttons to
  try each style.
- **Animation presets**: `spring` (default), `slide`, `drop`, `fade`. Light/dark and accent colour
  follow Windows live. Soft chime (can be turned off).
- Settings: `[notifications]` in the config file. On Windows 10 / early Windows 11 the
  background is solid instead of acrylic.

**Maintenance** — a database backup is written daily to `backups\taskhub-YYYY-MM-DD.db`
(14 kept); trashed tasks are deleted for good after 30 days. Both configurable in `[maintenance]`.

**Keyboard** — `Ctrl+N` quick add · `Ctrl+F` search · `Space` complete · `Delete` trash ·
`Ctrl+Z` undo · `Ctrl+S` save editor · `Esc` back to the list.
Global: `Ctrl+Alt+T` show/hide · `Ctrl+Alt+N` quick add from anywhere · `Ctrl+Alt+Space` quick launcher ·
`Ctrl+Alt+V` clipboard → task · `Ctrl+Alt+S` screenshot → task. Change them under `[hotkeys]`.

**Quick launcher** (`Ctrl+Alt+Space`) — type to find Start-menu apps, your open tasks, rules, `[[shortcuts]]`
and commands (screenshot, clipboard, Do Not Disturb, today, config …). `Enter` runs the top hit; a task
opens in the window, `Ctrl+Enter` completes it. Anything else you type can be added as a task
(quick-add syntax works). Things you pick often move up.

**Capture** — `Ctrl+Alt+V` turns the clipboard into a task: text → title (first line) + notes; an image →
saved under `attachments\` and attached; copied files → attached where they are. `Ctrl+Alt+S` freezes the
screen; drag a rectangle (Esc cancels) and it becomes a task with the screenshot attached. A banner
confirms it with **Open** to edit. `capture_list` under `[automation]` sends captures to a list.

**Reminder actions** — in the editor, *When reminded, also* takes one action per line, run with the
task's first reminder (not again on snooze/nag, not for reminders missed > 15 min ago):

```
open https://meet.example.com/standup
notify Bring the slides | {task.title}
copy {task.notes}
```

A task may only open web/mail links unless `allow_task_programs = true` under `[automation]` — tasks can
come from the API and imports, so starting programs from them is opt-in. Rules can always do it.

## Automation

Rules live in the config file (saved = applied) and show up in the **Automation** tab with their last run,
problems, and a **Run now** button.

```toml
[[rules]]
name = "Stand-up link"
when = "reminder.due"                         # event pattern(s): "task.*", ["task.created", "task.updated"]
if   = "task.tags contains 'standup'"         # optional
then = ["open https://meet.example.com/standup", "dnd on"]

[[rules]]
name = "Invoices"
when = "file.detected"
if   = "watch == 'downloads' and extension == '.pdf' and name contains 'invoice'"
then = ["add Pay {name} tomorrow #finance", "notify New invoice | {name}"]
cooldown = 10                                 # seconds between runs (optional)

[[rules]]
name = "Focus"
hotkey = "Ctrl+Alt+F"                         # instead of / as well as 'when'
then = ["dnd toggle", { action = "run", program = "pwsh", args = ["-File", "C:\\scripts\\focus.ps1"], wait = true }]

[[rules]]
name = "Low battery"
when = "system.battery.low"
then = ["notify Battery at {percent}% | Plug in soon", { action = "notify", title = "Save your work", priority = "urgent" }]

[[watch]]
name = "downloads"
path = "~/Downloads"
filter = ["*.pdf", "*.docx"]                  # optional; also subfolders = true, events = ["created", "changed", "deleted", "renamed"]

[[shortcuts]]                                 # extra launcher entries
name = "Team wiki"
target = "https://wiki.example.com"
keywords = "docs notes"
```

**Actions** (as a line, or a table with `action = "…"` and named values):

| Line | Table keys | Does |
|---|---|---|
| `open <url\|file\|folder\|app> [args]` | `target`, `args`, `working_dir` | Opens it like a double-click |
| `run <program> [args…]` | `program`, `args`, `wait`, `timeout`, `hidden`, `working_dir` — or `shell` (pwsh, powershell, cmd, python, node) + `script` | Starts a program directly (never through a shell) |
| `notify <title> [\| body]` | `title`, `body`, `priority`, `seconds`, `icon` | Shows a banner |
| `add <quick-add text>` | `text`, `notes` | Adds a task |
| `done` · `snooze [min]` | `id` (optional) | Completes / snoozes the task the event is about |
| `dnd on\|off\|toggle` | `state` | Do Not Disturb |
| `copy <text>` | `text` | Puts text on the clipboard |
| `wait <seconds>` · `log <text>` | | Pause (max 300 s) · write to the log |
| `screenshot` · `clip` | | Same as the capture hotkeys |

`{placeholders}` are filled in from the event (`{task.title}`, `{path}`, `{percent}`, `{now}`, `{today}`, `{weekday}`).
In `run`, they work in `args` (each stays one argument); scripts get the event as environment variables
instead (`TASKHUB_TASK_TITLE`, `TASKHUB_PATH`, `TASKHUB_EVENT_JSON` …), so odd file names can't inject commands.

**Conditions:** `== != < <= > >=`, `contains`, `startswith`, `endswith`, `matches '<regex>'`, `in ['a', 'b']`,
`and`, `or`, `not`, parentheses. Text comparisons ignore case; a field on its own is true when it has a value.

**Events rules can use** (fields are available to `if` and `{placeholders}`):

| Event | Fields |
|---|---|
| `reminder.due` | `task.*` (title, notes, priority, tags, dueDate, dueTime, id …), `missed` |
| `task.created` `task.updated` `task.completed` `task.reopened` `task.deleted` `task.restored` | `task.*` |
| `file.detected` | `watch`, `change`, `path`, `name`, `extension`, `folder`, `size`, `oldPath` |
| `system.idle` / `system.active` | `idleMinutes` (after `idle_minutes` without input) |
| `system.battery.low` · `system.power.ac` · `system.power.battery` | `percent`, `onBattery` |
| `system.disk.low` | `drive`, `freeGb`, `totalGb` |
| `system.locked` · `system.unlocked` · `system.resumed` · `system.suspended` | — |
| `camera.started` · `camera.stopped` · `microphone.started` · `microphone.stopped` | `app`, `appId`, `device` |
| `app.started` · `hotkey.pressed` · `clipboard.captured` · `screenshot.captured` · `notification.action` | see the Diagnostics tab |

Every event also has `event.type`, `event.source`, `now`, `today`, `time`, `weekday` and `hour`.
Rules never react to what rules cause (no loops), a rule doesn't start again while it's running, and
`max_runs_per_minute` caps everything. The Diagnostics tab shows every event live with its data.

## CLI (`th`)

`th.exe` is built to `src\TaskHub.Cli\bin\Debug\net10.0\` (or `artifacts\TaskHub` when published).
TaskHub must be running.

```
th add "Pay rent tomorrow 9am #home !high @Finance"
th ls                      th ls -v today        th ls -t home -l Finance -s rent
th done 3f2a91c0           th edit 3f2a91c0 --due 2026-11-20 --time none -p urgent
th rm 3f2a91c0             th restore 3f2a91c0   th undo
th lists                   th lists add Side Project
th tags                    th export backup.json th import backup.json
th status                  th ls --json
th add "Standup 9:30am" --repeat "every weekday" --remind 0,10 --on-remind "open https://meet.example.com/standup"
th snooze 3f2a91c0 30      th edit 3f2a91c0 --repeat none --nag 15
th notify "Backup done" --body "42 GB" -p high --button view=Open
th dnd on                  th dnd off            th dnd
th mcp                     th mcp --read-only    (MCP server for AI assistants, see below)
```

> **PowerShell:** quote quick-add text. Unquoted, `#` starts a comment and `@` means splatting.

## Local API (for other apps)

`http://127.0.0.1:47821/v1/…`. It only listens on loopback. Every request needs
`Authorization: Bearer <token>`. To find the port and token, read
`%LOCALAPPDATA%\TaskHub\api.json`, which looks like `{ "port": …, "tokenFile": "…" }`. Requests from browsers
(any `Origin` header) and requests with a non-local `Host` are rejected. Optional:
`X-TaskHub-Client: greeter` marks the events your app causes as `source: "api:greeter"`.

| Method | Path | |
|---|---|---|
| GET | `/v1/status` | version, pid, time zone |
| GET | `/v1/tasks?view=&q=&tag=&list=&includeCompleted=` | views: inbox, today, upcoming, overdue, all, completed, trash |
| GET | `/v1/tasks/{id}` | |
| POST | `/v1/tasks` | `{"text":"quick add…"}` or `{"title":…, "dueDate":"2026-11-20", "dueTime":"09:30", "priority":"high", "tags":[…], "listId":…, "recurrence":"every monday", "reminderOffsets":[0,10], "repeatReminderMinutes":15, "reminderActions":"open https://…"}` |
| PATCH | `/v1/tasks/{id}` | merge-patch: present = set, `null` = clear |
| POST | `/v1/tasks/{id}/complete` · `/reopen` · `/restore` | completing a repeating task advances it |
| POST | `/v1/tasks/{id}/snooze` | `{"minutes":30}` (optional) |
| DELETE | `/v1/tasks/{id}` | moves to trash |
| POST | `/v1/undo` | 409 when nothing to undo |
| GET/POST | `/v1/lists` | `{"name":"Garden","color":"#30A46C"}` |
| PATCH | `/v1/lists/{id}` | any of `{"name":…, "color":"#RRGGBB", "icon":"home"}` (`""` clears) |
| GET | `/v1/tags` | |
| GET / POST | `/v1/export` · `/v1/import` | JSON document |
| POST | `/v1/notifications` | show a banner: `{"title":…, "body":…, "priority":"high", "seconds":8, "buttons":[{"action":"ok","label":"OK"}], "icon":"🎉", "sound":true}` → `{"id":…}`; clicks arrive as `notification.action` events |
| GET / PUT | `/v1/notifications/dnd` | `{"enabled":true}` |
| GET | `/v1/notifications/history?limit=50` | |

PowerShell example:

```powershell
$d = Get-Content "$env:LOCALAPPDATA\TaskHub\api.json" | ConvertFrom-Json
$h = @{ Authorization = "Bearer $(Get-Content $d.tokenFile)" }
Invoke-RestMethod "http://127.0.0.1:$($d.port)/v1/tasks?view=today" -Headers $h
Invoke-RestMethod "http://127.0.0.1:$($d.port)/v1/tasks" -Method Post -Headers $h `
  -ContentType 'application/json' -Body '{"text":"Buy milk tomorrow #errands"}'
```

## Live events (WebSocket)

Other apps and scripts can watch TaskHub as things happen instead of polling. Connect to
`ws://127.0.0.1:<port>/v1/events?topics=task.*,reminder.due` (omit `topics` for everything) with the
same `Authorization: Bearer <token>` header as the REST API. Each message is one JSON event:

```json
{ "id": "0192…", "type": "task.created", "time": "2026-11-03T05:30:00Z", "source": "ui", "data": { "task": { … } } }
```

The first message is `stream.ready`. Topics are event types (`task.completed`), prefixes (`task.*`,
`system.*`) or `*` — the same names rules use (see *Automation*); the Diagnostics tab shows them live.
`source` tells you who caused it (`ui`, `api:<client>`, `rule:<name>` …) so a client can skip its own
echoes. Browsers can't connect (Origin check). Ready-made examples: `samples\events.py` (`pip install
websockets`) and `samples\events.ps1` (PowerShell 7).

## Use TaskHub from Claude (MCP)

`th mcp` is a [Model Context Protocol](https://modelcontextprotocol.io) server, so an AI assistant can
work with your real tasks: *"what's due today?"*, *"remind me to call the bank Friday 10am"*,
*"I finished the report — and move the dentist to next week"*. Everything goes through the running
TaskHub, with the usual undo and notifications (events show `source: "api:mcp"`).

**Claude Desktop:** Settings → Developer → Edit Config, add TaskHub to `claude_desktop_config.json`, then
restart Claude Desktop:

```json
{
  "mcpServers": {
    "taskhub": {
      "command": "D:\\Projects\\TaskHub\\artifacts\\TaskHub\\th.exe",
      "args": ["mcp"]
    }
  }
}
```

Use the path to your `th.exe` (`artifacts\TaskHub` after `.\build.ps1 -Publish`, or
`src\TaskHub.Cli\bin\Debug\net10.0\th.exe`). Add `"--read-only"` to `args` to let the assistant look
but not change anything. For the dev instance add `"env": { "TASKHUB_HOME": "D:\\Projects\\TaskHub\\.devhome" }`.
TaskHub must be running; if it isn't, the assistant is told so.

| Tool | Does |
|---|---|
| `get_status` | Today's date, local time, time zone |
| `list_tasks` | today / upcoming / overdue / inbox / all / completed / trash, with search, tag, list filters |
| `get_task` · `list_lists` · `list_tags` | Details, lists, tags |
| `add_task` | Quick-add text plus optional due date/time, priority, tags, list, repeat, reminders |
| `update_task` | Change any field (`"none"` clears) |
| `complete_task` · `reopen_task` · `delete_task` · `restore_task` · `snooze_task` · `undo` | As in the app |
| `notify` · `set_do_not_disturb` | Banner on your screen · Do Not Disturb |

## Files

| What | Where |
|---|---|
| Config (hot-reloaded) | `%APPDATA%\TaskHub\config.toml` |
| Database (SQLite, WAL) | `%LOCALAPPDATA%\TaskHub\data\taskhub.db` |
| Daily + pre-upgrade DB backups | `%LOCALAPPDATA%\TaskHub\backups\` |
| Logs | `%LOCALAPPDATA%\TaskHub\logs\` |
| API discovery / token | `%LOCALAPPDATA%\TaskHub\api.json`, `api.token` |
| Screenshots / clipboard images | `%LOCALAPPDATA%\TaskHub\attachments\YYYY-MM\` |
| Launcher "frequently used" | `%LOCALAPPDATA%\TaskHub\data\launcher.json` |

Set `TASKHUB_HOME=D:\somewhere` to run a separate instance with its own config, data and
autostart entry (that's what `run-dev.cmd` does). The CLI follows `TASKHUB_HOME` too.
