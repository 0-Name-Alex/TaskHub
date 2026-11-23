# Hands-on checks

The automated tests (`build.cmd`) cover the logic. These are the things only a person at the keyboard
can confirm. Use the dev copy so your real tasks aren't touched:

1. Quit any running TaskHub (tray icon → **Quit TaskHub**).
2. Double-click `run-dev.cmd` (data goes to `.devhome`).

Tick them off as you go; tell Claude about anything that fails or looks wrong.

---

## Island notifications and camera/mic indicator

- [ ] **Notifications** tab → **Normal**: a small black circle appears at the top centre and grows into the card; it shrinks back into the circle and disappears when it times out (hover pauses it).
- [ ] **High priority** / **Urgent**: orange / red accent; urgent springs a little more.
- [ ] Click all four test buttons quickly: one card at a time, the card shows **+3**, **+2** …, and each next one grows out of the circle.
- [ ] **With reply box**: click the box, type `snooze 10m`, Enter — works without TaskHub stealing focus before you click.
- [ ] Clicking the card (not a button) or its ✕ dismisses it.
- [ ] **Camera in use** / **Mic in use**: a short pill with a green / orange dot and the text, then it's gone.
- [ ] Real use: open the Windows Camera app (or join a call): *"Camera is using your camera"* appears for a few seconds. With `indicator_dot = true`, a small dot stays until you close the camera.
- [ ] Flip the laptop's camera switch (or press its camera key): *Camera off* (grey) appears briefly; press it again: *Camera on* (green). The **Diagnostics** tab shows `camera.disabled` / `camera.enabled`.
- [ ] Watch a video full screen (or press F5 in PowerPoint) and trigger a test notification: it waits until you leave full screen. Urgent ones still show.
- [ ] Set `style = "banner"` in the config and save: test notifications use the classic stacking cards again.
- [ ] Two monitors (if you have them): the island appears on the monitor your mouse is on, centred.

---

## Phase 4 — Automation

**Quick launcher**
- [ ] `Ctrl+Alt+Space` opens the search box near the top of the monitor your mouse is on.
- [ ] Typing part of an app name (e.g. `note`) shows the app; Enter starts it.
- [ ] Typing `buy milk tomorrow #home` and Enter adds a task and shows a "Task added" banner.
- [ ] Typing part of an existing task's title shows it; Enter opens it in the window, `Ctrl+Enter` completes it.
- [ ] Esc or clicking elsewhere closes the launcher.

**Clipboard → task (`Ctrl+Alt+V`)**
- [ ] Copy some text → a task with the first line as title (and the rest as notes).
- [ ] Copy an image (e.g. `Win+Shift+S`) → a task with the image under *Attachments*.
- [ ] Copy files in Explorer → a task with those files attached.
- [ ] The confirmation banner's **Open** button shows the new task.

**Screenshot → task (`Ctrl+Alt+S`)**
- [ ] The screen freezes and dims; dragging a rectangle creates a task with the screenshot attached.
- [ ] Esc or right-click cancels without creating anything.
- [ ] With two monitors (if you have them): the capture is the right area on each monitor.

**Reminder actions**
- [ ] Make a task due in 2 minutes; in the editor, put `open https://example.com` in *When reminded, also*; save.
- [ ] When the reminder fires, the page opens. Snoozing and the reminder firing again does **not** open it a second time.
- [ ] Typing `explode` in that box shows a red error under it.

**Rules** (tray → *Edit config file*)
- [ ] Add and save:
  ```toml
  [[rules]]
  name = "Test rule"
  hotkey = "Ctrl+Alt+F"
  then = ["dnd toggle", "notify Rule ran | Do Not Disturb toggled"]
  ```
- [ ] The **Automation** tab shows the rule as *Active*; `Ctrl+Alt+F` and **Run now** both show the banner and toggle Do Not Disturb.
- [ ] Break it on purpose (`then = ["nonsense"]`), save: the tab shows the problem in red; the rest of TaskHub keeps working.

**Folder watcher**
- [ ] Add and save:
  ```toml
  [[watch]]
  name = "downloads"
  path = "~/Downloads"

  [[rules]]
  name = "New download"
  when = "file.detected"
  then = ["notify Downloaded | {name}"]
  ```
- [ ] Download a file in your browser: one banner with the file name (not one per chunk).

**System events** — the **Automation** tab's *This computer* line shows idle time, battery and free disk
space; the **Diagnostics** tab shows `system.locked` / `system.unlocked` after `Win+L` and logging back in.

---

## Phase 5 — MCP server and live events

**MCP with Claude Desktop**
1. Claude Desktop → **Settings → Developer → Edit Config** (`claude_desktop_config.json`). Add (merge into an
   existing `"mcpServers"` section if there is one):
   ```json
   {
     "mcpServers": {
       "taskhub": {
         "command": "D:\\Projects\\TaskHub\\src\\TaskHub.Cli\\bin\\Debug\\net10.0\\th.exe",
         "args": ["mcp"],
         "env": { "TASKHUB_HOME": "D:\\Projects\\TaskHub\\.devhome" }
       }
     }
   }
   ```
   Keep the `env` line for the dev copy; remove it to use your normal TaskHub. Add `"--read-only"` to `args`
   to let Claude look but not change anything.
2. Fully quit Claude Desktop (tray icon too) and open it again.

- [ ] **taskhub** appears in Claude's tools/connectors menu.
- [ ] "What's due today in TaskHub?" lists your tasks.
- [ ] "Add a task: call the bank tomorrow 10am, high priority" → it appears in the TaskHub window at once.
- [ ] "Mark that done", then "undo that" → both show in TaskHub.
- [ ] With TaskHub closed, asking again gives a clear "TaskHub isn't running" answer.

**Live events**
1. Open PowerShell 7 (`pwsh`). For the dev copy first run `$env:TASKHUB_HOME = "D:\Projects\TaskHub\.devhome"`.
2. Run `D:\Projects\TaskHub\samples\events.ps1`.

- [ ] It prints `Connected`; adding/completing a task in TaskHub prints a line each time (e.g. `task.created  ui  Call the bank`).
- [ ] Quitting TaskHub ends the script cleanly.

---

## Task screen (layout, grouping, list colours)

- [ ] With no task selected the editor is hidden; click a task → it opens; Esc or ✕ closes it (edits are saved).
- [ ] Today view with an overdue task: sections **Overdue** (red) and **Today**; today's tasks show only the time.
- [ ] Priority shows as the colour of the round check (medium blue, high orange, urgent red).
- [ ] Hover a row → *move to tomorrow* and *delete* buttons; in Trash → *restore*.
- [ ] Tick a task → it fades and slides out; the "x of y done" ring next to **Today** moves on.
- [ ] Type `Pay rent tomorrow 9am #home !high @Finance` in quick add → chips for date, priority, list, tag.
- [ ] **+ New list** → type a name → Enter creates it; Esc cancels.
- [ ] Right-click a list → pick a colour → the sidebar icon, the dot by the heading, the `@list` on task rows and the editor's list box use it. *No colour* clears it.
- [ ] Right-click a list **on its icon** → the menu text is normal (not squares). *Icon* → pick one → the sidebar and heading show it.
- [ ] Text everywhere is readable in dark and light mode (Settings → Personalisation → Colours).

## If something goes wrong

- Build fails with "file is locked by TaskHub": quit TaskHub (tray → Quit) and build again.
- Logs: `.devhome\logs\` (dev copy) or `%LOCALAPPDATA%\TaskHub\logs\`.
- Claude Desktop's MCP log: Settings → Developer → *taskhub* → **Open Logs Folder**.
