# Shamash PC service (scaffold)

A .NET 10 Generic Host worker for the Windows PC. Currently a placeholder that proves the build, package and unattended-update pipeline. The real service (speech recognition, Claude agent, TTS) is not here yet.

## Contents

- `Shamash.Pc.Core/` - the logic (net10.0 library, unit tested); `Shamash.Pc/` - the Windows host (WinExe, WinForms tray icon and status window: `TrayApp`, `StatusForm`, `TrayPromotion`). The list below is the logic in Core except `Program.cs`.
- `Shamash.Pc/` - the app. `Program.cs` calls `VelopackApp.Build().Run()` first, then starts the host.
  - `UpdateService` - on startup and every 6 hours checks `https://github.com/BackSlasher/shamash` (GitHub Releases, stable only) via Velopack. If an update exists it is downloaded, then applied with `ApplyUpdatesAndRestart` only inside the update window. If `UpdateManager.IsInstalled` is false it logs "not installed via Velopack, skipping updates" and does nothing.
  - `UpdateWindow` - pure static-style parsing/containment logic (unit tested).
  - `HeartbeatService` - logs the assembly informational version once a minute.
  - `PhoneBattery` - the `battery` object of the phone's hello (`ServiceStatus.PhoneBattery`); the Status window shows a `Phone: battery 71%, not charging (seen HH:mm:ss)` line and the log gets `phone: battery 71%, not charging` whenever level or charging changes.
  - `LinkServer` / `LinkHostedService` - phone link probe server (see below); `LinkFrame`, `LinkSessionStats`, `WavWriter` are the pure parts.
- `Shamash.Pc.Tests/` - xunit tests for `UpdateWindow` (same-day window, window crossing midnight, empty window, malformed input falls back to the 03:00-05:00 default).
- `build.sh` - runs everything in podman (`mcr.microsoft.com/dotnet/sdk:10.0`), caches in volume `shamash-dotnet-cache` (NuGet packages in `/cache/nuget`, `vpk` in `/cache/tools`).

## Tray icon and status window

The service shows no window. The owner sees a coloured disc with a white shin on the taskbar: green idle, orange answering, red error, grey starting. Right-click menu: Status..., Open log folder, Open recordings folder, Check for updates (or "Install update <v> now" when one is pending), Quit Shamash. Double-click opens the status window (version, link, config, turns, update state, recent log, buttons for the log folder, update check and copying diagnostics). Closing the window only hides it. Quit stops the service until the next logon or a manual start of the `Shamash.Pc` task.

Windows 11 puts new tray icons in the overflow flyout, so on startup `TrayPromotion` sets `IsPromoted=1` under `HKCU\Control Panel\NotifyIconSettings\<id>` for our exe (retried for ~30 s while Explorer creates the entry). It only writes when the value is missing, so an icon the user hid stays hidden.

## Logging choice

Serilog (`Serilog.Extensions.Hosting`, `Serilog.Sinks.Console`, `Serilog.Sinks.File`). Microsoft.Extensions.Logging has no built-in file provider, and a rolling daily file (14 kept) is needed because the user never sees a console. Logs go to `%LocalAppData%\Shamash\logs\shamash-YYYYMMDD.log`, or `logs\` next to the exe if that folder is unavailable.

## Commands

All from the repo root; only podman is needed on the host. The build image is `pc/Containerfile` (the official .NET 10 SDK image plus `zstd`, which vpk needs to build delta packages; without it `pack` fails with "Could not find 'zstd' binary" as soon as a previous release sits in `pc/out/releases`). Keep `pc/out/releases` between releases, or fetch the previous one with `pc/build.sh vpk "[win]" download github ...`, so deltas get built.

```
pc/build.sh test       # dotnet test
pc/build.sh publish    # dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o pc/out/publish
pc/build.sh pack       # vpk [win] pack ... -> pc/out/releases
pc/build.sh upload     # vpk [win] upload github ... (publishes a real GitHub release)
pc/build.sh vpk ...    # run vpk with any arguments
```

`pack` runs:

```
vpk "[win]" pack --packId Shamash.Pc --packVersion $SHAMASH_VERSION_NAME --packDir out/publish \
  --mainExe Shamash.Pc.exe --runtime win-x64 --channel win --outputDir out/releases
```

`upload` runs:

```
vpk "[win]" upload github --repoUrl https://github.com/BackSlasher/shamash --publish --merge \
  --releaseName "Shamash $SHAMASH_VERSION_NAME" --tag "v$SHAMASH_VERSION_NAME" \
  --token $GITHUB_TOKEN --channel win --outputDir out/releases
```

The token is taken from `GITHUB_TOKEN`, else from `gh auth token` on the host, and passed into the container as an environment variable (not echoed). `--channel win` is explicit because `vpk upload` defaults the channel to the host OS (`linux` here) and would not find the Windows files. `--merge` lets vpk add its assets to a release that already exists; without it vpk stops with "There is already an existing release tagged 'v0.1.0'" (seen 2026-11-25). The phone APK is published first by `tools/release.sh --publish` (its `gh release create` refuses an existing tag), then this upload merges into the same release, so one tag carries both halves.

The `vpk` version (`vpk_version` in `build.sh`) must equal the `Velopack` package version in `Shamash.Pc.csproj` (both 1.2.161).

## Environment variables

| variable | where | meaning |
|---|---|---|
| `SHAMASH_VERSION_NAME` | build | app and package version, default `0.1.0` (MSBuild `Version`) |
| `GITHUB_TOKEN` | upload | token for the release upload |
| `SHAMASH_UPDATE_WINDOW` | runtime | `HH:mm-HH:mm` local time, default `03:00-05:00`; may cross midnight; malformed falls back to default |
| `SHAMASH_UPDATE_NOW` | runtime | `1` applies a downloaded update immediately |
| `SHAMASH_UPDATE_CHECK_MINUTES` | runtime | minutes between update checks, default 360, minimum 1 |
| `SHAMASH_LINK_PORT` | runtime | phone link probe port, default 8777 |
| `ELEVENLABS_KEY` | runtime | ElevenLabs API key (conversation STT and TTS) |
| `ANTHROPIC_API_KEY` | runtime | Anthropic API key (conversation LLM) |
| `SHAMASH_LLM` | runtime | `claude` (default) or `echo` |
| `SHAMASH_TTS_VOICE_ID` | runtime | ElevenLabs voice, default `JBFqnCBsd6RMkjVDRZzb` |
| `SHAMASH_CLAUDE_MODEL` | runtime | default `claude-opus-5-5` |
| `SHAMASH_PICTURE_ROOTS` | runtime | semicolon-separated folders searched only for `search_documents` kind picture and readable by `insert_picture`; config.json key `pictureRoots`; default on Windows: the Pictures folder, elsewhere none |
| `SHAMASH_DOCUMENT_ROOTS` | runtime | semicolon-separated folders the document tools may search and read; config.json key `documentRoots` (string array); default on Windows: Documents and Desktop, elsewhere none |

## Tools

With `llm=claude` the reply model runs a tool loop (`ToolLoop`, `ClaudeReplyModel`): the tools are sent with each Messages call; on `tool_use` blocks every tool runs, the assistant content and one `tool_result` block per call go back, and it repeats for at most 8 rounds, after which a last call with `tool_choice` none forces a text answer. If the final response has no text (it happens now and then), the loop logs `turn: empty reply, retrying once`, appends the user turn "ענה עכשיו במשפט אחד או שניים." and calls once more with `tool_choice` none; that call is counted in the turn's usage, is not a round, and a second empty reply fails the turn as `empty reply`. Tool errors reach the model as text starting `error:` and are never thrown. Each call (name, compact input, ms, result length) is saved in the turn JSON under `toolCalls` and the "turn: reply" log line and the status window show the count. `echo` mode has no tools.

Prompt caching: the system prompt (a text block) and the last tool definition carry `cache_control: ephemeral`, so the whole tool array plus the system prompt, identical on every call, is a cached prefix (request order is tools, system, messages). Token usage of every model call in a turn, including `translate_document`'s own calls, is summed (a per-turn `UsageScope` in an AsyncLocal) and logged as `llm usage: calls=N input=… cacheWrite=… cacheRead=… output=…`; the same numbers are saved in the turn JSON under `usage` (`calls`, `input`, `cacheWrite`, `cacheRead`, `output`). `input` counts only uncached input tokens. Measured on the win11-office VM (0.1.16, 2026-11-27 12:03): a 3-call turn read 18,042 tokens from cache and paid for 2,614 fresh input and 224 output tokens; a 4-call turn 24,056 cached / 1,675 fresh / 533 output. The cached prefix (prompt plus 23 tool definitions) is about 6,000 tokens, so a typical turn costs about two cents on Opus 5.5.

| Tool | Input | Result |
|---|---|---|
| `search_documents` | `query` (keywords), optional `kind` (`any`, `docx`, `pdf`, `xlsx`, `txt`, `picture`: jpg/jpeg/png/tif/tiff/gif/bmp, also searching `pictureRoots`), optional `max` (1-20, default 10) | lines `n. name \| folder \| yyyy-MM-dd \| size KB` (folder starts with the root's name, e.g. `Desktop/Desktop stuff`, `Documents/מאמרים`, or just `Desktop` for a file directly in the root; the other tools accept that form) (Windows Search index only; within 30 days .docx first, then other text formats, then .doc/.rtf/.pdf, newest first), `no documents found`, or `error: ...` when the index is unavailable |
| `read_document` | `path` (full path, or `folder/name` as search showed it), optional `max_chars` (default 6000, cap 30000) | text of .docx (paragraphs and tables, cells tab-separated), .doc and .rtf (Word COM on Windows, else the managed extractor, see Office automation), .pdf (PdfPig), .xlsx (sheet name, first 200 rows, tabs), .txt/.md/.csv (UTF-8, BOM detected), cut with `[truncated, N chars total]` |
| `show_in_explorer` | `path` | runs `explorer.exe /select,"<path>"`; returns the folder name |
| `open_file` | `path` | if the document is already open (exact path in Word/Excel, else window title, as `is_document_open`) focuses it and says so; else `Process.Start` with `UseShellExecute` (default app); `opened <name> with the default app` or the error text |
| `is_document_open` | `path` | first asks Word and Excel through COM (`IOpenDocuments`) for an exact full-path match (case-insensitive, normalised): `open in Word: <path>` or `open in Excel: <path>`, with `(has unsaved changes)` when not saved; a same-named file at another path gives `not open; a different file with the same name is open: <path>`. Otherwise (pdf in Edge, txt in Notepad) `open: window "<title>" (process <name>); matched by window title only...` (visible top-level window whose title holds the file name with or without extension, `WindowMatcher`), else `held by process <name>, no window found` (Restart Manager, rstrtmgr.dll), else `not open` |
| `focus_document` | `path` | for an exact Word/Excel match: `Activate()` on the document, `Visible = true`, `Application.Activate()` where it exists, then the Win32 steps below on the window handle from COM; otherwise finds the window by title as above, restores it if minimized, then `SetForegroundWindow`; if that does not take the foreground, the `AttachThreadInput` workaround, last the ALT-key trick; returns the step that worked, or `not open` |
| `describe_office_dialogs` | none | reads (never clicks) visible dialogs of Word, Excel and Outlook (window class `#32770` or `NUIDialog`): `<App> is showing a dialog "<title>": <text> [buttons: ...]`, or `no Office dialogs are open` |
| `close_document` | `path`, optional `save` (`ask` default, `yes`, `no`) | Word COM: finds the document by full path among the open ones; saved or `save=no` closes (`no` discards), `yes` saves then closes, unsaved with `ask` returns `unsaved changes; ask the user ... (not closed)`. After a successful close, if Word has no documents left it is quit (`Documents.Count` = 0, `Quit(SaveChanges: 0)`) and the result ends `; Word closed`; other documents open: Word stays; a failing count or Quit is swallowed and the close still reports done. Not in Word: `WM_CLOSE` to its window, result says the app may show its own prompt; `not open` otherwise |
| `describe_active_document` | none | built, not yet verified on the VM. Attaches to the running Word (never starts it) and reads `ActiveDocument`: `document: <name>; path: <full>; saved: yes\|no; track changes: on\|off; revisions: N; paragraphs: N; cursor: paragraph K of N, starts "<first 15 words>"; selection: "<up to 30 words>"\|none; footnotes: N`; `error: no document is open in Word` when Word is not running or has no document. Read only |
| `undo_last_edit` | optional `count` (1-50, default 1) | `ActiveDocument.Undo(count)`: `undone: N step(s); document: <name>`, or `error: nothing to undo` when Word's `Undo` returns false |
| `restore_backup` | optional `path` (as `read_document`; default the document open in Word) | copies the newest backup of that file name to `<name> (לפני שמש).<ext>` next to the original, ` 2`, ` 3` while the name exists; `restored the backup of "<name>" to <path>; the original was not touched`; `error: no backup of "<name>" was taken by Shamash` |
| `set_track_changes` | `on` (boolean) | sets `ActiveDocument.TrackRevisions`, reads it back: `track changes: was off, now on; document: <name>; undo available`; never accepts or rejects revisions |
| `set_paragraph_spacing` | `scope` (`selection`, `paragraph` = the paragraph under the cursor, `document`), optional `line_spacing` (`single`, `1.5`, `double`), optional `space_after_points` (0-1584); at least one of the two | sets `Range.ParagraphFormat.LineSpacingRule` (WdLineSpacing: 0 single, 1 one-and-a-half, 2 double) and/or `SpaceAfter` on the scope's range only, reads both back and fails if they differ: `line spacing: 1.5 (was single) for paragraph 3; document: <name>; undo available`; `selection` with nothing selected gives `error: nothing is selected; use scope paragraph or document` |
| `insert_text_at_cursor` | `text` (max 200 characters, at most one paragraph break) | `Selection.TypeText` at the insertion point (the special-character tool: the model names the character first): `inserted "<text>" at paragraph K; document: <name>; undo available`; refuses when text is selected (typing would replace it) |
| `add_footnote` | `text` (one line, max 1000 characters), optional `after_words` (max 255 characters) | `Footnotes.Add(range, Text:)` at the cursor (after the selection if any), or right after `after_words` found by `Range.Find` (case-insensitive) inside the paragraph under the cursor: `footnote N added after "<words>" in paragraph K; document: <name>; undo available`, or `error: "<words>" not found in the current paragraph` |
| `copy_paragraph_start` | `source` (as search shows it), optional `paragraph_number` (wins) or `words` (all must occur, any order, case/niqqud/quote-insensitive) | Verified on the VM 2026-11-28 (build cac39739) except that every call answered an InvalidComObjectException text (fixed in the source, needs redeploy). Remembers the open document and its cursor in `%LocalAppData%\Shamash\copy-session.json`, opens `source` read-only and visible (or uses it if already open), brings it to the front and selects the first candidate paragraph: `candidate 1 of N in "<source>": paragraph 3 starts "<12 words>"; ask him "זאת?"` |
| `copy_paragraph_next` | optional `hint` | Verified on the VM 2026-11-28 (build cac39739) except that every call answered an InvalidComObjectException text (fixed in the source, needs redeploy). Next candidate, highlighted the same way; `יותר למטה` forward, `יותר למעלה` back, other words narrow the list; at the end `no more candidates; ask him for other words or the paragraph number` |
| `copy_paragraph_confirm` | optional `plain` (boolean) | Verified on the VM 2026-11-28 (build cac39739) except that every call answered an InvalidComObjectException text (fixed in the source, needs redeploy). Backup, then the highlighted paragraph goes in at the remembered cursor (`FormattedText`, or `InsertAfter(text)` when plain) as one undo step; closes the source if the tool opened it (no save), activates the target, cursor after the paste: `pasted paragraph 3 of "<source>" into "<target>" at paragraph K; document: <target>; undo available` |
| `copy_paragraph_cancel` | none | Verified on the VM 2026-11-28 (build cac39739) except that every call answered an InvalidComObjectException text (fixed in the source, needs redeploy). Closes the source if the tool opened it, activates the target, forgets the copy |
| `insert_picture` | `path` (as `search_documents` kind picture shows it), optional `wrap` (`inline` default, `square`, `tight`, `top_bottom`), optional `width_cm` (0.5-50) | Verified on the VM 2026-11-28 (build cac39739). Backup, then `InlineShapes.AddPicture(path, LinkToFile:=False, SaveWithDocument:=True, Range)` at the cursor (selection start; a selection is not replaced); other wraps go through `ConvertToShape` and `WrapFormat.Type` (square 0, tight 1, top_bottom 4); a picture wider than the text is scaled to fit unless `width_cm` is given: `inserted picture "<file>" (<w>x<h> cm, wrap: square) after paragraph K starting "<words>"; document: <name>; undo available` |
| `resize_last_picture` | `width_cm` | Verified on the VM 2026-11-28 (build cac39739). Width of the picture inserted last (or the selected one), proportions kept, read back: `picture "<file>" resized to <w>x<h> cm (was ...)`; `error: no picture selected` when neither exists |
| `set_picture_wrap` | `wrap` (`inline`, `square`, `tight`, `top_bottom`) | Verified on the VM 2026-11-28 (build cac39739). Same target as `resize_last_picture`; converts between inline and floating as needed, reads the wrap back: `picture "<file>" wrap: top_bottom (was square)` |
| `review_changes_start` | none | Verified on the VM 2026-11-28 (build cac39739). Starts the review of the tracked changes of the document open in Word, or resumes the stored sitting (`review resumed ... at the next unanswered change: accepted A, rejected R, skipped S, remaining M`); `no tracked changes in this document` when there are none |
| `review_changes_next` | none | selects the current revision (`Range.Select`, scrolled into view) and returns `revision 3 of 8: insertion by Dani (2026-11-20): "…10 words [changed text] 10 words…"; answer with review_changes_answer`; the same revision until it is answered |
| `review_changes_answer` | `answers` (list of `accept`, `reject`, `skip`; Hebrew `קבל`/`דחה`/`דלג` also read) | applies the answers in order to the current and the following revisions: `accepted; rejected; accepted; remaining 5, next: revision 6 of 8: ...`; skipped ones come back once after the others, then `all answered or skipped`; backup before the first accept/reject, ends `; undo available` when anything was accepted or rejected |
| `review_changes_status` | none | `review: session since ...; revisions in the document: N; accepted A, rejected R, skipped S, remaining M`, or `review: no session` |
| `accept_all_changes`, `reject_all_changes` | optional `author` (part of the name), `type` (`insertion`, `deletion`, `formatting`), `confirmed_count` | act only when `confirmed_count` equals the number of matching revisions, else `error: N changes match, confirm with confirmed_count N`; no filter uses `Revisions.AcceptAll/RejectAll`, a filter goes through the matches from the last to the first |
| `word_recovery` | `action`: `check`, `open`, `close` | Partly verified on the VM 2026-11-28 (build cac39739): the pane was not recognised (fixed in the source, needs redeploy). Word's Document Recovery pane. `check`: `recovery pane: shown\|not shown\|unknown; autosaved files: N (newest "<name>", <date>)`; `open`: `Documents.Open` of the newest `*.asd` read-only as a new document, both stay open; `close`: clicks the pane's Close button through UI Automation, else `CommandBars.ExecuteMso("DocumentRecoveryClose")`, then looks again; never deletes `.asd` files. `describe_active_document` and `describe_office_dialogs` end `; recovery pane shown` when the pane is up |
| `list_mail_accounts` | none | Outlook stores: `n. name \| inbox: N messages, M unread` |
| `list_recent_mail` | optional `account` (part of the store name, default all), `max` (default 10, cap 30), `unread_only` | lines `id \| date \| account \| sender \| subject \| read/unread \| first 200 chars`, newest first; ids (`m1`...) are per process |
| `read_mail` | `id` | sender, date, subject, attachment names, plain body cut at 6000 chars |
| `draft_reply` | `id`, `text` | `item.Reply()`, the text above Outlook's quoted original, saved to Drafts, not sent; returns draft id, recipient, subject, text |
| `send_draft` | `id` of a draft made by `draft_reply` | `Send()`; `sent to <recipient>`; refuses any other id |
| `find_duplicates` | `folder` (as search shows it, `Desktop/...`/`Documents/...`, a Hebrew root name `מסמכים`/`שולחן העבודה`, or a full path), optional `include_subfolders` (default true) | plan id, counts (groups, duplicate files, bytes) and up to 30 group lines; nothing is moved |
| `apply_plan` | `plan_id` (optional; default: the most recently created plan not yet applied, since ids are lost between turns) | moves the extra copies into `<folder>\כפולים\`, counts and the first moves |
| `undo_plan` | `plan_id` (optional; default: the most recently applied plan not yet undone, found from the journal, so it also works after a restart; an unknown id names the default) | reverts a whole plan applied with `apply_plan` (all moves at once) from the journal, writes `undo:<plan>` lines to it, and starts its result by naming the plan and how many files went back; files already back in place are reported, not an error; counts and any skipped files; not for sorting sittings |
| `sort_folder_start` | `folder` (same forms as `find_duplicates`), optional `target_root` (a path, a document root's folder name such as `Documents`/`Desktop` case-insensitively, or its Hebrew name `מסמכים`/`שולחן העבודה`/`שולחן עבודה`; default: the Desktop root, else the folder's parent) | `session_id`, new/resumed, counts (total, remaining, done, skipped), duplicate count, existing destination folders (top level names); refuses more than `maxOrganizeFiles` files |
| `sort_folder_next` | `session_id` | newest unsorted file: name, folder, date, size, exact duplicates in the folder, ~600-char excerpt (`cannot read .doc files` for .doc/.rtf), and evidence from the other folders: `identical copies elsewhere`, `similar versions elsewhere` (up to 3, similarity >= 0.6), `folders with similar content` (top 2 with a score); the same file again until decided |
| `sort_folder_move` | `session_id`, `destination` (name or path under `target_root`), optional `create_if_missing` (default false) | moves the current file, moves its exact duplicates to `<target_root>\כפולים\`, journals, counts |
| `sort_folder_skip` | `session_id`, optional `reason` | skips the current file for this sitting (`never` skips it for good), counts |
| `sort_folder_status` | `session_id` or `folder` | counts and the last 5 moves |
| `undo_last_move` | none | puts back the single file last moved by `sort_folder_move` in a sorting sitting (with its duplicates); refuses with `use undo_plan` when the newest journal entry is a plan move, and when the original place is taken |
| `translate_document` | `path` (as for `read_document`), `target_language` (`en` or `he`), optional `only_summary` (default false) | `ok: saved as <path relative to the root> \| folder: <name> \| paragraphs: N \| batches: B \| ms`, or with `only_summary` a five-sentence summary in the target language and no file; `error: ...` on failure |
| `report_missing_feature` | `request` (what the user asked, in his words), optional `details` | appends `{"time","request","details"}` as one JSON line to `%LocalAppData%\Shamash\missing-features.jsonl`, logs a warning, returns `recorded. Tell the user ...`; `error: ...` when the file cannot be written |
| `look_at_screen` | none | `screenshot WxH of the whole screen taken at HH:mm; foreground window: "<title>" (<process>); other visible windows: ...` plus the screenshot as an image (see Escape hatches); `error: screen capture not available` |
| `list_windows` | none | `n. "<title>" (<process>) [front] [minimized]`, front first, at most 15 |
| `describe_window` | optional `window` (title part or process name; default the foreground window) | `window "<title>" (<process>)` then `- <kind>: <name> [disabled] [checked\|unchecked] [selected] (value: ...)`, text runs joined into `- text: ...`, at most 60 lines / 2500 characters, then `... (more)`; `error: no window matches "<x>"; visible windows: ...` |
| `click_control` | optional `window`, `name`, optional `kind` | exact name first, then case-insensitive contains; brings the window forward, invokes the control, waits 700 ms; `clicked <kind> "<name>"; now in front: "<title>" (<process>)` plus `; a dialog appeared: ...` or `; the window closed`; `error: N controls named "<x>"; be more specific (kinds: ...)` when ambiguous |
| `close_window` | `window` | `WM_CLOSE`, waits 1 s; `closed "<title>"` or `"<title>" is still open; it shows: ...` (an unsaved-changes prompt, for example); refuses explorer (desktop, taskbar, folder windows) and the assistant's own windows |
| `press_key` | `key` (`escape`, `enter`, `tab`, `space`, `up`, `down`, `left`, `right`, `alt+f4`) | sends the key to the foreground window; `pressed <key>; now in front: ...`; `alt+f4` is refused while explorer or the assistant is in front

The four window tools (`WindowTools.cs`, all P/Invoke in `WindowNative`) resolve `path` like `read_document` (same guard, shared `ReadDocumentTool.Resolve`) and return `error: Windows only` on other systems.

Test aid, text turns: with `allowTextTurns` true in config.json (or `SHAMASH_ALLOW_TEXT_TURNS=1`; default false) a conversation `hello` carrying `"text"` skips STT and uses the text as the transcript (see `docs/link-protocol.md`). Drive it without a phone: `tools/pclink/client.sh ws://<pc>:8777/ --text "תפתח את המכתב לעירייה"`. Add `"noTts": true` to that hello (`client.sh ... --text "..." --no-tts`) to skip speech synthesis for the turn (reply text and `done` only, no audio, no TTS cost).

How search works: on Windows it queries the Windows Search index through OLE DB (`Provider=Search.CollatorDSO`, table `SYSTEMINDEX`, one `SCOPE='file:<root>'` per document root; `CONTAINS` prefix match of all words ANDed, or `FREETEXT` of the whole query (catches Hebrew prefixed forms such as `העות` for `העות'מאנית`), or all words in the file name via `LIKE`; newest first). Claude keeps no index of its own. Words are split on whitespace, apostrophes, geresh, gershayim, quotes and hyphens. Search is index only: if the index throws (E_FAIL for a minute after boot, provider missing, not Windows) the tool returns a hard `error: ` saying the Windows Search index is not available right now, and the model tells the user plainly ("אינדקס החיפוש לא זמין כרגע, נסה שוב בעוד דקה") without guessing file names; there is no folder walk or other fallback. An index that answers with nothing gives `no documents found`. The index can lag deletions, so hits whose file no longer exists on disk are dropped and a trailing `(index entries for files that no longer exist were ignored: N)` line says so. `documentRoots` stays the scope filter. Hebrew is plain UTF-16 end to end. The tool description tells the model to give Hebrew words without prefix letters and to retry once with another form before saying a file does not exist.

Path guard: `read_document` refuses any path not under a document root (after `..` is normalised) with `error: path not under a document root`. Roots come from `documentRoots` / `SHAMASH_DOCUMENT_ROOTS` (see above); only the count is logged.

### Office automation

`OfficeCom.cs`, `OutlookTools.cs`, `LegacyDocReader.cs`. Word and Outlook are driven through late-bound COM (`Type.GetTypeFromProgID` + `Activator.CreateInstance`, or `GetActiveObject` through oleaut32 to attach to the running instance, then `dynamic`); no Interop assemblies or PIAs. It works only in the user's interactive Windows session (the service runs there), everything else answers `error: Windows only`. Each call runs on its own STA thread with a 25 s timeout (`OfficeCom.TimeoutMs`) so a hung Office or a modal dialog fails the call instead of the turn; COM objects are released after use; failures come back as `error: ... (0xHRESULT)`. Word calls: `Documents.Open(FileName, ConfirmConversions:false, ReadOnly:true, AddToRecentFiles:false, NoEncodingDialog:true, Visible:false)`, `Content.Text`, `Close(SaveChanges)`, `Saved`, `ReadOnly`, `FullName`; a hidden `Word.Application` is created only for reading and quit afterwards, never for closing. Outlook calls: `GetNamespace("MAPI")`, `Stores`, `GetDefaultFolder(6)`, `Items.Restrict/Sort/GetFirst/GetNext`, `GetItemFromID`, `Reply()`, `Save()`, `Send()`; Outlook is started if not running and left running. Caveat: Outlook's object-model guard may show a security prompt on `Send` or address access when antivirus is not current; the tool then waits for the user to click and times out after 25 s, and the model is told to say so. Exact document identity: `IOpenDocuments` (`OfficeOpenDocuments`) attaches with `GetActiveObject` (never starts Word or Excel) and lists `Documents` / `Workbooks` with `FullName`, `Saved` and the window handle (Word `doc.ActiveWindow.Hwnd`, Excel `Application.Hwnd`); COM errors per application are swallowed (it returns what it got). `is_document_open`, `focus_document`, `open_file` and the Word close path all compare normalised full paths, so two files with the same name are told apart; window-title matching remains only for non-Office files and the result says so. Preemptive Word settings: on a Word we create, `Visible=false`, `DisplayAlerts=0`, `AutomationSecurity=3` (force-disable macros), `Options.ConfirmConversions=false`, and `Documents.Open(..., OpenAndRepair:false)` added to the existing arguments; on the user's running Word nothing persistent is changed, only `DisplayAlerts=0` around our call, restored afterwards. Dialog reporting: when an Office call times out or fails, `DialogProbe` (Win32, no clicking) enumerates visible top-level `#32770` and `NUIDialog` windows owned by WINWORD, EXCEL or OUTLOOK, reads the text of their `Static`, `RichEdit` and `Button` children with `WM_GETTEXT`, and appends `Word is showing a dialog "<title>": <text> [buttons: ...]` to the error, so the model can tell the user what to press; `describe_office_dialogs` returns the same on demand. Office 2013+ `NUIDialog` windows draw their text themselves, so there only the title may be readable. Not tested against real Office yet. `.doc` reading wants Word on the PC; without it (or when Word fails) `read_document` falls back to `LegacyDocReader`: an RTF stripper (`\par`, `\tab`, `\'hh` bytes decoded with the document's `\ansicpg`, Hebrew cp1255, `\uN` with fallback skipped, font/colour/picture groups dropped) and a managed Word 97 reader (OLE compound file, FIB, CLX piece table; unencrypted files only; main text, no headers or footnotes). Search walks and sorting evidence still treat .doc/.rtf as unreadable.

### Editing the open Word document

Verified on the VM (2026-11-28, service 0.1.25, Word 2016, harness cases and the `word-edit-basics` scenario with the real model passed), with the exceptions listed at the end of this paragraph. `WordEditTools.cs`: seven tools (`describe_active_document`, `undo_last_edit`, `restore_backup`, `set_track_changes`, `set_paragraph_spacing`, `insert_text_at_cursor`, `add_footnote`) that act on the document open in the running Word at the place the user put the cursor. They attach with `GetActiveObject` (Word is never started), run through `OfficeCom.Guarded` (STA thread, 25 s timeout, dialog probe on failure) and answer `error: no document is open in Word` when there is none. Input is validated before any COM call; every result names the document (`document: <file name>`) and mutations end with `; undo available`. All decisions and result texts are static functions in `WordEditText` (unit tested); the COM code is late bound like `WordAutomation`.

- Backup: before the first edit of a document in this process every mutating tool calls `WordBackups.EnsureBackup`, which copies the file on disk to `%LocalAppData%\Shamash\backups\<yyyyMMdd-HHmmss> <name>` and adds `backup: <path>` to that one result (a failed copy aborts the edit; a document never saved gives `backup: none (document never saved)` and is retried on the next edit). The copy is the file as saved on disk, so unsaved changes made before the first Shamash edit are not in it, and two files with the same name share one backup name. `restore_backup` only adds a file (`<name> (לפני שמש).<ext>`, then ` 2`, ` 3`) and nothing is ever deleted.
- Undo: the edits go through the object model, so `undo_last_edit` (`ActiveDocument.Undo`) takes them back like Ctrl+Z; the track-changes toggle is NOT an undo step in Word 2016 (verified), so its result says "not an undo step" instead of "undo available". Verified on 0.1.25: `Footnotes.Add` and two consecutive `ParagraphFormat` assignments are 2 undo steps each, so `undo_last_edit` took back half an edit; fixed by running those three tools inside one `Application.UndoRecord` (`ActiveWord.InUndoRecord`; probed by hand on the VM, one `Undo(1)` reverted footnote plus spacing), redeployed as build 6062bf20 (0.1.25) and re-run on the VM: one `undo_last_edit` reverted line spacing and space after together, `count: 2` took back a footnote plus an inserted character, and cases 2, 2b, 3, 5, 5b, 6, 6b passed. Other findings: `Paragraphs.Count` includes table row-end marks and the final empty paragraph (the seed document reports 19, not 15); table row-end marks keep their own line spacing under scope document (harmless); the footnote reference stays at the cursor and the view stays in the main pane.
- Read-back: track changes and paragraph spacing are read back after setting and the tool returns an error rather than a false success; the spacing result also gives the previous value. The system prompt makes the model read dictated text (a character, a footnote) back and wait for a yes in the next turn, and ask for a yes before changing spacing in the whole document.
- Seed data and the VM checks: `tools/seed-docs/gen.py` (`מאמרים/מאמר על ירושלים - לעריכה.docx`), `tools/winvm/harness/` (`word-edit.ps1`, `word-edit-cases.md`) and the `word-edit-basics` scenario.

### Copying a paragraph from another document and inserting a picture

Verified on the VM 2026-11-28 (build cac39739, `tools/winvm/harness/word-edit-cases.md`). Pictures: all cases and the `word-picture` scenario passed (search with `kind: picture`, insert inline and floating, 6.3x4.2 cm for a PNG without DPI, wrap square/tight/top_bottom/inline, resize by cm, one undo for insert+wrap+size, the selected picture when no session). Copy paragraph: the highlight is visible (the read-only source opens in Read Mode with the paragraph selected and the Word window in front) and the source `Saved` stayed True with an unchanged hash, but the deployed build threw `InvalidComObjectException` at the end of `start`, `next` and `cancel` and before the paste in `confirm` when the tool had opened the source: `WordDocs.Find` released every non-matching document (one RCW per document, shared with `ActiveWord.With`) and the tools released the shared `Documents` object. Fixed in the source (needs redeploy; the paste itself worked, plain and formatted, one undo, when the source was opened before the target); the `word-copy-paragraph` scenario failed on this build. `describe_active_document` also failed on a read-only document (`TrackRevisions`, 0x800A11FD); now it reports `read-only document, track changes not readable`. `WordCopyTools.cs`: the paragraph is spotted together on screen. `copy_paragraph_start` records the target (the active document's full path) and its cursor in `copy-session.json`, opens the source read-only and visible in the same Word (an already open source is used and never closed by the tool), activates it and selects the first candidate; the highlight is the read-back, so the system prompt tells the model to ask only "זאת?". `copy_paragraph_next` moves it (candidates are numbers, or paragraphs containing all the words, or every non-empty paragraph); `copy_paragraph_confirm` pastes at the remembered cursor, so a cursor inside a paragraph splits it like Word's own paste of a whole paragraph. Candidate selection, hints, wording and the state file are static code in `WordCopyText` and `SessionFile<T>`; the source file is only read, and if its `Saved` flag turned false the result carries a warning and it is never saved. `WordPictureTools.cs`: `insert_picture` reads picture files under the document roots or `pictureRoots` (`SHAMASH_PICTURE_ROOTS`, config key `pictureRoots`, default the user's Pictures folder; alias `תמונות` in `PathResolver`); the picture is embedded (`LinkToFile:=False`), the file is never changed. The last inserted picture is remembered in `picture-session.json` (anchor position for inline, a `ShamashPicture<ticks>` shape name for floating), with the selected picture as fallback. Insert, paste and conversions run inside one `Application.UndoRecord` so a single `undo_last_edit` takes them back. Seed data: `tools/seed-docs/gen.py` writes `מאמרים/ירושלים בתקופה העות'מאנית.docx` (five numbered, heavily formatted paragraphs, two about the sultan) and `תמונות/` (three small PNGs, two with similar names); the VM's `Pictures` folder must receive that `תמונות` folder (copy it from `shared/seed` and wait for the indexer). VM checks: the "Copy paragraph" and "Picture" sections of `tools/winvm/harness/word-edit-cases.md` and the scenarios `word-copy-paragraph` and `word-picture`.
### Reviewing tracked changes

Verified on the VM 2026-11-28 (build cac39739): cases R1 to R15 and the `word-review` scenario passed. Exception: in the VM's Simple Markup view nothing of a revision is drawn and a deletion cannot be selected (the selection lands in the Revisions pane), so `Select` now sets All Markup and inline revisions first (needs redeploy, not seen on screen yet; with them set by hand the deletion is struck through and selected). `WordReviewTools.cs`: the tool owns the pointer of a review sitting. The state file `%LocalAppData%\Shamash\review-sessions.json` holds one session per document path: the revisions in document order (`start|type|author` key, type, author, date, first 80 characters of the text, status `pending`/`skipped`/`left`/`accepted`/`rejected`), the current position, and the started time. Word's revision indices shift after every accept or reject, so every call takes a fresh snapshot of `ActiveDocument.Revisions` and resolves each stored item against it (same type, author and text; the closest start wins), dropping the ones that vanished and appending new ones. Accepting a deletion or rejecting an insertion also moves the stored starts of the later items. A session resumes on another day; skipped revisions are asked once more after the others. Types: insertion, deletion, `formatting` (property, paragraph property, style), others by name. All decisions are in `ReviewLogic`/`ReviewEngine` over `IRevisionDocument` (unit tested with a fake document); `ComRevisionDocument` is the late-bound Word side. Seed: `מאמרים/מאמר עם הערות עורך.docx` (8 changes by דני לוי and רינה כהן); VM checks in `tools/winvm/harness/word-edit-cases.md` and the scenario `word-review`.

### Word's Document Recovery pane

Verified on the VM 2026-11-28 (build cac39739) only in part: after a real crash Word 2016 starts on the Start screen ("Show Recovered Files"), which the tools do not report; after clicking it the pane was up but `describe_office_dialogs`, `describe_active_document` and `word_recovery check` all said it was not shown, because the pane has no element named "Document Recovery" in UI Automation (its intro text is `Word has recovered the following files...`). Fixed in the source (needs redeploy), so `close` and the pane note were never exercised; `open` worked (read-only recovered document with the typed text, both `.asd` untouched). `WordRecoveryTools.cs`: `WordRecovery.Detect` combines the `*.asd` files under `Application.Options.DefaultFilePath(5)` (modified times) with the pane itself, found through `IDesktopAutomation.Describe` on every WINWORD window: an element named "Document Recovery" or "שחזור מסמכים", or a list entry ending `[Original]`/`[Recovered]` (UI Automation only reports leaf controls, so the pane container itself is not seen). Closing clicks a button named "Close" or "סגור", but only when that is the first such button in the window's control order and it comes after the pane's title, because Word's own title-bar Close (which would quit Word) is found first by name otherwise. Open and close do not need a document (the pane shows with none open). Unknowns to settle on the VM: whether the pane title is exposed to UI Automation under that name, whether the UI Automation walk (800 nodes, 4 s) reaches the pane past the ribbon, whether `DefaultFilePath(5)` works as a late-bound call, and whether `DocumentRecoveryClose` exists.

### Escape hatches

Three generic ways out of situations the dedicated tools do not cover, all described to the model in `ClaudeReplyModel.SystemPrompt`.

1. `report_missing_feature` (`MissingFeatureTool.cs`): when no tool can do what the user asked, the model records the request in his words and tells him in one sentence that it is not possible yet and was noted for the maintainer. Local file only, no network; once turn records are uploaded to the home server they carry every tool call with its input, so the server sees these requests through them.
2. `look_at_screen` (`ScreenTool.cs`, capture in `Shamash.Pc/ScreenCapture.cs`): for a window the user does not understand or a tool that failed without a clear reason. The whole virtual screen is captured with `Graphics.CopyFromScreen`, scaled to at most 1280 px on the longest side and sent as a PNG image block inside the `tool_result` (`IRichAgentTool`, `ToolResult`/`ToolImage`; about 1200 image tokens). It is the only tool that sends an image. The PNG is also saved under `%LocalAppData%\Shamash\pclink\screens\yyyyMMdd-HHmmss.png` (last 50 kept) so the owner can see what the model saw, and the turn record's `toolCalls[].imageBytes` holds the size of the picture sent (0 for other tools).
3. Generic window tools (`DesktopTools.cs`; Windows side `Shamash.Pc/UiaDesktopAutomation.cs` over FlaUI.UIA3 and Win32): `list_windows`, `describe_window`, `click_control`, `close_window`, `press_key`. The Core tools own matching, formatting and the safety rules and talk to `IDesktopAutomation`, so they are unit-tested on Linux with a fake. The model is told to use them only for unexpected situations (a dialog, a program opened by accident, a question the computer asks), never for Word, Excel or Outlook, to prefer closing the window or pressing Cancel/No over OK, and never to press anything that confirms, agrees, buys, deletes or sends without a clear yes. They never type free text and never start programs; `press_key` has a fixed whitelist. Clicking uses UI Automation patterns (Invoke, Toggle, SelectionItem, ExpandCollapse; Invoke runs on its own task with a 1.5 s wait because buttons that open modal dialogs do not return); without a pattern it focuses the window and clicks the centre of the control. Password fields are never read out. Without the Windows host (`DesktopServices` null in `TurnPipeline.BuildTools`) the screen and window tools are left out. Verified on the VM (0.1.24, 2026-11-28): `missing-feature` and `settings-escape` scenarios passed with the real model: `look_at_screen` returned a legible 1280x800 PNG of Windows Settings (566 KB, the model read the text in it and named the window), `close_window` closed it (first with the Hebrew word, which matched nothing and listed the visible windows, then with `Settings`), and `report_missing_feature` wrote its journal line. Not exercised on the VM: `click_control`, `press_key`, `describe_window` on a dialog, password-field redaction.

### Organising

`find_duplicates` skips files over 50 MB and groups exact copies by SHA-256; among .docx/.txt/.pdf it also groups near-copies (Jaccard of word 3-grams >= 0.8, sizes within 30%, case and whitespace ignored). The keeper of a group is the newest file (ties: a name without "עותק", "Copy", "(1)", then the shortest). The plan is kept in memory (`p1`, `p2`, ...; lost on restart), written as `<folder>\תוכנית סידור <yyyy-MM-dd HHmm>.txt` and opened with the default app on Windows. `apply_plan` moves the extra copies into `<folder>\כפולים\` keeping their subfolder structure; an existing target gets ` (2)`; nothing is overwritten or deleted; a plan can be applied once. Every move is a tab-separated line (timestamp, plan id, from, to) in `%LocalAppData%\Shamash\moves.log`. `undo_plan` reads that journal and moves the files back, skipping any whose original place is now taken. The system prompt makes the model describe the plan and wait for a clear yes in a later turn.

### Sorting a folder piecemeal

`SortTools.cs`. For a messy "salad" folder the model works one file per decision (system prompt: describe, propose one destination, move on yes, skip on no, undo on "תחזיר", report remaining every 5 files, stop on request). Sessions live in `%LocalAppData%\Shamash\sort-sessions.json` (`SortStore`, process-wide, written atomically after each change), so a sitting resumes after a restart: `sort_folder_start` on the same folder finds its session, forgets this-sitting skips (`skipped`) and keeps `never`. Shape: `{"version":1,"nextId":N,"sessions":[{"id":"s1","folder":...,"targetRoot":...,"created":...,"current":path|null,"nextMove":N,"decided":[{"path","status":"done|skipped|never","reason","destination","tag"}],"moves":[{"time","from","to","tag"}],"cache":{path:{"size","mtime","sha","profiled","sketch":[...],"words":[...]}}}]}`. Files are enumerated fresh each call (newest first; `~$` and `.lnk` skipped; the `כפולים` folder and plan files ignored); only decisions are stored. Evidence for `sort_folder_next` is computed from files under the document roots outside the session folder: SHA-256 only for files of the same size, near-duplicates only among the same extension (.docx/.txt/.pdf) within 30% of the size (at most 200 candidates, bottom-64 sketch of word 3-grams), folder similarity from the 40 most frequent words of up to 20 documents per top-level folder under `target_root` (mean of the top 3 matches). Hashes and profiles are cached in the session keyed by path, size and mtime, so a resume does not recompute unchanged files; a 25 s budget cuts the evidence and says so. The code only reports evidence; the model decides. Moves are journaled in the same `moves.log` with tag `<session>#<n>` (one tag per file and its duplicates); `undo_last_move` writes `undo:<tag>` lines so repeated undos step further back, and puts the file back to pending in the session.

Guards: `find_duplicates` and the sort tools refuse a folder with more than `maxOrganizeFiles` files (default 3000; config key or `SHAMASH_MAX_ORGANIZE_FILES`); `~$*` Word lock files and `.lnk` shortcuts are skipped in search results, duplicates and sorting. Default document roots stay Desktop and Documents; the D: archive must be added explicitly in `documentRoots` / `SHAMASH_DOCUMENT_ROOTS` (and then the 3000-file limit applies per folder). Search results list index hits before walk hits, and within a 30-day window .docx before other text formats before .doc/.rtf/.pdf.

Translation: `translate_document` reads the document and sends the non-empty paragraphs to the model in batches of up to 40 (and 15000 characters) as a JSON array of `{id, text}`; the answer must be the same array in the same order, parsed strictly and retried once on a malformed reply, otherwise the tool returns an error. The call goes through `ITranslator` (`ClaudeTranslator` uses the same `AnthropicClient` construction as the chat, `Effort.Low`, no tools; the model is `translateModel` in config.json or `SHAMASH_TRANSLATE_MODEL`, default the chat model). The new file is built in memory and written only when every batch succeeded; the original is never opened for writing. The result is saved next to the original as `<name> (English).<ext>` or `<name> (עברית).<ext>`; if that exists, ` (2)`, ` (3)` and so on is appended, nothing is overwritten. .docx: every paragraph in the body, tables included, is rewritten in place: the text goes into the paragraph's first text run (its run properties, the paragraph style, numbering and table structure stay), the other runs' text is removed, and for Hebrew `w:bidi` is added while for English it is removed, with `w:jc` left and right swapped when present; `w:rtl` is added to (Hebrew) or removed from (English) every run's properties and the paragraph mark's. Formatting that changes mid-paragraph (a bold word inside a sentence) is lost: the paragraph takes the first run's look. Not translated and left as they are: headers and footers, footnotes and endnotes, text boxes, comments, images. .txt and .md: blocks separated by blank lines, saved with the same extension as UTF-8. .pdf: text is extracted with PdfPig, split on blank lines (very long blocks are cut at sentence ends), and a plain .docx with one paragraph per block is produced; the PDF layout is not kept. Not verified against real Word rendering or a real model call yet.

## Phone link probe

Port of `tools/pclink/server.py`: Kestrel (ASP.NET Core, framework reference, no extra packages) runs inside the Generic Host and serves a WebSocket at `ws://<host>:8777/` (plain ws for now). Port from `SHAMASH_LINK_PORT`. If the port cannot be bound it logs "link: failed to start" and the rest of the service keeps running.

Protocol:

- Text `{"type":"hello","sampleRate":16000,"seconds":N,"device":"..."}` first; text `{"type":"end"}` when the phone is done.
- Binary frames: 4-byte big-endian uint32 seq, 8-byte big-endian int64 phone send time (ms), then raw 16-bit mono PCM (3200 bytes = 100 ms at 16 kHz). Frames under 12 bytes are ignored. Messages over 1 MB close the socket.
- Every frame is answered with `{"type":"ack","seq":S,"tPhone":T,"tServerMs":<unix ms>}`.
- On `end` or close: `{"type":"summary","frames":N,"bytes":N,"seconds":<3 decimals>,"gaps":<missing seq count>}`, then the server closes.

The PCM is written to `%LocalAppData%\Shamash\pclink\pclink-yyyyMMdd-HHmmss.wav` (`.\pclink` if LocalAppData is unavailable), mono 16-bit at the hello's sample rate. Log lines (Information):

```
link: listening on http://0.0.0.0:8777
link: hello from <ip:port>: {"type":"hello",...}
link: peer=<ip:port> frames=N bytes=N audio=S.SSs wall=S.SSs gaps=N interarrival_ms(min/med/max)=a/b/c
```

Test from the Android debug build (one quoted string, because adb joins its arguments with spaces):

```
adb shell "am broadcast -a net.backslasher.shamash.PCLINK_PROBE -p net.backslasher.shamash --es url ws://<pc-ip>:8777/ --ei seconds 10"
```

### Direct tool invocation (test aid)

With `allowTextTurns` on, a text message `{"type":"tool","name":"<tool>","input":{...}}` runs one tool from the conversation's tool list directly (no model, STT or TTS, 25 s timeout) and gets `{"type":"tool_result","name":...,"ms":N,"result":"..."}` or an `error` back, then the socket closes. With the setting off it is refused. For asserting Office/COM behaviour on the VM:

    tools/pclink/client.sh ws://127.0.0.1:8777/ --tool is_document_open '{"path":"Desktop/Reading list.docx"}'

Prints the result verbatim; exit 0 result, 2 error, 3 unknown tool.

### Windows Firewall

The first time `Shamash.Pc.exe` listens, Windows Defender Firewall may show an allow prompt, or (on a Public network profile, or when no one can answer the prompt) silently block inbound connections. Allow it from an elevated PowerShell:

```
New-NetFirewallRule -DisplayName "Shamash link" -Direction Inbound -Protocol TCP -LocalPort 8777 -Action Allow -Profile Private
```

or per program: `New-NetFirewallRule -DisplayName "Shamash link" -Direction Inbound -Program "$env:LocalAppData\Shamash.Pc\current\Shamash.Pc.exe" -Action Allow` (the path changes per Velopack version folder only if you use `current\`; unverified whether the rule survives updates, so the port rule is safer). Drop `-Profile Private` to include Public networks. Kestrel binding `0.0.0.0` on a non-reserved port as a normal user needs no URL ACL (that is a http.sys/HttpListener requirement); this is Kestrel's own socket, but it has not been tried on the Windows VM yet (unverified). Not verified either: the firewall prompt behaviour itself.

Verified on Linux in podman only: the in-process integration test (`ClientWebSocket`: hello, 3 frames, end; 3 acks, summary frames=3 bytes=9600 gaps=0, WAV written). A trimming/single-file `publish` for win-x64 succeeds and `Shamash.Pc.exe` grew to about 104 MB; it also leaves `aspnetcorev2_inprocess.dll` (IIS module, unused) beside the exe.

## Conversation mode

A hello with `"mode":"conversation"` (see `docs/link-protocol.md`) runs one turn per connection after `end`: ElevenLabs batch STT (`scribe_v3`, `heb`, WAV built with `WavWriter`; audio under 0.5 s skips STT and counts as an empty transcript) -> `transcript` -> Claude (`Anthropic` NuGet 13.14.0, non-beta Messages API, `output_config.effort` low, system prompt in `ClaudeReplyModel.SystemPrompt`) -> `reply` -> ElevenLabs streaming TTS (`eleven_v4_turbo`, `pcm_16000`) -> `audio_start`, binary PCM chunks as they arrive (even lengths; an odd trailing byte is carried to the next chunk), `audio_end`, `done`, close. Any failure sends `error` with stage `stt`/`llm`/`tts`/`internal` and closes. `cancel` or a closed socket cancels the pipeline and nothing more is sent. Probe mode (no `mode`, or `probe`) is unchanged.

Settings are loaded once at startup: environment variables win over `%LocalAppData%\Shamash\config.json` (fallback `.\config.json`; first existing file only), camelCase keys:

```json
{ "elevenLabsKey": "...", "anthropicApiKey": "...", "llm": "claude", "ttsVoiceId": "JBFqnCBsd6RMkjVDRZzb", "claudeModel": "claude-opus-5-5", "documentRoots": ["C:\\Users\\x\\Documents"] }
```

Env names: `ELEVENLABS_KEY`, `ANTHROPIC_API_KEY`, `SHAMASH_LLM`, `SHAMASH_TTS_VOICE_ID`, `SHAMASH_CLAUDE_MODEL`. Key values are never logged, only whether they are present. Without an ElevenLabs key a turn fails with stage `internal`, "no ElevenLabs key configured" (probe mode still works); without an Anthropic key and `llm=claude` it fails with stage `llm`.

`SHAMASH_LLM=echo` makes the reply equal to the transcript, so STT and TTS can be tried without an Anthropic key.

History: one in-memory conversation for the life of the process, user and assistant entries alternating, capped at the last 40 entries (20 exchanges), dropped in pairs. A turn that fails before the reply does not add to it.

Log lines (Information unless noted):

```
config: elevenLabsKey=true anthropicApiKey=false llm=claude
turn: transcript N chars in X ms
turn: reply N chars in X ms
turn: tts first byte X ms, total Y ms, bytes B
turn: done total X ms            (or: turn: cancelled at <stage>)
turn: error at <stage>: <message>   (Warning)
link: conversation peer=... frames=N bytes=N audio=S.SSs wall=S.SSs
```

Each turn's audio is written to `%LocalAppData%\Shamash\pclink\pclink-yyyyMMdd-HHmmss.wav` (written at `end`, before the pipeline runs), with `pclink-yyyyMMdd-HHmmss.json` beside it (transcript, reply, timings in ms, TTS bytes, error or cancelledAt, done).

## Home link

Optional "dial home" link to the owner's home server, see `docs/home-protocol.md` (slice 1: registration and heartbeat; slice 2: turn and log upload, update steering). Off unless `homeUrl` is set.

| config.json key | env | meaning |
|---|---|---|
| `homeUrl` | `SHAMASH_HOME_URL` | server base URL; empty disables the link |
| `homeEnrolToken` | `SHAMASH_HOME_ENROL_TOKEN` | shared token, used only to register; never logged |
| `homeHeartbeatMinutes` | `SHAMASH_HOME_HEARTBEAT_MINUTES` | heartbeat interval, default 5 |

- Key: an ECDSA P-256 key is created on first run at `%LocalAppData%\Shamash\home-key.pem` (PKCS#8 PEM). On Windows the file gets an ACL for the current user only (best effort; the default ACL of the folder is already user, SYSTEM and administrators); on Linux mode 0600. The private key never leaves the PC.
- Registration: when no `%LocalAppData%\Shamash\home-client.json` exists, `POST /api/register` sends the enrolment token, the public key (SubjectPublicKeyInfo PEM), the machine name and the service version; the returned `clientId` is stored in `home-client.json`. A `401` on a heartbeat deletes it and registers again once.
- Heartbeat: right after startup and then every `homeHeartbeatMinutes`, signed (`X-Shamash-Client`, `-Timestamp`, `-Signature`). Sent: version, uptime, health, last turn time, turns today, token usage today (resets at local midnight), last turn error, machine name, OS description and whether Word is registered; `phone` is `{lastSeenAt, batteryLevel, charging}` from the battery the phone reports in its link `hello` (`null` until a hello carries one). The reply's `desiredVersion` and `hold` are kept in the status window ("Home:" line).
- Turn upload (slice 2, **built, not verified on the VM**): `LinkServer` hands every finished turn (turnId = the per-turn file's base name, its time, the JSON) to an in-memory queue (`HomeUploadQueue`, 500 at most, oldest dropped). It is drained right after each successful heartbeat and also ~10 s after a turn, as `POST /api/turns` batches of up to 50; on any failure the items stay queued and go with the next attempt. Nothing is persisted: a restart loses the unsent turns (the files under `pclink/` remain).
- Log upload (slice 2, **built, not verified on the VM**): after each heartbeat, the lines logged since the last upload (from the in-memory log ring, last 400 lines) go to `POST /api/logs`, at most 500 per call: Warning and above, plus Information lines starting `turn:`, `tool:`, `llm usage:` or `missing feature:` (`HomeLogUpload.Wants`).
- Steering (slice 2, **built, not verified on the VM**): `hold` true makes `UpdateService` skip every update check (periodic or triggered) and the status line shows HOLD. Command `check_update` wakes the update loop for an immediate check. Command `restart` is **not supported**: Velopack only restarts as part of applying an update, so the PC logs the warning "restart requested; not supported" and skips it. `desiredVersion` is only recorded and shown; pinning is not implemented (Velopack updates to the latest GitHub release). Each command arrives once.
- The status window's "Home:" line shows the queued turn count and the time of the last upload.
- Failures are retried with backoff (30 s doubling to 30 min) and logged once per ok/failed change; the link never blocks or fails a voice turn.

Verified 2026-11-27 evening against the server container (`home/`, `podman build`) on this host: the VM service (0.1.22, then 0.1.23) generated its key, registered, and its signed heartbeats appear on the status page (version, health, uptime, machine info, turns today). From the Windows guest the host is reachable at the podman host alias `169.254.1.2`, not the LAN address (the guest shares it with the host). The VM's clock was two hours off, so every heartbeat was `401` until the clock-skew handling landed: with the clock deliberately shifted again, the server answered one `stale timestamp` and the retried heartbeat with the corrected timestamp was accepted. The Athena side (compose entry, secret template, Caddy sites) is prepared in the owner's NixOS config and deployed by the owner.

## Update flow

1. `Shamash.Pc-win-Setup.exe` is a one-click, per-user installer: it installs under `%LocalAppData%\Shamash.Pc` without admin rights or prompts and creates Desktop and Start Menu shortcuts. Since 0.1.7 the Velopack after-install hook (`StartupTask.cs`) also registers a logon scheduled task "Shamash.Pc" for the installing user (no execution time limit, least privilege, runs the stub from the install root) and starts it; the before-uninstall hook deletes the task. Hooks log to `%LocalAppData%\Shamash\logs\install.log`. The app holds a per-user named mutex, so a second start (installer plus task, or a task start during an update restart) exits at once.
2. The installed app checks GitHub Releases at startup and every 6 hours. A found update is downloaded in the background.
3. Once inside the update window the app calls `ApplyUpdatesAndRestart`. Velopack keeps the previous version in its own folder and switches the `current` folder to the new one.
4. Releasing: bump `SHAMASH_VERSION_NAME` (same value as the APK release), run the phone `tools/release.sh --publish`, then `pc/build.sh publish`, `pack`, `upload`. Each version needs a new, higher version number.

## Verified (2026-11-25, on Linux in podman)

- `pc/build.sh test`: 20 tests passed.
- `pc/build.sh publish` produces a win-x64 single-file `Shamash.Pc.exe` (about 76 MB).
- `pc/build.sh pack` works on Linux: vpk 1.2.161 logs "Directive enabled for cross-compiling from Linux (current os) to Windows" and produces `Shamash.Pc-win-Setup.exe`, `Shamash.Pc-0.1.0-full.nupkg`, `Shamash.Pc-win-Portable.zip`, `RELEASES`, `releases.win.json`, `assets.win.json`.
- `pc/build.sh upload` with `--merge` attached `Shamash.Pc-win-Setup.exe`, `Shamash.Pc-0.1.0-full.nupkg`, `Shamash.Pc-win-Portable.zip`, `releases.win.json` and `RELEASES` to the existing release <https://github.com/BackSlasher/shamash/releases/tag/v0.1.0>, next to the phone APK.

## Verified on Windows 11 (2026-11-25, in the test VM from `tools/winvm/`)

- `Setup.exe --silent`, run from an SSH session as the normal user, exits 0 with no prompt and installs to `%LocalAppData%\Shamash.Pc` (`Shamash.Pc.exe` stub, `Update.exe`, `current\`, `packages\`).
- Started by a logon scheduled task with no execution time limit, the 0.1.0 app logged its heartbeat and "No update available" against the real repo within 2 s.
- With v0.1.1 published and the app restarted: "Update available: 0.1.1", "Update 0.1.1 downloaded" 4 s later (the full 37 MB package, not the 7.5 KB delta vpk had built). After a restart with `SHAMASH_UPDATE_NOW=1` the process came back as 0.1.1 from `current\`, `sq.version` 0.1.1.
- With v0.1.2 published and the app restarted without the override at 15:03 local: downloaded, then "Outside the update window, holding update 0.1.2 until 03:00".
- Fully unattended path: with 0.1.2 running under `SHAMASH_UPDATE_NOW=1` and `SHAMASH_UPDATE_CHECK_MINUTES=1`, v0.1.3 was published and nothing touched the VM. The log shows "Update available: 0.1.3" at 15:07:48, downloaded at :50, "Applying update 0.1.3 and restarting", and the new process heartbeating as 0.1.3 at 15:07:55, 7 s after detection. The scheduled task stays Ready throughout because Update.exe relaunches the app outside the task.
- The log file is `%LocalAppData%\Shamash\logs\shamash-<yyyyMMdd>.log`.

## Verified end to end with the phone (2026-11-26)

- v0.1.4 (the first build with the link server) was published while the VM app ran 0.1.3 with `SHAMASH_UPDATE_NOW=1` and one-minute checks: "Applying update 0.1.4 and restarting" at 09:55:55, "link: listening on http://0.0.0.0:8777" from the 0.1.4 process at 09:56:01. The inbound port rule had been added beforehand with the PowerShell line below (Private profile, which is what the VM's network is). Kestrel bound 0.0.0.0:8777 as the normal user with no URL ACL.
- The Pixel 7 Pro (debug build, on the same Wi-Fi) ran the probe against the VM through the host's published port: 100 frames, 100 acks, RTT min/median/max/p95 10/19/137/35 ms, no gaps. The VM logged the hello with the phone's real LAN address and wrote `pclink-20261126-095707.wav`, 10.00 s of 16 kHz mono, which held real room audio (nobody spoke; RMS 86, peak 525).

## Verified on Windows 11 with real ElevenLabs calls (2026-11-26, v0.1.6, `llm=echo`)

- Night window, hands off: the service started 20:51 on 0.1.5 with the window set to 20:59-21:09 and one-minute checks, found v0.1.6 at 20:53:42, logged "holding update 0.1.6 until 20:59", and applied it at 21:03:46 (the first 5-minute window poll after 20:59) with nobody touching the VM. Note for the real deployment: Velopack also applies an already-downloaded update at the next app start regardless of the window, seen twice when the task was restarted.
- `config.json` under `%LocalAppData%\Shamash` was read and logged as `config: elevenLabsKey=true anthropicApiKey=false llm=echo`.
- `tools/pclink/client.sh` turn with a 7 s synthetic Hebrew sentence: `transcript` arrived 1.4 s after `end` with the exact text ("שלום. תמצא בבקשה את המאמר על ירושלים בתקופה העות'מאנית ותעביר אותו לאנגלית. תודה."), the echo reply followed, TTS first byte 0.3 s later, 7 s of 16 kHz reply audio streamed back, `done` at 2.5 s. With a 12 s noisy sample STT took 6.9 s and TTS first byte 0.9 s. So the ElevenLabs request shapes are confirmed.
- A silent 3.7 s turn from the Pixel in link mode: empty transcript in 0.8 s, `done`, phone back to idle.

## Verified: first real Claude turns (2026-11-27 07:21, v0.1.6, `llm=claude`)

- `config.json` regenerated with `tools/pc-config.py <out> --llm claude`, copied over the shared folder, service restarted: `config: elevenLabsKey=true anthropicApiKey=true llm=claude`.
- Same 7 s Hebrew sentence (a request to find an article about Ottoman Jerusalem and translate it to English). Claude answered in Hebrew within the system prompt's one-to-three sentences, asking which article and whether the author or title is known. Timings from the service log: STT 1459 ms, Claude 2443 ms, TTS first byte 952 ms, 10.2 s of reply audio, `done` at 5835 ms.
- The same sentence sent again as a second turn: the reply started with "sorry, maybe I was not clear" and narrowed the question (is it the user's own article saved on this computer), so the per-process history reaches the model. STT 988 ms, Claude 2340 ms, TTS first byte 191 ms, `done` at 4475 ms.
- Nothing from the key, the prompt or the reply is logged; only lengths and timings.

## Verified: installer registers the logon task, uninstall removes it (2026-11-26 23:44, local 0.1.7 pack)

- The VM was restored to the `win11-base` snapshot (`tools/winvm/snapshot.sh`, no Shamash on it), then `Setup.exe --silent` from the shared folder: exit 0; `install.log` shows `after-install 0.1.7`, `schtasks /Create` SUCCESS, `/Run` SUCCESS; `Get-ScheduledTask` reports Ready, `ExecutionTimeLimit PT0S`, a logon trigger; exactly one `current\Shamash.Pc.exe` process; the service logged "No update available" 7 s after the installer returned.
- `Update.exe --uninstall --silent`: exit 0, `before-uninstall 0.1.7`, `schtasks /Delete` SUCCESS, task gone. Velopack leaves `Update.exe` itself and `%LocalAppData%\Shamash` (config and logs) behind; the data folder is intentional so a reinstall keeps its config.

## Verified: 0.1.8 tray host (2026-11-27, clean `win11-base` VM, local pack)

- `Setup.exe --silent` exit 0; task Ready; exactly one `Shamash.Pc` process, SessionId 1; no console window on the desktop (screenshot).
- Config line `elevenLabsKey=true anthropicApiKey=true llm=claude` and `link: listening on http://0.0.0.0:8777` in the log.
- One conversation turn from the host (`--fast`): transcript at 6.3 s, reply at 8.6 s, done at 10.0 s.
- `Update.exe --uninstall --silent` removed the task and the process; reinstall worked.
- Icon promotion (fresh restore, 40 s after install): `NotifyIconSettings` entry for `current\Shamash.Pc.exe` has `IsPromoted=1`; log shows `tray: promoted icon` 1.1 s after `link: listening`; the green disc is on the taskbar between the chevron and the cloud icon. A second turn: transcript 1.4 s, reply 3.8 s, done 5.1 s.

## Verified: first tool-using turn (2026-11-27, 0.1.9 on the `win11-seeded` VM)

- VM restored from `win11-seeded` (58-file Hebrew/English corpus in Documents and Desktop, no Office, so the index holds docx file names but not docx bodies), config with both keys, `Setup.exe --silent`, 90 s wait for the indexer after boot.
- The 7 s test sentence (find the article about Ottoman Jerusalem and translate it): Claude called `search_documents` once with the query `ירושלים עות'מאנית` (prefix letter dropped as the tool description asks), which answered in 1.2 s with 844 chars of hits. Reply (Hebrew): it found two matching files, the survey in the מאמרים folder and a newer draft in Desktop stuff, said translation is not available yet, and offered to read or summarise one. Timings: STT 1.3 s, model incl. tool 5.0 s, TTS first byte 0.3 s, done at 8.4 s; 21 s of reply audio.
- The per-turn JSON in `%LocalAppData%\Shamash\pclink` records `toolCalls` (name, input, ms, result chars).

## Verified: steps 2-5 of the task order (2026-11-27, 0.1.10 on the `win11-seeded` VM, text turns)

Nine scripted turns through `tools/pclink/client.sh --text` with `allowTextTurns` on, real Claude and ElevenLabs. Tool calls as recorded in the turn JSON; screenshots checked after each turn.

- "Show me the letter to the municipality in the file manager": `search_documents` then `show_in_explorer`; an Explorer window opened on the מכתבים folder with the file selected. Reply also mentioned the duplicates and offered to organise.
- "Open the file Jerusalem Ottoman period notes": `search_documents`, `open_file`; Edge opened the PDF (the VM has no Word, so PDFs and text files are the openable types there).
- "Is it open now?": `is_document_open` answered in 2 ms (window title match); reply: yes, in Edge.
- "Bring it to the front": `focus_document` 197 ms; Edge came in front of the Explorer window.
- "Organise the articles folder": `find_duplicates` 419 ms; the plan file opened in Notepad (4 groups, 5 files, one exact copy and three older versions from ישנים); the reply described it in three sentences and asked whether to proceed.
- "Yes, do it": the model re-ran `find_duplicates` (plan p2) and `apply_plan`; 5 files moved into מאמרים\כפולים, 5 lines in `moves.log`, nothing deleted.
- "Undo it": `undo_plan`; כפולים empty afterwards. The model first searched for and read the plan file, which was unnecessary but harmless.
- "Translate the Ottoman Jerusalem survey to English": `translate_document` 5.6 s; `ירושלים בתקופה העות'מאנית - סקירה (English).docx` written next to the original (37 KB), original untouched; reply named the folder and offered to open it.
- "Read me the first two paragraphs of the translated file": `read_document` on the new file; the English text came back coherent (Sharia court records, the 1517 conquest, Istanbul archives), so the OpenXML rewrite kept paragraph order and the translation is real.

Turn times with one tool call were 3-8 s; the translation turn 11 s.

## Verified: a piecemeal sorting sitting (2026-11-28 00:35, 0.1.11 on the `win11-seeded` VM, text turns)

- "Let's sort the Desktop stuff folder, target the folders in Documents": the model tried `target_root` as מסמכים, Documents and a guessed path, all refused (the tool only takes a path under a root), then started with the default target (the Desktop root). Fix pending: accept a root by its folder name or Hebrew display name.
- The session found 9 files. For each one `sort_folder_next` returned the excerpt and the evidence; the first three all had byte-identical copies in other folders (משפחה, the Desktop), so the proposals were "identical copy exists in X, move this one to כפולים?".
- "No, skip" -> `sort_folder_skip`; "Yes" -> `sort_folder_move` to כפולים (journal line `s1#1`); "Put it back" -> `undo_last_move` restored it (journal line `undo:s1#1`); "Enough for today" -> no tool call, reply said it can resume from the same file. Folder count back to 9, `sort-sessions.json` persisted (131 KB with the hash/sketch cache).
- Each turn 3.5-8.5 s; the model calls `sort_folder_status` before acting on most turns, which is harmless.

## Verified: Office tools on the win11-office VM (2026-11-27 00:50-00:56, 0.1.12, text turns)

- VM: `win11-office` snapshot = seeded corpus + Office 2016 Pro Plus 64-bit (Word, Excel, Outlook) + an Outlook profile over a maintainer-supplied PST, unactivated in the grace period, no key. After the unattended Office setup, PowerShell could not start ("Starting the CLR failed with HRESULT 80004005"), which broke every SSH session until the VM was restarted.
- "Explain briefly what a named legacy file is about": `search_documents` then `read_document` on a real legacy `.doc` (806 KB) from a private local sample via Word COM in 2256 ms, 4031 chars; the Hebrew summary matched the document (checked by the maintainer against the private file). Turn 10 s.
- "Open the file Reading list on the Desktop": `open_file` launched Word (no activation or first-run dialog); the reply noted a same-size copy in another folder.
- "Is Reading list open now?": `is_document_open` matched the Word window; the model also noted it cannot tell two same-named copies apart.
- "Close the document Reading list": `close_document` reported no unsaved changes and closed it (114 ms).
- "Translate the letter to the municipality to English" then "open the translation": `translate_document` 2.6 s, the new file opened in Word with the title, three headings and body text preserved in English; defect seen: trailing periods rendered at the line start because run-level w:rtl was kept (fixed in the next build, see TranslateTools).

## Verified: Outlook mail tools on the win11-office VM (2026-11-27 01:50-02:05, 0.1.12, a private PST)

- Outlook 2016 profile "Shamash" holding only a private PST (~2k inbox items), created as described in `tools/winvm/README.md` (PRF import plus registry path, wizard clicked through once in the interactive session). The service starts Outlook itself through COM when needed.
- First attempt: `list_recent_mail` hit the 25 s timeout and the reply told the user an Outlook window was waiting on screen. It was the object-model guard ("A program is trying to access e-mail address information"), raised because Defender's signatures were a day old. After `Update-MpSignature` (signature age 0) the same call answered in 271 ms with no prompt. After the next VM reboot the prompt came back although Defender reported signature age 0, so the trigger is Security Center not yet reporting the antivirus right after boot, not the signature date; expect the same on his PC for a few minutes after a restart (the tool times out after 25 s and the reply tells him to look for an Outlook window). On the VM the policy fallback is now applied at deploy time (`HKCU\Software\Policies\Microsoft\Office\16.0\Outlook\Security` ObjectModelGuard=2, AdminSecurityMode=3), after which the same call took 251 ms straight after a reboot. Not applied on his PC unless the prompt proves recurrent there.
- "Do I have new mail? Tell me about the last three": three real unread messages summarised in Hebrew; two were flagged as likely phishing.
- "Read me the second one": `read_mail` 45 ms, 5.4 K chars; a faithful summary of an airline promotion.
- "Reply to them: thanks, not interested, remove me from the list": `draft_reply` 1.1 s; the draft was read back and the model asked whether to send (it also suggested the unsubscribe link instead). "No, don't send, leave it in Drafts": no tool call; Drafts count rose, Outbox stayed at 0.
- Not exercised: `send_draft` (no sending account on the VM) and the quoting of the original in the draft body (not inspected).

## Verified: index-only search, exact document identity and dialog detection (2026-11-27 10:28-10:32, 0.1.13 on the win11-office VM, text turns)

- 0.1.13 installed over the running 0.1.12 with Setup.exe --silent; service back in ~1 s, "No update available".
- A real 7 s Hebrew audio recording (tools/fixtures/hebrew_v3.wav, asking to find the article about Ottoman Jerusalem and translate it) through the full chain: transcript correct, `search_documents` found two matches, the reply asked which one to translate (reply at 17.7 s from the start of the 7 s upload, 14.5 s of reply audio); the follow-up "the survey" ran `translate_document` and answered in 10 s. Only the phone's microphone and speaker remain unverified end to end.
- With the Windows Search service stopped (`Stop-Service WSearch`): "find the letter to the municipality" -> `search_documents` returned the index error in 78 ms and the reply was "אינדקס החיפוש לא זמין כרגע, אז אני לא יכול לחפש את המכתב לעירייה. נסה לבקש ממני שוב בעוד דקה." No fallback search ran. Service started again; the first query after ~70 s worked.
- "Where are files named Reading list": two copies (Desktop root and Desktop stuff). "Open the one in the main folder": `open_file` on the Desktop copy, Word opened it. "Is the Reading list in Desktop stuff open now?": `is_document_open` (273 ms) answered `not open; a different file with the same name is open: <Desktop path>`; the reply said so and corrected its own earlier wording. "Which Reading list is open?": exact match in 17 ms, `open in Word: <path>`.
- Word's classic Open dialog raised with Ctrl+F12 in the interactive session; "Is Word showing something on screen? What does it say?" -> `describe_office_dialogs` (6 ms) read the dialog; the reply named the "Open" window, said the file-name field was empty and advised pressing Cancel. Screenshot confirmed the dialog.
- Defect seen and fixed in this build: with two roots the folder column did not say which root a file was in (a Desktop file was reported as being in the main Documents folder). The column now starts with the root name and the path tools accept `<rootname>/relative/name`.

## Regression suite

`tools/winvm/scenarios/` replays phone-session scenarios (find/open/close, translate, mail listing, duplicates ask/yes/undo, index down, same-name identity, dialog) as text turns on the win11-office VM and asserts tool sequences, result prefixes and end state, never wording. Run `tools/winvm/scenarios/run.sh [--restore] [--install <Setup.exe>] [--only <name>]` after deploying `config.json`; see its README. The per-turn JSON carries `resultPrefix` (first 80 characters of each tool result) for these checks.

First full run (2026-11-27 12:00, 0.1.16 installed by the runner, version read back from the log): 8/8 scenarios passed. The run before it (0.1.15 build still at 0.1.14 because the old install step silently did nothing, and no history reset) failed 3: two on a missing `resultPrefix` field, one because the model refused to redo a dedup plan it had just been told "no" about in the previous scenario. Both runner defects are fixed.

Runs on 2026-11-27 afternoon (releases 0.1.17 to 0.1.21): each run found something real before reaching 8/8 on a freshly restored VM with 0.1.21: the ElevenLabs quota (suite now silent by default), a hidden window-less Word left by an earlier run (clean step kills those), Word not quitting with its last document (fixed), the search index listing deleted files (filtered), an empty final model reply (one retry), the sorting undo being picked for a dedup plan (guards and descriptions), plan ids not surviving between turns (apply_plan/undo_plan default to the latest plan), and the dedup scenario setup destroying the corpus's duplicates (restores them now).

Run 2026-11-28 (0.1.24, escape hatches, no restore): the new scenarios `missing-feature` and `settings-escape` passed on the first try. Full suite 8/10 on the first pass: `dedup-ask-then-no` failed once (the model searched documents instead of calling `find_duplicates`; passed on rerun, a flake) and `word-dialog` failed twice in a row: asked "is Word showing something on the screen?" with no Word running, the model now calls `look_at_screen` instead of `describe_office_dialogs`. The answer was correct, but the new screen tool competes with the Office dialog tool for that question.

Run 2026-11-28 (0.1.25, build cac39739, no restore): `word-review` passed 1/1 after three harness fixes (the setup line used `&` on a UNC script, which the execution policy blocks, so Word never opened; one answer per turn because the model only answers revisions it has read aloud; the close turn names the lost answers because the model asks before discarding review work), `word-picture` passed 1/1 on the first run, `word-copy-paragraph` failed 0/1 on the InvalidComObjectException of this build (fixed in the source, needs redeploy). Direct cases (R1 to R15, copy cases 9 to 13, picture cases 14 to 18, recovery C4/C7/C8) are in `tools/winvm/harness/word-edit-cases.md`; `check.py` now fails a scenario whose setup line printed `ERROR` or was blocked.

## Not verified

- The dialog text of Office 2013+ `NUIDialog` alerts (DirectUI; only the title may be readable).
- The effect of `AutomationSecurity`/`ConfirmConversions` on a real macro or conversion prompt.
- Excel document identity (no Excel document was opened).
- Which clause of the index query matched a given hit (name, CONTAINS or FREETEXT); the result text does not record it.
- A spoken turn from the phone (someone talking into it) and the phone playing the reply audio through its speaker; only a silent turn ran from the phone.
- Shortcuts, and SmartScreen on a browser-downloaded Setup.exe (the VM copy came over the shared folder).
- The firewall prompt itself: the rule was added before the service first listened.
- Why the delta package is not used: Velopack downloaded the full package every time, though vpk builds 7 KB deltas.
- Release assets are unsigned; Windows SmartScreen may warn on first run.
- vpk warns that `VelopackApp.Run()` "does not look like your application's entry point" because of top-level async `Main`. Updates work regardless.
