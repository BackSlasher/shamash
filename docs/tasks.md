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
| Phone power (physical) | The Pixel 7 Pro's USB-C port has a weak grip: cables slide out (owner, 2026-11-28). The phone is meant to sit on a stand, always on, screen lit, so a loose cable means a dead phone within a day. Plan: a Qi wireless charging stand (Pixel 7 Pro charges wirelessly; `KioskPolicy` already keeps the screen on for BATTERY_PLUGGED_WIRELESS) or a cradle that clamps the cable; plus a not-charging watchdog in the app (see Tasks). |

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
Owner notes (2026-11-27): agreed. Tooling = a document text reader plus the Windows Search index (OS-maintained, never stale; query via the Search.CollatorDSO OLE DB provider), index only (owner decision 2026-11-27: no walk or other fallback; if the index is unavailable the tool returns a hard error and Shamash tells the user to try again in a minute). Claude keeps no index of its own. Verify on his PC: PDF content indexing and which folders are indexed.

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

### Editing the open Word document (second priority)

These tasks act on the document he is working in, at the place he put the cursor with the mouse. They come after the finding, sorting and mail tasks above. Every one of them works through the running Word (COM attach only, never start Word) and the `ActiveDocument`.

### Describe the open document                          status: idea
Ask (Hebrew, as he would say it): "מה פתוח עכשיו?"
Also phrased as: "איפה אני במסמך?", "באיזה מסמך אני עובד?"
What should happen: `describe_active_document` attaches to the running Word (never starts one) and reads `ActiveDocument`: name, full path, saved flag, track-changes state, revision count, paragraph count, the paragraph under the cursor (first ~15 words) and the selected text if any. This is the "here" every editing tool below relies on: the cursor he put with the mouse.
Touches: the running Word, read only.
Must never: change anything in the document or the Word session.
Spoken result: "פתוח המסמך X, הסמן בפסקה 3 שמתחילה ב..., מעקב שינויים כבוי, יש שינויים שלא נשמרו".
Confirmation needed: none.
Seed data for the VM: a multi-paragraph .docx open in Word with the cursor placed by script, one variant with a selection and one with tracked changes.
Done when: with a document open and the cursor in paragraph 3, the tool reports that paragraph's opening words.

### Undo the last edit                                  status: idea
Ask (Hebrew, as he would say it): "תבטל את מה שעשית", "תחזיר את זה כמו שהיה"
Also phrased as: "בטל", "תחזיר את המסמך לפני השינויים"
What should happen: `undo_last_edit` calls `ActiveDocument.Undo(n)` (default 1) and says what was undone. Before the FIRST edit of each document in a session, every editing tool takes a backup copy under `%LocalAppData%\Shamash\backups`; `restore_backup` writes `<name> (לפני שמש).docx` next to the original, never overwriting anything.
Touches: the active document, the backups folder.
Must never: overwrite the original or an existing file on restore; delete backups during the session.
Spoken result: "ביטלתי". Every editing tool below must mention "אפשר לבטל" in its spoken result.
Confirmation needed: none for undo; none for restore (it only adds a file).
Seed data for the VM: a document edited by two tools in a row.
Done when: undo reverts the last edit only, and restore creates the "(לפני שמש)" copy with the original content without touching the original.

### Paragraph spacing                                   status: idea
Ask (Hebrew, as he would say it): "תעשה רווח של שורה וחצי"
Also phrased as: "רווח כפול בכל המסמך", "רווח אחרי פסקה", "תחזיר לרווח רגיל"
What should happen: scope is the selection if any, else the paragraph under the cursor, else ask "בכל המסמך או רק בפסקה הזאת?". Set `Range.ParagraphFormat.LineSpacingRule` (wdLineSpaceSingle = 0, wdLineSpaceDouble = 2, wdLineSpace1pt5 = 4); "רווח אחרי פסקה" sets `SpaceAfter`. Read the value back after setting.
Touches: the active document.
Must never: change anything outside the scope he named; touch fonts or text.
Spoken result: "הרווח בפסקה הוא שורה וחצי, אפשר לבטל".
Confirmation needed: none for a paragraph or selection; before acting for the whole document.
Seed data for the VM: a document with a mix of paragraph styles and a table.
Done when: the rule reads back as set for exactly the scoped paragraphs.
Owner inbox (2026-11-28): "Change paragraph spacing to 1.5 lines".

### Track changes on/off                                status: idea
Ask (Hebrew, as he would say it): "תדליק מעקב שינויים"
Also phrased as: "תכבה", "מעקב שינויים דולק?"
What should happen: `ActiveDocument.TrackRevisions` get and set; say the state before and after.
Touches: the active document's setting.
Must never: accept or reject existing revisions.
Spoken result: "מעקב שינויים היה כבוי ועכשיו הוא דולק, אפשר לבטל".
Confirmation needed: none.
Seed data for the VM: a document with tracking off and one with it on.
Done when: the property reads back as requested. Simplest of the set; first to build.
Owner inbox (2026-11-28): "Turn track changes on/off".

### Review the suggested changes                        status: idea
Ask (Hebrew, as he would say it): "בוא נעבור על השינויים המוצעים"
Also phrased as: "קבל", "דחה", "דלג", "קבל הכל מהמחבר הזה", "מספיק להיום"
What should happen: like the sorting sitting. `ActiveDocument.Revisions` is enumerated one at a time: type (insert / delete / format), author, date, the changed text with ~10 words of context; the tool selects the revision's `Range` so it is highlighted on screen. He says "קבל" / "דחה" / "דלג" / "קבל הכל מהמחבר הזה" / "מספיק להיום"; `Revision.Accept()` / `Reject()`. Tools: `review_changes_start`, `review_changes_next`, `review_changes_accept`, `review_changes_reject`, `review_changes_skip`, `review_changes_status`; `accept_all_changes` and `reject_all_changes` only after explicit confirmation with the count.
Touches: the active document.
Must never: accept or reject anything without his word per item or an explicit "all".
Spoken result: "הוספה של דני: '...', לקבל?" then "התקבל, נשארו 6".
Confirmation needed: before each accept or reject (his word per item); with the count before any "all".
Seed data for the VM: a docx with ~8 tracked revisions by two authors (add to `tools/seed-docs/gen.py`).
Done when: a sitting accepts 2, rejects 1, skips 1, stops, and resumes later at the skipped one.
Owner inbox (2026-11-28): "let's review all of the suggested changes". Most valuable of the set for an academic.
Design note (2026-11-28): easy on the Word side, hard on the sitting side. The tool owns the pointer (state file like sort-sessions.json); "קבל" always means the revision highlighted on screen, never one the model remembers. Per-item turns cost ~6 s each, so allow several answers in one turn ("קבל, דחה, קבל" for the next three, applied in order) and group answers ("כל השינויים של דני", "כל שינויי העיצוב") with a count confirmation. Stop and resume across days is expected, not an edge case.

### Add a footnote                                      status: idea
Ask (Hebrew, as he would say it): "תוסיף הערת שוליים: ..."
Also phrased as: "תוסיף הערת שוליים אחרי המילים X"
What should happen: at the cursor, or after the words he names (Find within the paragraph under the cursor), `Footnotes.Add(Range, Text:=dictated)`. The dictated text is read back before inserting (STT errors in citations are likely); after inserting, the footnote number is spoken.
Touches: the active document.
Must never: insert before the read-back is confirmed; change other text.
Spoken result: "הוספתי הערת שוליים 4, אפשר לבטל".
Confirmation needed: before acting (the read-back).
Seed data for the VM: a document with a few existing footnotes and a paragraph with a distinctive phrase.
Done when: the footnote exists at the right place with the confirmed text and the right number; undo removes it.
Owner inbox (2026-11-28): "Add a footnote".

### Insert a picture                                    status: idea
Ask (Hebrew, as he would say it): "תכניס כאן את התמונה של ..."
Also phrased as: "שהטקסט יזרום מסביב", "מעל ומתחת", "יותר קטן"
What should happen: `search_documents` needs image kinds (jpg/png/tif) and the Pictures folder as a root; then `InlineShapes.AddPicture(path)` at the cursor. "שהטקסט יזרום מסביב" converts it to a Shape with `WrapFormat.Type` = wdWrapSquare (0) or wdWrapTight (1); "מעל ומתחת" = wdWrapTopBottom (4); "יותר קטן" uses `ScaleWidth` / `ScaleHeight`.
Touches: the Pictures folder (read), the active document.
Must never: modify or move the picture file.
Spoken result: which picture and where: "הכנסתי את התמונה X אחרי הפסקה שמתחילה ב..., אפשר לבטל".
Confirmation needed: before acting when more than one picture matches.
Seed data for the VM: a few images with Hebrew names in Pictures, one pair with similar names.
Done when: the picture is at the cursor with the requested wrapping and size.
Owner inbox (2026-11-28): "Insert picture so and so here (incl text flow)".

### Insert a special character                          status: idea
Ask (Hebrew, as he would say it): "תכניס סימן הפסקה"
Also phrased as: "קו מפריד ארוך", "השם עם נקודה", describing the character in words
What should happen: the model maps his description to a Unicode character; `insert_text_at_cursor` (`Selection.TypeText`) inserts it after a read-back of the character's name. For Hebrew niqqud, combining marks go AFTER the base letter, and some fonts lack them.
Touches: the active document.
Must never: insert before he confirmed the named character.
Spoken result: "הכנסתי קו מפריד ארוך, אפשר לבטל".
Confirmation needed: before acting (name the character).
Seed data for the VM: a paragraph with the cursor placed; a Hebrew word for the niqqud case.
Done when: the right code point is in the document at the cursor.
Design note (2026-11-28): this is a model-knowledge task; the tool is trivial.
Owner inbox (2026-11-28): "I want to add a special character (proceeds to describe it)".

### Copy a paragraph from another document              status: idea
Ask (Hebrew, as he would say it): "תעתיק לכאן את הפסקה על X מהמסמך Y"
Also phrased as: "הפסקה השלישית ממסמך Y", "בלי עיצוב"
What should happen: spot the paragraph together, on screen. `search_documents` for Y; open Y read-only but VISIBLE in Word and bring it to the front; locate the candidate paragraph by number ("הפסקה השלישית") or by content (first words / topic); select it so it is highlighted (`Range.Select()`), and ask "זאת?". On "לא" move the highlight to the next candidate (or take his hint: "יותר למטה", "הפסקה עם ..."); on "כן" copy with `Range.FormattedText` into the remembered cursor position of the target document (keeps formatting), or plain text when he says "בלי עיצוב", then bring the target document back to the front. The read-back is the highlight itself, not spoken text.
Touches: Y (read only, visible), the active document.
Must never: modify Y; type into Y; lose the target cursor position while Y is in front.
Spoken result: "זאת?" per candidate; then "הדבקתי את הפסקה מתוך Y במקום שהסמן היה, אפשר לבטל".
Confirmation needed: before acting, by "כן" on the highlighted paragraph.
Seed data for the VM: two documents with distinct, numbered paragraphs, one with heavy formatting; a case where two paragraphs match the words he gives.
Done when: the wrong candidate is rejected and the next one highlighted; the chosen paragraph lands at the original cursor with formatting (or plain); Y is byte-identical; the target is in front again.
Owner inbox (2026-11-28): "Copy the paragraph so and so from document so and so here". Owner note (2026-11-28): the model can push back with "let's spot the paragraph together": switch to that document, mark the paragraph, ask "is that it".

Design note (2026-11-28, owner + Fable): all editing goes through predefined tools with fixed contracts, never free-form COM scripting by the model (blast radius on his real files and mail, testability, latency, untrusted document text). Every mutating tool acts only on the ActiveDocument identified by full path, takes a backup before the first edit, is undoable, and reads back dictated text before inserting. Regression protection before any of these ships: (1) a direct tool-invocation harness on the VM (run one tool with a JSON input, assert the exact result and the file/Office state, no model), (2) a scenario suite replaying the owner's phone session of 2026-11-28 as text turns asserting tool sequences and end state, not wording, (3) the model version pinned and bumped deliberately.

### Word's Document Recovery pane                       status: idea
Ask (Hebrew, as he would say it): none; Shamash notices it. He may say "וורד מראה משהו משמאל".
Also phrased as: "מה זה החלון הזה בוורד?"
What should happen: after a Word crash or power loss, Word opens with the "Document Recovery" pane listing autosaved files; it blocks nothing, but it confuses him and sits there until clicked. Detection, two independent signals: (a) AutoRecover files present (`Application.Options.DefaultFilePath(wdAutoRecoverPath)`, `*.asd`, with their modified times); (b) the pane itself, through the UI Automation tree of the Word window (name "Document Recovery"; the dialog probe does not see it because it is a task pane, not a `#32770` dialog). When seen, the first Word-related reply of the session adds: "וורד מציע לשחזר קובץ ששמרת אוטומטית (X מאתמול). לפתוח את הגרסה המשוחזרת, או לסגור את ההצעה?" On "לפתוח": open the .asd (`Documents.Open`) as a new document next to the original and say both are open; on "לסגור": close the pane (UI Automation Close button, or `CommandBars.ExecuteMso("DocumentRecoveryClose")` if it exists; verify on the VM) and leave the .asd alone. Never delete .asd files.
Touches: the running Word, the AutoRecover folder (read).
Must never: delete or overwrite a recovered or original file; dismiss the pane without asking.
Spoken result: the question above, then "פתחתי את הגרסה המשוחזרת" or "סגרתי את ההצעה, הקובץ המשוחזר נשאר במקום".
Confirmation needed: before acting (either choice is his).
Seed data for the VM: kill WINWORD with a modified unsaved document open, then start Word; the pane appears. Test tooling must stop using Stop-Process on Word for cleanup (use `Application.Quit(SaveChanges:=0)`), so snapshots and runs do not carry this pane by accident.
Done when: with the pane up, the reply mentions it; "לסגור" removes it and the .asd survives; "לפתוח" opens the recovered copy without touching the original.
Owner note (2026-11-28): the agent session left Word open with the recovery pane; special-case it ("you have files pending, should we recover them?").

### Phone feedback and test tooling

### Phone: show what is happening                       status: built (owner to check the screen)
Ask (Hebrew, as he would say it): none; this is app behaviour, not a request.
What should happen: show the transcript and the reply text on screen when the PC sends them (the protocol already carries both), a microphone level meter while listening, and the hint "לחץ שוב כשסיימת" on the red screen. Partials stay off.
Touches: the phone app UI, link protocol (read only).
Must never: dim the screen; show partial words (the live model hallucinated Hebrew in the field test).
Spoken result: unchanged.
Confirmation needed: none.
Seed data for the VM: none; test on the phone against the PC service.
Done when: a link-mode turn shows his words and the reply on screen, the meter moves while he speaks, and the hint is visible on the red screen.
Owner note (2026-11-28): link mode lost the live words of the phone-only POC and that confused him.

### Regression harness for Office tools                 status: tested-vm
Ask (Hebrew, as he would say it): none; this is test tooling.
What should happen: the three items of the editing design note: (1) a direct tool-invocation harness on the VM (one tool, one JSON input, assert the exact result and the file/Office state, no model); (2) a scenario suite replaying the owner's phone session of 2026-11-28 as text turns, asserting tool sequences and end state, not wording; (3) the model version pinned and bumped deliberately.
Touches: the win11-office VM, `tools/winvm/`, the PC service.
Must never: run against his real PC or real documents; depend on model wording.
Spoken result: not applicable.
Confirmation needed: none.
Seed data for the VM: the seed documents from `tools/seed-docs/gen.py`, including the tracked-revisions docx.
Done when: `tools/winvm/scenarios/run.sh` restores win11-office, installs the build, runs the scenarios and prints pass/fail; a COM-level script suite calls each Office tool directly.

### Dial home: telemetry, turn records and logs         status: built (server deployed; uploads to verify on the VM)
Ask (Hebrew, as he would say it): none; this is the owner's view of the deployment.
What should happen: the PC registers with the owner's home server once (enrolment token, its own P-256 key), then sends a signed heartbeat every few minutes (version, health, turns and tokens today, last error) and uploads each finished turn record and the interesting log lines. The server keeps a year and shows a small web page per client: heartbeats, turns (transcript, reply, tool calls, usage), logs, and the list of requests the agent could not fulfil. The heartbeat reply can hold updates or ask for an immediate update check.
Touches: `home/` (FastAPI + SQLite, image on GHCR), the fort compose stack on Athena, `HomeLink.cs`, `HomeUpload.cs`, the tray Status window.
Must never: send his email address or file contents beyond what a turn record already holds; accept unsigned requests; expose the web UI without the household client certificate.
Spoken result: none.
Confirmation needed: none.
Seed data for the VM: none; the VM registers as its own client.
Done when: the client page on the home server shows the VM's heartbeats, a turn with its tool calls, log lines, and a `report_missing_feature` request; a hold set in the UI stops the VM's update checks.
Owner decisions (2026-11-28): no Home Assistant monitoring, small web UI, transcript upload is fine, retention one year, registration by uploading the PC's own public key instead of mTLS.

### Escape hatches: missing feature, look at the screen, generic windows   status: built (VM test pending)
Ask (Hebrew, as he would say it): "תדפיס לי את המכתב" (no print tool yet); "נפתח לי פה משהו על המסך ואני לא יודע מה זה"; "תסגור את זה".
Also phrased as: "מה זה החלון הזה?", "על מה ללחוץ?", "תוציא אותי מפה".
What should happen: (1) a request no tool covers is recorded with `report_missing_feature` (local JSONL, visible on the home server's missing-features page) and he hears that this is not possible yet and was noted; (2) when he describes something on the screen he does not understand, or a tool fails for no clear reason, the agent takes a screenshot (`look_at_screen`, an image the model sees, about 1200 tokens) and reads the window's controls (`describe_window`) and tells him plainly what is there; (3) to get out, the agent prefers closing the window (`close_window`) or Cancel/No over Accept, and can press Escape or Enter and click a named button (`click_control`) through UI Automation.
Touches: the whole desktop (read), any window (close, click); never Word/Outlook content, which have their own tools.
Must never: type free text; click a button that agrees, consents, buys, deletes or sends without his clear yes; run programs; close Explorer or itself.
Spoken result: "זה חלון של ... שנפתח; אפשר לסגור אותו, לסגור?" / "את זה אני עוד לא יודע לעשות, רשמתי את הבקשה".
Confirmation needed: before any click other than close, cancel or no.
Seed data for the VM: none; the scenario opens Windows Settings (and Copilot if the VM has it) as the "accidental" window.
Done when: the VM scenarios pass: a print request records a missing feature; an accidentally opened Settings or Copilot window is described from a screenshot and closed on request; a Notepad unsaved-changes prompt is read back and answered.
Owner test case (2026-11-28): he opened Copilot by accident, it showed a consent dialog, and he did not know what it was or what to click.

<!-- Add tasks below. Printing, calendar, reading a PDF aloud, summarising a long document, dictating a letter, renaming files, finding a photo... -->

### Warn when the phone stops charging                  status: built (phone test pending)
Ask (Hebrew, as he would say it): none; this is a background guard, not a request.
Also phrased as: (the owner asks the PC) "is the phone charging?"
What should happen: the app watches the battery state; if the phone is unplugged for more than N minutes (say 3) while the assistant is deployed, it shows a large on-screen banner in Hebrew ("הטלפון לא בטעינה, בדוק את הכבל") and says it once aloud; it reports charging state and battery level to the PC in the link hello/heartbeat so the owner can see it in the service log or Status window. Clears itself when power returns.
Touches: phone BatteryManager, link protocol (status fields), PC status window.
Must never: nag repeatedly (one spoken warning per outage, banner stays), never dim or sleep the screen as a "saving" measure.
Spoken result: one Hebrew sentence when charging stops; nothing when it resumes (banner disappears).
Confirmation needed: none.
Seed data for the VM: none; test by unplugging the phone.
Done when: unplugging the phone for N minutes produces the banner and one spoken warning, and the PC log shows "phone not charging, 71%".
Owner notes (2026-11-28): the Pixel 7 Pro's USB-C port grips weakly and cables slide out; first mitigation is hardware (Qi wireless stand or a cradle that clamps the cable), this task is the software backstop.

## Findings from the saba inventory (2026-11-27)

These are facts about the target environment; none of the files themselves are in this repo or in the public VM snapshots.

- Source: `~/projects/saba` holds rclone listings of three Google Drive roots (D:, Desktop, Documents) taken 2026-11-12, a partial mirror, `inventory/index.csv` (6,892 documents with LLM category, summary and canonical path), `dedup.json`, and an unapplied consolidation plan for D: (round one: dedup into a Drive quarantine folder; round two: a new folder structure). Its Python tools (python-docx, olefile, striprtf, pypdf, LibreOffice for .doc) are reusable for text extraction.
- Scale: `find_duplicates` is sized for the live Desktop and Documents, not for D:. Default document roots should be Desktop and Documents; the D: archive should be reachable only through the Windows Search index or the saba `index.csv` canonical paths, never walked by search, and `find_duplicates` must refuse roots above a file-count threshold.
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
9. Phone feedback (show transcript, level meter, hint).
10. Regression harness for Office tools (before any editing tool).
10a. Dial home (telemetry, turns, logs, update hold) and the escape hatches (missing feature, screenshot, generic windows): built, VM verification pending.
11. Describe the open document and Undo the last edit (foundation).
12. Small edits: Track changes on/off, Paragraph spacing, Add a footnote, Insert a special character.
13. Review the suggested changes (the most valuable for an academic, after the harness matured).
14. Copy a paragraph from another document.
15. Insert a picture.

# Inbox
<!-- drop raw ideas here; they get expanded into the Tasks section -->
