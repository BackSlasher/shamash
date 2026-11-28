# Scenario suite (win11-office VM)

Replays the owner's phone session of 2026-11-28 and the VM tests of 2026-11-27 as text turns against the real PC service and
the real model, and asserts tool sequences and end state. Item 2 of the "Regression harness for Office tools" task.

```
tools/winvm/scenarios/run.sh [--restore] [--install <Setup.exe>] [--expect-version <v>] [--only <name>] [--url ws://127.0.0.1:8777/] [--tts]
```

Turns are sent with `--no-tts` (hello `"noTts": true`, see `docs/link-protocol.md`): the PC skips ElevenLabs synthesis, so the suite spends no
TTS credit; assertions never look at audio. Pass `--tts` to `run.sh` (forwarded to `check.py`) to synthesise audio as in a real turn, for a
manual listen. A turn whose JSON carries `error.stage == "tts"` prints a `WARNING` line and does not fail the scenario; tool and state
assertions are still evaluated. Any other error stage still fails it.

The maintainer runs it; it needs podman (for `tools/pclink/client.sh`), the VM up, and:

1. `--restore` restores snapshot `win11-office` (shutdown, copy, boot) and waits for ssh. That snapshot holds no Shamash and no secrets.
2. The service must be installed with `config.json` deployed (API keys, `allowTextTurns: true`, document roots Desktop and Documents).
   The script does NOT handle config.json: deploy it yourself after a restore. With `--install <Setup.exe>` the script copies the
   installer to `~/vm/shamash-win11/shared/`, runs it over ssh with `--silent` and waits ~75 s for the indexer; do the config deploy
   before running the suite (restore, install by hand or with `--install` alone, deploy config, then run without `--restore`).
3. The Outlook object-model guard policy from `tools/winvm/README.md` must be set for the mail scenario.

4. `--install` copies Setup.exe to `~/vm/shamash-win11/shared/`, then over ssh copies it from `\\host.lan\Data` to `C:\Users\shamash\Setup.exe`
   and runs it with `--silent -Wait` (running the share path directly did not install). After ~75 s the script reads the newest
   `%LocalAppData%\Shamash\logs\*.log` for the last `heartbeat, version X` / `after-install X` and prints `installed version: X`.
   This line is printed on every run, with or without `--install`, so the report says what was tested.
   `--expect-version <v>` (optional) exits 4 before any scenario if the version read back does not start with `<v>`.
5. Before each scenario `check.py` sends `client.sh <url> --reset` (link message `{"type":"reset"}`, allowTextTurns only), which clears the
   service's conversation history, so a "no" in one scenario cannot leak into the next. If the reset does not answer `reset_ok`
   (service too old, allowTextTurns off) the scenario fails at once.

6. Isolation. Besides the history reset, `check.py` runs a "clean desktop" step over ssh before each scenario's `setup`: it calls
   `CloseMainWindow()` on every WINWORD, EXCEL and notepad process, waits up to 8 s (polling every 1 s) for them to exit, then
   Stop-Processes only Notepad windows whose title contains `תוכנית` (plan files). A Word or Excel with a window is never killed (that creates Document
   Recovery entries), but one with no window at all (`MainWindowHandle` 0, which ignores `CloseMainWindow` forever) is Stop-Processed
   and reported as `killed window-less Word pid N`; if Word still has a window (e.g. an unsaved-changes prompt) the step prints `WARNING: Word still running (title: ...)`
   and the scenario goes on. A scenario can skip the step with `"keep_apps": true`. `run.sh` runs the same step once at the very start,
   before `--install` (the installer restarts the service, not Word), so a run never inherits the previous run's windows
   (`check.py --clean-only` does just that).
7. State grace period. Each `expect_state` check is polled up to 10 s (every 2 s) until it matches, and fails only after that, since
   Word takes a few seconds to exit after Quit. The output mentions the wait only when it was needed (`(matched after N s)`).
8. Rule: a scenario that opens something (Word, Excel, Notepad) must close it itself in a final turn (and assert it in `expect_state`);
   the clean step is a safety net, not a substitute.

Exit code is non-zero if any scenario failed; each failure prints the failing assertion.

## Format

One JSON file per scenario in this folder, run in file-name order:

```
{ "name": "...",
  "keep_apps": false,
  "setup": ["<powershell line>", ...],
  "turns": [ { "say": "<hebrew text turn>",
               "expect_tools": ["search_documents", "open_file", "any:translate_document|open_file"],
               "forbid_tools": ["apply_plan"],
               "only_tools": ["search_documents"],
               "expect_result_prefix": {"search_documents": "error:"},
               "max_total_ms": 30000 } ],
  "expect_state": [ { "ps": "<powershell expression>", "equals": "<string>" } ],
  "teardown": ["<powershell line>", ...] }
```

- Every `ps` line runs over `tools/winvm/ssh.sh` (UTF-8 output); `expect_state` compares the trimmed output.
- `expect_tools`: plain names must appear in that order as a subsequence of the turn's tool calls (other calls may sit between).
  An `any:a|b` entry is satisfied when at least one of the listed tools was called anywhere in the turn.
- `forbid_tools`: none of these may be called in the turn. `only_tools` (extension): no tool outside this list may be called.
- `expect_result_prefix`: some call of that tool must have a result starting with the text. The per-turn JSON
  (`%LocalAppData%\Shamash\pclink\*.json`) carries `resultPrefix`, the first 80 characters of each tool result (added with this suite).
- `max_total_ms`: bound on `timingsMs.total` of the turn.
- A turn that reports an `error` in its JSON fails the scenario.
- Turns of a scenario run in one conversation; the history is reset between scenarios (see above), so a scenario's first turn
  must make sense without context. Scenarios that change files clean up in `teardown`.

## Scenarios

| file | what it checks |
| --- | --- |
| `find-open-close` | search without opening; open; is it open; close; no Word window with the letter left |
| `translate-existing` | translate the letter to English and open it; the `*English*.docx` exists in `Documents\מכתבים` (removed again); close the translation, no Word left |
| `mail-list-no-send` | list the three latest mails; never `send_draft` or `draft_reply` |
| `dedup-ask-then-no` | `find_duplicates` on the articles folder, no `apply_plan`; after "no" still none and no `כפולים` folder |
| `dedup-yes-undo` | "yes" applies the plan, "put everything back" calls `undo_plan`; `כפולים` is absent or has no files |
| `index-down` | `WSearch` stopped: `search_documents` returns `error:` and no other tool runs; teardown starts the service, `run.sh` then waits 70 s |
| `same-name-identity` | two `Reading list.docx` (Desktop and `Documents\Desktop stuff`): open one, the other is reported as "not open; a different file", close |
| `word-dialog` | "is Word showing something" calls `describe_office_dialogs` or, with no Word running, `look_at_screen`; never a click or close |
| `missing-feature` | "print the letter" (no tool does that): `report_missing_feature` is called; `missing-features.jsonl` has a fresh last line with a `request` |
| `settings-escape` | Windows Settings opened on the interactive desktop (scheduled task); "a window opened, I don't know what it is" calls `look_at_screen` or `describe_window` and none of close/click/press; "close it" calls `close_window`; no `SystemSettings` process left |
| `word-edit-basics` | copies the seed `מאמר על ירושלים - לעריכה.docx`, `tools/winvm/harness/word-edit.ps1` opens it with the cursor in paragraph 3; "what is open" -> `describe_active_document`, track changes on, 1.5 spacing, undo, close without saving; no Word left. Needs `tools/winvm/harness/` copied to `~/vm/shamash-win11/shared/harness/` and the seed docs in `shared/seed` |
| `word-copy-paragraph` | the seed `ירושלים בתקופה העות'מאנית.docx` is the source, the editing seed is open (cursor in paragraph 3): "copy here the paragraph about the sultan" -> `search_documents` + `copy_paragraph_start`; "no, further down" -> `copy_paragraph_next`; "yes" -> `copy_paragraph_confirm`; close without saving; no Word left |
| `word-picture` | `תמונות` copied into the Pictures folder: "insert the picture of the Kotel here" -> `search_documents` (kind picture), no `insert_picture` yet (two matches); "the winter one" -> `insert_picture`; "let the text flow around" -> `set_picture_wrap`; close without saving; no Word left |

## Opening a window on the interactive desktop

GUI programs started from an ssh session do not appear on the desktop (the ssh session is not the interactive one). A scenario `setup`
line therefore starts the program through a scheduled task, which runs in the logged-on user's session:

```
schtasks /Create /TN shamash-test-open /TR "explorer.exe ms-settings:" /SC ONCE /ST 00:00 /F | Out-Null; schtasks /Run /TN shamash-test-open | Out-Null; Start-Sleep 6
```

(the `/ST` start-time warning is harmless; the Settings process is `SystemSettings`). `teardown` must run
`schtasks /Delete /TN shamash-test-open /F | Out-Null` and Stop-Process any leftover `SystemSettings`. The clean-desktop step of
`check.py` was not extended: `settings-escape` stops a stale `SystemSettings` in its own setup, and no other scenario is affected by it.
The `missing-feature` check does not clear `missing-features.jsonl`; it only requires the last line to be under 10 minutes old.
Assertions on the reply (it must not claim to have printed) are not automated; read the turn record.

## First run

The first run (2026-11-28) had two `expect_result_prefix` failures. Those were the old installed build, which predates `resultPrefix`
(the `--install` that should have updated it silently did nothing). `check.py` now reports `resultPrefix missing (build older than 0.1.15?)`
when the turn JSON has no such field at all.

## Not asserted, and known limits

- Wording of any reply, language, politeness, whether the model asks a clarifying question (only tools and state).
- Tool results beyond an 80-character prefix; tool inputs.
- The model may legitimately call `search_documents` twice or add a harmless read; hence ordered-subsequence matching, never exact
  lists. A turn can still flake when the model decides to ask a question instead of acting; rerun with `--only <name>` before blaming code.
- The Word Open-dialog case is manual: raise it with Ctrl+F12 through `tools/winvm/sendkeys.py` in the interactive session, then ask
  "is Word showing something on screen?" (`word-dialog` only covers the no-dialog path, and passes as long as the tool is called).
- Seed names: the scenarios rely on `tools/seed-docs/gen.py` (`מכתבים/מכתב לעירייה.docx`, `מאמרים/` with duplicates,
  `Desktop stuff/Reading list.docx`). gen.py does not create a Desktop-root `Reading list.docx`; the `win11-office` snapshot's
  Desktop copy (used in the 2026-11-27 verification) must exist for `same-name-identity`.
- Office COM plus forced kills can wedge ssh on this VM (see `tools/winvm/README.md`); teardowns close Word politely and never kill it.
