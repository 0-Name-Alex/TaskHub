# TaskHub — Architecture & Solution Structure (Step 0: proposal)

> Working name **TaskHub** (rename freely — it's only the namespace/exe prefix).
> Target: Windows 10/11, .NET 10 LTS, C# 14.

---

## 1. Big picture

One per-user process (`TaskHub.exe`) runs the .NET Generic Host. Everything —
UI, scheduler, automation, API, plugins — is a service inside that host and
talks through **one event bus** and **one set of application services**.

```
                         ┌───────────────────────── TaskHub.exe (per user, single instance) ─────────────────────────┐
                         │                                                                                            │
  Surfaces (WPF, STA)    │  MainWindow   NotificationBanner   QuickLauncher   TrayIcon                                │
                         │       │               │                 │             │                                    │
                         │       └──────── ViewModels (CommunityToolkit.Mvvm) ────┘                                    │
                         │                         │                                                                  │
  Application services   │   TaskService · ReminderService · SearchService · UndoService · NotificationService       │
                         │                         │                                                                  │
  Core engine            │   Scheduler (PriorityQueue)   RuleEngine   ◄──────►   EVENT BUS   ◄──────►  PluginHost       │
                         │                         │                    ▲   ▲                          │              │
  Infrastructure         │   Storage (EF Core/SQLite WAL)   Platform.Windows (hotkeys, power, idle, DWM)  │              │
                         │                                              │   │                          │              │
  Edges                  │   Local API (Kestrel 127.0.0.1: REST + WebSocket)   MCP server (stdio/HTTP)  │              │
                         └──────────────────────────────┬───────────────────────────────────────────────┼──────────────┘
                                                        │ token + Host/Origin checks                    │ AssemblyLoadContext
                   ┌────────────────────────────────────┼──────────────────────┐                        │
                   │                                    │                      │                 in-process .NET plugins
             TaskHub.Cli (exe)              Greeter app / scripts      out-of-process plugins
                                            (any language)             (Python, Node …)
```

**Rules of the road**

1. **Core has no Windows, WPF, ASP.NET or EF dependencies.** It's plain `net10.0`, so the
   domain, scheduler, recurrence and rule engine are fully unit-testable anywhere.
2. **Every state change goes through an application service**, which persists, then publishes
   an event. UI, API, CLI, rules and plugins never write to the DB directly. That's what
   keeps undo, events and the API consistent.
3. **Surfaces are dumb.** Main window, banner, launcher, CLI, REST and MCP are all just
   front-ends over the same services.
4. **Windows-specific code lives in one project** (`Platform.Windows`) behind interfaces
   defined in Core (`IHotkeyService`, `IPowerEvents`, `IIdleMonitor`, `ISystemInfo`).

---

## 2. Solution / folder structure

```
TaskHub/
├─ TaskHub.slnx                         # new XML solution format (.NET 9+ SDK)
├─ Directory.Build.props                # LangVersion, Nullable, TreatWarningsAsErrors, analyzers
├─ Directory.Packages.props             # Central Package Management — all versions in one place
├─ global.json                          # pins .NET 10 SDK
├─ .editorconfig
├─ docs/
│   ├─ ARCHITECTURE.md                  # this file
│   ├─ api.md                           # REST/WS reference (generated OpenAPI + examples)
│   ├─ events.md                        # event catalogue + JSON schemas
│   └─ plugins.md                       # how to write in-proc / out-of-proc plugins
├─ src/
│   ├─ TaskHub.Abstractions/            # net10.0 — the PUBLIC SDK (versioned, minimal deps)
│   │   ├─ Events/                      #   EventEnvelope, well-known event type names
│   │   ├─ Plugins/                     #   ITaskHubPlugin, ITrigger, IAction, PluginContext
│   │   ├─ Ai/                          #   IAiProvider
│   │   └─ Contracts/                   #   DTOs shared by Api, Cli, MCP, plugins (TaskDto, …)
│   │
│   ├─ TaskHub.Core/                    # net10.0 — domain + engine, no infra
│   │   ├─ Domain/                      #   TaskItem, TaskList, Tag, Reminder, Recurrence, Rule …
│   │   ├─ Services/                    #   TaskService, ReminderService, SearchService, UndoService
│   │   ├─ Events/                      #   IEventBus + Channel-based InMemoryEventBus
│   │   ├─ Scheduling/                  #   Scheduler, IClock, RecurrenceCalculator (Ical.Net)
│   │   ├─ Rules/                       #   RuleEngine, rule parser ("when … then …")
│   │   ├─ Parsing/                     #   quick-add parser: "pay rent tomorrow 9am #home !high"
│   │   └─ Abstractions/                #   IHotkeyService, IPowerEvents, INotificationPresenter, IRepository …
│   │
│   ├─ TaskHub.Storage/                 # net10.0 — EF Core + SQLite
│   │   ├─ TaskHubDbContext.cs
│   │   ├─ Configurations/              #   IEntityTypeConfiguration per entity
│   │   ├─ Migrations/
│   │   ├─ Repositories/
│   │   ├─ Backup/                      #   daily VACUUM INTO backup + retention
│   │   └─ ImportExport/                #   JSON import/export (System.Text.Json source-gen)
│   │
│   ├─ TaskHub.Platform.Windows/        # net10.0-windows — ALL Win32 interop (CsWin32)
│   │   ├─ NativeMethods.txt            #   RegisterHotKey, DwmSetWindowAttribute, GetLastInputInfo …
│   │   ├─ Hotkeys/  Power/  Idle/  SystemInfo/  Autostart/  SingleInstance/  Windowing/
│   │
│   ├─ TaskHub.Notifications/           # net10.0-windows, WPF — the custom banner system
│   │   ├─ BannerWindow.xaml            #   WindowStyle=None + WindowChrome, DWM backdrop
│   │   ├─ BannerManager.cs             #   placement, stacking/queue, DND, history
│   │   ├─ Animations/                  #   presets: spring, slide, drop, fade
│   │   └─ Theming/                     #   live light/dark + accent tracking
│   │
│   ├─ TaskHub.Automation/              # net10.0-windows — triggers & actions that touch the OS
│   │   ├─ Actions/                     #   OpenApp/File/Url, RunScript (pwsh, python, exe)
│   │   ├─ Triggers/                    #   FileWatcher, LowBattery, LowDisk, Idle, Resume
│   │   └─ Capture/                     #   clipboard-to-task, region screenshot-to-task
│   │
│   ├─ TaskHub.Api/                     # net10.0 class lib (FrameworkReference AspNetCore)
│   │   ├─ Endpoints/                   #   /v1/tasks, /v1/lists, /v1/notifications, /v1/events (WS)
│   │   ├─ Security/                    #   token auth, Host + Origin guard middleware
│   │   └─ Mcp/                         #   MCP server (ModelContextProtocol C# SDK) tools
│   │
│   ├─ TaskHub.Plugins/                 # net10.0 — PluginHost, AssemblyLoadContext loader,
│   │                                   #   manifest parsing, out-of-proc plugin registry
│   │
│   ├─ TaskHub.Integrations/            # (Phase 6) one folder/project per integration,
│   │   ├─ TaskHub.Integrations.Telegram/   each built as a plugin against Abstractions only
│   │   ├─ TaskHub.Integrations.Ntfy/
│   │   └─ …
│   │
│   ├─ TaskHub.Ai/                      # (Phase 6) Ollama provider (default), opt-in cloud
│   │                                   #   providers, Whisper.net voice input
│   │
│   ├─ TaskHub.App/                     # net10.0-windows WPF EXE — composition root
│   │   ├─ Program.cs                   #   [STAThread] Main: single-instance → build Host → run WPF
│   │   ├─ App.xaml                     #   ThemeMode="System" (Fluent)
│   │   ├─ Hosting/                     #   service registration, UI dispatcher bridge
│   │   ├─ Tray/                        #   H.NotifyIcon.Wpf
│   │   ├─ Views/  ViewModels/
│   │   └─ QuickLauncher/
│   │
│   └─ TaskHub.Cli/                     # net10.0 EXE `th` — System.CommandLine → local API
│
└─ tests/
    ├─ TaskHub.Core.Tests/              # domain, scheduler (fake clock), recurrence/DST, rules, parser
    ├─ TaskHub.Storage.Tests/           # EF + SQLite (file + in-memory), migrations, import/export
    ├─ TaskHub.Api.Tests/               # WebApplicationFactory: auth, Host/Origin guard, endpoints, WS
    ├─ TaskHub.Cli.Tests/               # command parsing + output against a fake API
    └─ TaskHub.Windows.Tests/           # Windows-only (skipped elsewhere): hotkeys, DWM, autostart
```

**Dependency direction** (arrows = "references"):

```
Abstractions ◄── Core ◄── Storage
                  ▲ ▲ ▲
                  │ │ └── Platform.Windows ◄── Notifications, Automation
                  │ └──── Api, Plugins, Ai, Integrations.*
                  └────── App  (references everything; the only place DI is wired)
Cli ──► Abstractions only (talks HTTP to Api)
Integrations.* ──► Abstractions only (so they can also run out-of-process later)
```

---

## 3. Process & hosting model

- **Startup (`Program.Main`, `[STAThread]`)**
  1. Single-instance guard: named `Mutex` `Local\TaskHub.{userSid}`. If it's already held, pass
     args (e.g. `--show`, `--quick-add "…"`) over a **named pipe** to the running instance and exit.
  2. Load TOML config → configure Serilog.
  3. Build `Host` (`Host.CreateApplicationBuilder`), register all services, and `await host.StartAsync()`.
     Hosted services (scheduler, API/Kestrel, file watchers, plugins, backup) run on the thread pool.
  4. Create the WPF `Application` on the STA main thread and `app.Run()`. The tray icon is the root
     surface, and the main window is created lazily (hidden at login).
  5. On exit: `host.StopAsync()` with a timeout, flush logs, release the mutex.
- **UI threading:** `IUiDispatcher` (implemented in App over `Dispatcher`) is the only way
  background services touch WPF. The event bus never invokes UI handlers on the publisher's thread.
- **Autostart:** `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\TaskHub = "<exe>" --background`,
  toggled from settings. When packaged as MSIX, it uses the `StartupTask` extension instead.

## 4. File locations

| What | Where |
|---|---|
| Config (user-editable, hot-reloaded) | `%APPDATA%\TaskHub\config.toml` |
| Database | `%LOCALAPPDATA%\TaskHub\data\taskhub.db` (WAL) |
| Daily backups (keep N) | `%LOCALAPPDATA%\TaskHub\backups\taskhub-YYYY-MM-DD.db` |
| Attachments (screenshots) | `%LOCALAPPDATA%\TaskHub\attachments\` |
| Logs (rolling, 14 days) | `%LOCALAPPDATA%\TaskHub\logs\` |
| In-proc plugins | `%LOCALAPPDATA%\TaskHub\plugins\<id>\plugin.json + dlls` |
| API discovery file | `%LOCALAPPDATA%\TaskHub\api.json` → `{ "port": 47821, "tokenFile": "…" }` |
| API token | `%LOCALAPPDATA%\TaskHub\api.token` (ACL: current user only) |

Other apps (the greeter, scripts, CLI) find the API by reading `api.json` and the token file.

---

## 5. Domain model (v1)

| Entity | Key fields |
|---|---|
| `TaskItem` | `Id` (Guid v7), `Title`, `Notes`, `Status` (Open/Done/Cancelled), `Priority` (None/Low/Med/High/Urgent), `Due` (see time note), `ListId`, `ParentId` (subtasks), `Tags`, `Recurrence?`, `SortOrder`, `CreatedUtc`, `UpdatedUtc`, `CompletedUtc?`, `DeletedUtc?` (soft delete → undo/trash) |
| `TaskList` | `Id`, `Name`, `Color`, `Icon`, `SortOrder`, `IsArchived` |
| `Tag` | `Id`, `Name` (unique, case-insensitive), `Color` |
| `Reminder` | `Id`, `TaskId`, `Kind` (Absolute / RelativeToDue), `Offset?`, `NextFireUtc`, `LastFiredUtc?`, `SnoozedUntilUtc?`, `RepeatEvery?` (nag until done), `Actions` (JSON list for Phase 4) |
| `Recurrence` | `RRule` (iCal string), `TimeZoneId`, `Mode` (FixedSchedule / AfterCompletion) |
| `Attachment` | `Id`, `TaskId`, `Kind` (Screenshot/File/Link), `Path`/`Uri` |
| `NotificationRecord` | history: id, source, title, body, shown/acted times, action taken |
| `Rule` | `Id`, `Name`, `Enabled`, `Trigger`, `Condition?`, `Actions` |
| `PluginState` | `PluginId`, `Enabled`, `SettingsJson` |

**Time handling (one important refinement to "store everything in UTC").** Instants
(`CreatedUtc`, `NextFireUtc`, `CompletedUtc`) are stored in UTC. **Recurrences, however, are
expanded in the user's local time zone** (`TimeZoneId` stored alongside the RRULE) and then
converted to UTC. If we expanded "every day at 9:00" in UTC, the reminder would fire at 10:00
after a DST change. Due dates also need `DueHasTime`, since "due Friday" isn't "due Friday 00:00".

**Undo:** each mutating service call returns an `IUndoableChange` (the inverse operation) that
`UndoService` pushes onto a bounded stack. Delete is a soft delete, so undo is cheap. A purge job
empties the trash after 30 days.

---

## 6. Event bus

- In-process, built on `System.Threading.Channels`. Publishers never block, and each subscriber
  gets its own bounded channel and consumer loop, so a slow plugin can't stall the scheduler.
- Every event uses the same envelope, which is also the exact JSON sent over WebSocket:

```json
{ "id": "0192…", "type": "task.completed", "time": "2026-11-02T16:00:00Z",
  "source": "ui" | "api:greeter" | "plugin:telegram" | "rule:42", "data": { … } }
```

- Topic wildcards: `task.*`, `reminder.*`, `*`.
- v1 catalogue: `task.created|updated|completed|reopened|deleted|restored`,
  `reminder.due|missed|snoozed|dismissed`, `notification.shown|action|dismissed`,
  `system.resumed|suspended|idle|active|battery.low|disk.low`, `file.detected`,
  `clipboard.captured`, `hotkey.pressed`, `app.started|stopping`, `plugin.loaded|failed`.
- `source` lets handlers ignore their own echoes (e.g. a sync plugin doesn't re-sync its own change).

## 7. Scheduler & reminders

- **Custom scheduler (no Quartz).** Our needs are narrow and DB-backed, and Quartz's own
  persistence model would duplicate our tables.
- A single `BackgroundService` keeps a `PriorityQueue<ReminderId, DateTimeOffset>` loaded from
  `Reminder.NextFireUtc`. It waits on whichever comes first: the next due time,
  a "replan" signal (a reminder was added/changed), or a 30 s `PeriodicTimer` heartbeat. The heartbeat
  catches clock changes and sleep that `Task.Delay` can't see.
- When a reminder fires, the scheduler publishes `reminder.due`, then computes the next occurrence
  (recurrence or nag interval) and persists it in the same transaction.
- **Missed catch-up:** on startup and on `system.resumed`, every reminder with
  `NextFireUtc <= now` fires as `reminder.missed`. If more than 3 are missed, they're grouped
  into one summary banner instead of a flood.
- `IClock` is injected everywhere, so tests can jump time, including across DST.

## 8. Local API (Phase 5, but its shape is fixed now)

- Kestrel bound to **`127.0.0.1` only** (never `0.0.0.0`). The port comes from config (default 47821).
- **Auth:** `Authorization: Bearer <token>`, with a 256-bit random token generated on first run.
  It's stored in the user-only token file and can be rotated from settings.
- **Host guard:** reject unless `Host` is `127.0.0.1:<port>` or `localhost:<port>`. This blocks DNS rebinding.
- **Origin guard:** reject any request that carries an `Origin` header not on an explicit
  allowlist (empty by default). This blocks browser pages, including the WebSocket upgrade.
- REST `/v1/...` with OpenAPI. Live events over a **plain WebSocket** at `/v1/events?topics=task.*`.
  I recommend raw WebSockets over SignalR, because every language has a WebSocket client and SignalR
  needs its own protocol client. The greeter app can be written in anything.
- Token **scopes** (`tasks:read`, `tasks:write`, `notify`, `events`, `rules:write`). Rules that run
  scripts can't be created over the API unless the config explicitly allows it.

## 9. Plugins (Phase 5) & rules (Phase 4)

- `ITaskHubPlugin { Id; Version; ConfigureAsync(PluginContext) }`. Through the context, a plugin
  registers triggers, actions and a settings schema, and gets the event bus, task services, an HTTP
  client and secret storage (DPAPI).
- **In-process:** each plugin gets a collectible `AssemblyLoadContext`. `TaskHub.Abstractions` is
  always resolved from the default context, so types match. Plugins are discovered via `plugin.json`
  (id, version, entry assembly, minimum host version).
- **Out-of-process:** the plugin connects to the API, calls `POST /v1/plugins/register`
  to declare its triggers and actions, and receives action invocations over its WebSocket.
- **Rules** in `config.toml` (and editable in the UI):

```toml
[[rules]]
name = "Standup prep"
when = "reminder.due"
if   = "task.tags contains 'standup'"
then = [ { action = "open.url", url = "https://meet.example.com/standup" } ]
```

**Phase 4 as built**

- The rule engine, condition language, action dispatcher, capture logic, folder watcher, system
  monitor and launcher ranking all live in `TaskHub.Core` (`Automation/`, `Launcher/`) and are unit-tested
  there. `TaskHub.Automation` (net10.0-windows, WinForms) only supplies the Windows pieces behind small
  interfaces: `IShellLauncher` (ShellExecute / Process with an argument list), `IClipboardService`,
  `IScreenCapture` (frozen-desktop overlay, physical pixels) and `IAppCatalog` (Start-menu shortcuts).
  Idle/battery/disk readings (`ISystemStatus`) are in `Platform.Windows`.
- `[[rules]]`, `[[watch]]` and `[[shortcuts]]` are parsed by hand from the TOML tree; a bad entry gets an
  error message in the Automation tab instead of rejecting the whole file.
- Loop safety: everything a rule causes carries `source = "rule:<name>"`, and rules ignore such events.
  Per-rule "already running" + cooldown, plus a global `max_runs_per_minute`.
- Trust: rules come from the user's own config file and may start programs. A task's reminder actions
  may only open web/mail links unless `allow_task_programs = true`, because tasks can be written by the
  API, imports and plugins. `run` never goes through a shell; event data reaches scripts as
  `TASKHUB_*` environment variables, not pasted into command lines.
- Tasks gained `ReminderActions` (text, one action per line) and `Attachments` (file paths) — migration
  `AddAutomation`. Attachments are read-only over the API.

**Phase 5 as built (first part)**

- Live events: `GET /v1/events?topics=…` upgrades to a plain WebSocket inside the existing API (same
  token/Host/Origin guard). One bus subscription per connection feeding a bounded drop-oldest channel,
  so a slow client never slows the app; closed cleanly when TaskHub stops. At most 32 streams.
- MCP: `th mcp` (in `TaskHub.Cli`, not the app) speaks MCP over stdio — newline-delimited JSON-RPC,
  implemented directly (no SDK dependency) — and maps tools onto the local REST API. Keeping it in the
  console exe gives assistants a stdio process to launch while the single-instance WPF app keeps running
  in the tray, and every change still goes through the same services. `--read-only` hides write tools.
- Deferred: token scopes and the plugin host (§9) — not needed for a single-user setup yet.

## 10. Notification system (Phase 3) — key decisions already settled

- `INotificationPresenter` lives in Core. Phase 2 ships a plain placeholder banner, and Phase 3
  swaps in the full `BannerManager`.
- `WindowStyle=None` + `WindowChrome` (no `AllowsTransparency`). Uses `DWMWA_SYSTEMBACKDROP_TYPE=DWMSBT_TRANSIENTWINDOW`
  (Acrylic) and `DWMWA_WINDOW_CORNER_PREFERENCE=DWMWCP_ROUND` on Windows 11 22H2+ (build ≥ 22621),
  with a translucent solid brush as the fallback.
- `ShowActivated=false` + `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`. The `WS_EX_NOACTIVATE` bit is
  cleared only while the inline reply box has focus.
- Placement: the monitor under the cursor (`MonitorFromPoint`), work area top-center,
  per-monitor-v2 DPI via the app manifest.
- Click-through shadow: `WM_NCHITTEST` returns `HTTRANSPARENT` outside the pill rectangle.

## 11. Cross-cutting

- **Logging:** Serilog → rolling file (daily, 14 retained) + debug sink; `ILogger<T>` everywhere.
- **Config:** Tomlyn → strongly typed `TaskHubOptions` via `IOptionsMonitor` (hot reload on file change).
- **Security:** no listening port other than loopback; incoming webhooks are opt-in only via a tunnel;
  integration secrets are protected with DPAPI (`ProtectedData`, CurrentUser scope).
- **Testing:** Core, Storage, Api and Cli tests are cross-platform. Windows-only interop tests are
  marked and skipped on non-Windows runners.
- **Packaging:** `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true` for `TaskHub.exe`
  and `th.exe`. MSIX is optional later.

## 12. Phase plan (each ends with runnable code + passing tests)

| Phase | Delivers | Tests |
|---|---|---|
| **0 – Shell** | solution skeleton, Program/Host, single-instance + pipe forwarding, tray (Open/Quit), TOML config, Serilog, event bus, autostart toggle, global hotkey service | event bus, config load/defaults, single-instance arg forwarding |
| **1 – Core + storage** | domain, EF Core + first migration, CRUD + undo, search/filters, quick-add parser, JSON import/export, CLI (via a minimal API slice), basic main window | services, parser, repositories on real SQLite, import/export round trip |
| **2 – Reminders** | scheduler, recurrence (Ical.Net), snooze, nag repeat, missed catch-up on resume/start, placeholder banner, daily backup | fake-clock scheduler tests incl. DST + sleep gaps |
| **3 – Banner** | full banner + extras A–F | placement/stacking math, DND queue logic |
| **4 – Automation** | rule engine, actions, clipboard/screenshot capture, launcher, file watcher, system triggers | rule parse/eval, action dispatch with fakes |
| **5 – API & plugins** | full REST + WS, scopes, plugin host (both kinds), MCP server | WebApplicationFactory: auth/Host/Origin guards, WS events |
| **6 – Integrations & AI** | Telegram, ntfy, calendar …, Ollama provider, Whisper voice | per-integration with mocked HTTP |

> Small deviation from the original order: the CLI talks to the local API, so Phase 1 ships a
> **minimal API slice** (tasks CRUD only, with token + Host/Origin guards). Phase 5 expands it.
