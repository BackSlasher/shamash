# Tasks the PC agent must handle

Owner-maintained. This is the list of things the grandfather will ask Shamash to do on his
computer. Each entry drives what the PC service must be able to touch (apps, folders, accounts)
and what the test VM must have installed and seeded. Add tasks freely; the agent side reads this
file when deciding what tooling to build next and what to put in the VM snapshot.

Status values: `idea`, `specified` (enough detail to build), `tooling` (PC-side tool exists),
`tested-vm` (passes on the Windows VM with seeded data), `tested-real` (worked on his PC).

## The environment (fill in)

| Item | Value |
|---|---|
| Windows version on his PC | ? (10 / 11, Home / Pro) |
| Office | Office 2016 (installer ISO kept on D:\OFFICE2016; exact edition/licence to confirm); ABBYY FineReader 10/14/15 for OCR (owner's saba inventory, 2026-11-12) |
| Email | Classic (full) Outlook desktop with two accounts: university mail over POP3, Gmail over IMAP (owner, 2026-11-27) |
| Where his documents live | Live work is C:\Users\User\Desktop (~3,300 files, 9.6 GiB, project folders in Hebrew); C:\Users\User\Documents is small (~140 files: loose docs, scans, FineReader projects, a live PST); the archive is D:\Old D\Data\Documents (63k files, 221 GiB) and D:\Old D\Data\Desktop (14k files); Desktop/Documents/D: are also synced or listed on Google Drive. No OneDrive/Dropbox. Exclude D:\OFFICE2016, ABBYY trees, Outlook Files, D:\Old D\Users\<old user profile> (duplicates). |
| Document formats he uses | Legacy .doc (9,348) outnumbers .docx (8,109); .rtf 2,422; .pdf 19,819 (mostly scanned books, 185 GiB; 236 of 7,206 unique documents have no text layer); .tif/.jpg scans; .txt 372; almost no .xlsx/.pptx. Word ~$ lock files present on the Desktop. |
| Languages in his documents | Hebrew and English mixed in the same documents and file names (15% of names contain Hebrew); English summaries exist in the inventory |
| Printer / scanner | ? |
| Browser and default apps | ? |
| Other apps he relies on | ABBYY FineReader, AnyDesk, HP printing/fax, classic Outlook (PSTs, an ISP account) |
| Who else uses the PC | ? |

## Task template

```
### <short name>                                   status: idea
Ask (Hebrew, as he would say it): "..."
Also phrased as: "...", "..."
What should happen: <steps the agent takes, in order>
Touches: <apps / folders / accounts>
Must never: <hard limits, e.g. delete, send without confirmation>
Spoken result: <what he hears when it is done / when it fails>
Confirmation needed: <none / before acting / before sending>
Seed data for the VM: <files or mail the test VM needs to have>
Done when: <observable check>
```

## Tasks

### Find a document by what it is about                 status: tested-vm
Ask: "תמצא בבקשה את המאמר על ירושלים בתקופה העות'מאנית"
Also phrased as: "תפתח את הקובץ שנקרא מכתב לעירייה", "המסמך שעבדתי עליו אתמול", "הקובץ שבשולחן העבודה"
What should happen: search his document folders by content (not just file name), rank, open the best match in Word, say what was found and where. The Windows Search query covers name, content, folder and modified date; the document reader is used to confirm candidates.
Touches: document folders (see environment), Word.
Must never: move or modify the file.
Spoken result: "מצאתי את X בתיקייה Y ופתחתי אותו" or "לא מצאתי, אולי תזכור מילה מהכותרת?".
Confirmation needed: none.
Seed data for the VM: ~50 .docx/.pdf in nested folders, a few on the same subject, Hebrew and English, some duplicates with different names.
Done when: the right file opens on screen.
Owner notes (2026-11-27): agreed. Tooling = a document text reader plus the Windows Search index (OS-maintained, never stale; query via the Search.CollatorDSO OLE DB provider), with a slow folder walk as fallback for unindexed locations. Claude keeps no index of its own. Verify on his PC: PDF content indexing and which folders are indexed.

### Translate a document                                status: tested-vm
Ask: "תעביר את המאמר הזה לאנגלית"
What should happen: take the open or just-found document, translate it, save a new file beside the original with a language suffix, open it.
Touches: Word, the document's folder.
Must never: overwrite the original.
Spoken result: "תרגמתי ושמרתי בשם X, זה פתוח עכשיו".
Confirmation needed: none.
Seed data for the VM: a few Hebrew and English articles with headings, footnotes, tables.
Done when: a new .docx exists with the translation and formatting kept.
Owner notes (2026-11-27): no cloud-side Office exists; do it locally without Word: read the .docx with OpenXML, translate paragraphs with Claude, write back into the same structure (styles, headings, tables, lists), save as a new file, open it in Word. Flip paragraph direction for Hebrew<->English. PDFs come out as a translated .docx. Complex layouts are the hard part; v1 = paragraphs, headings, tables, lists.
Verified in Word (2026-11-27): headings, styles and text preserved; RTL run flag fix needed and done.

### Organise and deduplicate a folder                   status: tested-vm
Ask: "תסדר לי את התיקייה של המאמרים"
What should happen: find exact and near duplicates, propose a grouping, ask before moving anything, then move (never delete) into subfolders or a `duplicates` folder.
Touches: document folders.
Must never: delete; move without saying what and where.
Spoken result: a short summary of what was moved.
Confirmation needed: before acting.
Seed data for the VM: the folder above with exact copies, older versions, "Copy of" files.
Done when: no file lost (count before = count after), duplicates grouped.
Owner notes (2026-11-27): describing a rearrangement by voice is not realistic. Design: he triggers, the agent proposes a plan (spoken 3-sentence summary + plain list on the PC screen), he approves, moves only, never deletes, with a move journal for undo. First slice: deduplication alone.
Owner notes (2026-11-27, later): the interesting files are .docx; the numerous .doc/.rtf/.pdf are cold storage. The saba consolidation plan is good, but he gave up on presenting it in full for approval, so the end result is Shamash consolidating the "salad" folders (סלט למיון וסידור / לסידור piles) piecemeal, a few files per sitting, with the grandfather saying yes or no by voice (see "Sort a messy folder piecemeal"). He may find an Office ISO for the VM. Guards: `find_duplicates` and the sorting tools refuse a folder with more than 3000 files (`maxOrganizeFiles`); `~$` lock files and `.lnk` shortcuts are skipped everywhere; default document roots stay Desktop and Documents, D: must be added explicitly.

### Help with email                                     status: tested-vm
Ask: "יש לי מייל חדש?", "תקרא לי את המייל מדני", "תענה לו שאני מסכים"
What should happen: read recent mail aloud (sender, subject, gist), draft replies, send only after he confirms.
Touches: his email client / account (see environment).
Must never: send without an explicit spoken confirmation; delete mail.
Spoken result: the draft read back, then "לשלוח?".
Confirmation needed: before sending.
Seed data for the VM: a test mailbox with a handful of Hebrew and English threads.
Done when: a reply arrives at a test address.
Owner notes (2026-11-27): interesting but low priority (Outlook COM is high complexity). If his mail is Gmail/IMAP the PC can read and send directly without Outlook; only Outlook desktop on Exchange needs COM or Graph. Decide after the environment table is filled.
Design note (2026-11-27): both accounts live in classic Outlook, and the POP3 account's only complete copy may be Outlook's local store, so the tools must drive Outlook itself (COM automation of the running Outlook in his session: enumerate the two stores, read Inbox items, create replies, send). Reading the Gmail account directly over IMAP would cover only half. Outlook's object-model guard may prompt on send or address access unless antivirus is current; verify on his PC. Cannot be tested on the VM without Office/Outlook; a throwaway Microsoft 365 trial or his own licence on the VM is the way to test it. Status is tooling until then.
Tooling: `list_mail_accounts`, `list_recent_mail`, `read_mail`, `draft_reply` (saves to Drafts), `send_draft` (only ids made by draft_reply) over Outlook COM in `OutlookTools.cs`; the prompt requires reading the draft back and a yes in the next turn; untested against real Outlook.

### Show a file in Explorer                             status: tested-vm
Ask: "תראה לי איפה הקובץ הזה נמצא", "תפתח לי את התיקייה של הקובץ"
Also phrased as: "באיזו תיקייה זה?"
What should happen: run `explorer.exe /select,"<full path>"`, which opens a new Explorer window at the folder with the file selected; say which folder it is in.
Touches: Explorer only.
Must never: modify the file.
Spoken result: "פתחתי את התיקייה Y, הקובץ מסומן".
Confirmation needed: none.
Seed data for the VM: any file.
Done when: an Explorer window with the file selected is in front.

### Open a file in its program                          status: tested-vm
Ask: "תפתח את הקובץ הזה", "תפתח אותו בוורד"
Also phrased as: "תפתח לי את המכתב"
What should happen: ShellExecute with the default verb (the file's registered app: Word for .docx, the PDF viewer for .pdf), then confirm aloud. If the file is already open, focus it instead (see "Bring a document to the front").
Touches: the default app for the type.
Must never: pick an app other than the default without asking.
Spoken result: "פתחתי את X".
Confirmation needed: none.
Seed data for the VM: a .docx, a .pdf, an .xlsx.
Done when: the document is open and in front.

### Is a document open?                                 status: tested-vm
Ask: "המכתב לעירייה פתוח עכשיו?"
Also phrased as: "האם הקובץ הזה פתוח?", "אני כבר עובד על המסמך הזה?"
What should happen: two checks, the first that answers wins. (a) Enumerate top-level windows and match the file name in the title (Word titles are "<name> - Word"). (b) The Windows Restart Manager API (RmStartSession / RmRegisterResources / RmGetList) tells which process holds the file; this works for any app without Office automation. If Word automation is available (depends on the Office edition, see environment table) also check Word's Documents collection by full path. Answer in Hebrew: open in Word / not open.
Touches: window list, Restart Manager, optionally Word.
Must never: open or close anything.
Spoken result: "כן, הוא פתוח בוורד" / "לא, הוא לא פתוח".
Confirmation needed: none.
Seed data for the VM: one document open in Word, one open in another app (PDF viewer), one closed.
Done when: correct answer for open, closed, and open-in-another-app cases.

### Bring a document to the front                       status: tested-vm
Ask: "תעביר אותי למסמך של המכתב", "תביא לי את המסמך לקדמה"
Also phrased as: "תעבור למסמך הפתוח"
What should happen: find its window (as in "Is a document open?"), restore it if minimized, bring it to the foreground with SetForegroundWindow. Windows refuses focus stealing from a background process, so use the AllowSetForegroundWindow / attach-thread-input or minimize-restore workaround (to be verified on the VM). With Word automation: Documents(path).Activate() plus Application.Activate.
Touches: window list, optionally Word.
Must never: open a second copy.
Spoken result: "המסמך מולך".
Confirmation needed: none.
Seed data for the VM: two documents open, the target behind another window and one minimized.
Done when: the document's window is the active window.

### Close a document                                    status: tested-vm
Ask: "תסגור את המסמך", "תסגור את המכתב ותשמור"
Also phrased as: "תסגור בלי לשמור"
What should happen: needs the Office edition decision. For Word/Excel with automation available: find the document by path and read its Saved flag. If there are unsaved changes ask aloud "יש שינויים שלא נשמרו, לשמור?" and close with SaveChanges accordingly; if no changes, close without asking. Without automation (other apps): only a WM_CLOSE to the window, which lets the app show its own save prompt, and tell him to answer on screen.
Touches: Word/Excel via automation, or the app's window.
Must never: discard unsaved changes without his spoken yes.
Spoken result: "שמרתי וסגרתי" / "סגרתי בלי לשמור" / "ענה על החלון שבמסך".
Confirmation needed: before discarding.
Seed data for the VM: an open dirty document and an open clean one.
Done when: dirty and clean documents both close correctly.
Owner environment (2026-11-27): Office 2016 is on his PC, so Word automation is available; blocked only on an Office for the VM.
Tooling: `close_document` (Word COM by path, Saved flag, save ask/yes/no; WM_CLOSE fallback for other windows); unit-tested only for guards, needs an Office VM or his PC for the real test.

### Sort a messy folder piecemeal                       status: tested-vm
Ask: "בוא נסדר את התיקייה סלט"
Also phrased as: "תעזור לי לסדר את הסלט למיון", "נמשיך לסדר", "כן", "לא, דלג", "תחזיר את זה", "מספיק להיום"
What should happen: `sort_folder_start` creates or resumes a session for the folder and lists the existing destination folders (under the Desktop). For each file `sort_folder_next` returns the newest unsorted file with a text excerpt and evidence from the other folders under the document roots: identical copies elsewhere (SHA-256), similar versions elsewhere (word 3-gram similarity >= 0.6), and the two folders whose documents are most similar in content. Shamash describes the file in one sentence and asks about ONE destination: an identical copy exists -> "move this copy to כפולים?"; a similar version exists in folder X -> "move it there?"; it looks like the documents in folder Y -> "move it there?"; otherwise a new folder. "כן" -> `sort_folder_move` (the file goes to the folder, its exact duplicates in the salad folder go to כפולים); "לא" / "דלג" -> `sort_folder_skip` (asked again next sitting, unless he says never); "תחזיר את זה" -> `undo_last_move`; after every 5 files Shamash says how many remain and asks whether to continue; "מספיק להיום" stops and says the session can be resumed. Sessions persist in `%LocalAppData%\Shamash\sort-sessions.json`.
Touches: the salad folder and the Desktop folders, the move journal.
Must never: delete or overwrite; move without a spoken yes; touch .doc/.rtf/.pdf content (they are listed by name and date only, no excerpt for .doc/.rtf).
Spoken result: "זה מכתב לעירייה מלפני שנה, להעביר לתיקייה מכתבים?" then "הועבר, נשארו 40".
Confirmation needed: before each move.
Seed data for the VM: a salad folder with ~30 mixed files (some exact copies of files in other Desktop folders, a few older versions, .doc and .pdf files) plus 5-6 topic folders with a handful of documents each.
Done when: files land in the right folders by voice, copies end up in כפולים, "תחזיר את זה" restores the last file and its copies, a restart resumes where it stopped.

<!-- Add tasks below. Printing, calendar, reading a PDF aloud, summarising a long document, dictating a letter, renaming files, finding a photo... -->

## Findings from the saba inventory (2026-11-27)

These are facts about the target environment; none of the files themselves are in this repo or in the public VM snapshots.

- Source: `~/projects/saba` holds rclone listings of three Google Drive roots (D:, Desktop, Documents) taken 2026-11-12, a partial mirror, `inventory/index.csv` (6,892 documents with LLM category, summary and canonical path), `dedup.json`, and an unapplied consolidation plan for D: (round one: dedup into a Drive quarantine folder; round two: a new folder structure). Its Python tools (python-docx, olefile, striprtf, pypdf, LibreOffice for .doc) are reusable for text extraction.
- Scale: the current `search_documents` walk cap (2000 files / 10 s) and `find_duplicates` are sized for the live Desktop and Documents, not for D:. Default document roots should be Desktop and Documents; the D: archive should be reachable only through the Windows Search index or the saba `index.csv` canonical paths, never walked, and `find_duplicates` must refuse roots above a file-count threshold.
- Formats: `read_document` cannot read .doc or .rtf today, which are 11,770 of his files. Needed: a .doc reader (Word COM on his PC since Office 2016 is present, with a library fallback) and an .rtf reader; skip `~$` lock files everywhere; PDFs without a text layer need OCR (FineReader is installed) or are reported as "scan, no text".
- Dedup overlap: the saba plan already defines canonical paths and a quarantine policy for D:; Shamash's organise tools must not run on D: until that plan is applied, and should reuse its canonical ordering (live Desktop > Data/Documents/<topic> > Data/Desktop > Copy/OLD/salad paths) when it does.
- Shortcuts: Desktop .lnk files point into D:\Old D; moving targets breaks them (the saba plan tracks this in shortcuts.md).
- Office 2016 means classic Outlook and Word COM exist on his PC, so "Close a document" and email are feasible there; the VM still needs an Office to test them (his Office 2016 installer and key, if the licence allows a second install, is the cheapest route).
- Windows Search on his PC: confirm that D: is in the indexed locations and that the Office filters are present (Office 2016 ships them).

## Suggested order

0. The PC-side tool loop itself: Claude calling tools from the conversation turn, with a spoken confirmation step and a plain on-screen list for plans (prerequisite for everything below).
1. Find a document (Windows Search + document reader): the foundation every other task reuses.
2. Open a file in its program, and Show a file in Explorer: trivial, make "find" immediately useful.
3. Is a document open? and Bring a document to the front: small, window enumeration shared between them.
4. Deduplicate a folder (first slice of Organise): clean spoken summary, moves only.
5. Translate a document (OpenXML, no Word needed).
6. Close a document: waits for the Office edition answer (Word automation).
7. Sort the salad folders piecemeal (sort_folder_* tools): built, VM test pending.
8. Email: last, after the mail client is known.

# Inbox
<!-- drop raw ideas here; they get expanded into the Tasks section -->

