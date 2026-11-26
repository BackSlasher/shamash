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
| Office | ? (Microsoft 365 subscription / Office 2019-2024 perpetual / none), 32- or 64-bit, UI language |
| Email | ? (Outlook desktop / new Outlook / Gmail in a browser / other), account type (IMAP, Exchange, Gmail) |
| Where his documents live | ? (`Documents`, Desktop, OneDrive, Google Drive, a second disk, USB sticks) |
| Document formats he uses | ? (.docx, .doc, .pdf, .rtf, scanned images, .xlsx, .pptx) |
| Languages in his documents | Hebrew, English, ? |
| Printer / scanner | ? |
| Browser and default apps | ? |
| Other apps he relies on | ? |
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

### Find a document by what it is about                 status: specified
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

### Translate a document                                status: idea
Ask: "תעביר את המאמר הזה לאנגלית"
What should happen: take the open or just-found document, translate it, save a new file beside the original with a language suffix, open it.
Touches: Word, the document's folder.
Must never: overwrite the original.
Spoken result: "תרגמתי ושמרתי בשם X, זה פתוח עכשיו".
Confirmation needed: none.
Seed data for the VM: a few Hebrew and English articles with headings, footnotes, tables.
Done when: a new .docx exists with the translation and formatting kept.
Owner notes (2026-11-27): no cloud-side Office exists; do it locally without Word: read the .docx with OpenXML, translate paragraphs with Claude, write back into the same structure (styles, headings, tables, lists), save as a new file, open it in Word. Flip paragraph direction for Hebrew<->English. PDFs come out as a translated .docx. Complex layouts are the hard part; v1 = paragraphs, headings, tables, lists.

### Organise and deduplicate a folder                   status: idea
Ask: "תסדר לי את התיקייה של המאמרים"
What should happen: find exact and near duplicates, propose a grouping, ask before moving anything, then move (never delete) into subfolders or a `duplicates` folder.
Touches: document folders.
Must never: delete; move without saying what and where.
Spoken result: a short summary of what was moved.
Confirmation needed: before acting.
Seed data for the VM: the folder above with exact copies, older versions, "Copy of" files.
Done when: no file lost (count before = count after), duplicates grouped.
Owner notes (2026-11-27): describing a rearrangement by voice is not realistic. Design: he triggers, the agent proposes a plan (spoken 3-sentence summary + plain list on the PC screen), he approves, moves only, never deletes, with a move journal for undo. First slice: deduplication alone.

### Help with email                                     status: idea
Ask: "יש לי מייל חדש?", "תקרא לי את המייל מדני", "תענה לו שאני מסכים"
What should happen: read recent mail aloud (sender, subject, gist), draft replies, send only after he confirms.
Touches: his email client / account (see environment).
Must never: send without an explicit spoken confirmation; delete mail.
Spoken result: the draft read back, then "לשלוח?".
Confirmation needed: before sending.
Seed data for the VM: a test mailbox with a handful of Hebrew and English threads.
Done when: a reply arrives at a test address.
Owner notes (2026-11-27): interesting but low priority (Outlook COM is high complexity). If his mail is Gmail/IMAP the PC can read and send directly without Outlook; only Outlook desktop on Exchange needs COM or Graph. Decide after the environment table is filled.

### Show a file in Explorer                             status: specified
Ask: "תראה לי איפה הקובץ הזה נמצא", "תפתח לי את התיקייה של הקובץ"
Also phrased as: "באיזו תיקייה זה?"
What should happen: run `explorer.exe /select,"<full path>"`, which opens a new Explorer window at the folder with the file selected; say which folder it is in.
Touches: Explorer only.
Must never: modify the file.
Spoken result: "פתחתי את התיקייה Y, הקובץ מסומן".
Confirmation needed: none.
Seed data for the VM: any file.
Done when: an Explorer window with the file selected is in front.

### Open a file in its program                          status: specified
Ask: "תפתח את הקובץ הזה", "תפתח אותו בוורד"
Also phrased as: "תפתח לי את המכתב"
What should happen: ShellExecute with the default verb (the file's registered app: Word for .docx, the PDF viewer for .pdf), then confirm aloud. If the file is already open, focus it instead (see "Bring a document to the front").
Touches: the default app for the type.
Must never: pick an app other than the default without asking.
Spoken result: "פתחתי את X".
Confirmation needed: none.
Seed data for the VM: a .docx, a .pdf, an .xlsx.
Done when: the document is open and in front.

### Is a document open?                                 status: specified
Ask: "המכתב לעירייה פתוח עכשיו?"
Also phrased as: "האם הקובץ הזה פתוח?", "אני כבר עובד על המסמך הזה?"
What should happen: two checks, the first that answers wins. (a) Enumerate top-level windows and match the file name in the title (Word titles are "<name> - Word"). (b) The Windows Restart Manager API (RmStartSession / RmRegisterResources / RmGetList) tells which process holds the file; this works for any app without Office automation. If Word automation is available (depends on the Office edition, see environment table) also check Word's Documents collection by full path. Answer in Hebrew: open in Word / not open.
Touches: window list, Restart Manager, optionally Word.
Must never: open or close anything.
Spoken result: "כן, הוא פתוח בוורד" / "לא, הוא לא פתוח".
Confirmation needed: none.
Seed data for the VM: one document open in Word, one open in another app (PDF viewer), one closed.
Done when: correct answer for open, closed, and open-in-another-app cases.

### Bring a document to the front                       status: specified
Ask: "תעביר אותי למסמך של המכתב", "תביא לי את המסמך לקדמה"
Also phrased as: "תעבור למסמך הפתוח"
What should happen: find its window (as in "Is a document open?"), restore it if minimized, bring it to the foreground with SetForegroundWindow. Windows refuses focus stealing from a background process, so use the AllowSetForegroundWindow / attach-thread-input or minimize-restore workaround (to be verified on the VM). With Word automation: Documents(path).Activate() plus Application.Activate.
Touches: window list, optionally Word.
Must never: open a second copy.
Spoken result: "המסמך מולך".
Confirmation needed: none.
Seed data for the VM: two documents open, the target behind another window and one minimized.
Done when: the document's window is the active window.

### Close a document                                    status: idea
Ask: "תסגור את המסמך", "תסגור את המכתב ותשמור"
Also phrased as: "תסגור בלי לשמור"
What should happen: needs the Office edition decision. For Word/Excel with automation available: find the document by path and read its Saved flag. If there are unsaved changes ask aloud "יש שינויים שלא נשמרו, לשמור?" and close with SaveChanges accordingly; if no changes, close without asking. Without automation (other apps): only a WM_CLOSE to the window, which lets the app show its own save prompt, and tell him to answer on screen.
Touches: Word/Excel via automation, or the app's window.
Must never: discard unsaved changes without his spoken yes.
Spoken result: "שמרתי וסגרתי" / "סגרתי בלי לשמור" / "ענה על החלון שבמסך".
Confirmation needed: before discarding.
Seed data for the VM: an open dirty document and an open clean one.
Done when: dirty and clean documents both close correctly.

<!-- Add tasks below. Printing, calendar, reading a PDF aloud, summarising a long document, dictating a letter, renaming files, finding a photo... -->

## Suggested order

0. The PC-side tool loop itself: Claude calling tools from the conversation turn, with a spoken confirmation step and a plain on-screen list for plans (prerequisite for everything below).
1. Find a document (Windows Search + document reader): the foundation every other task reuses.
2. Open a file in its program, and Show a file in Explorer: trivial, make "find" immediately useful.
3. Is a document open? and Bring a document to the front: small, window enumeration shared between them.
4. Deduplicate a folder (first slice of Organise): clean spoken summary, moves only.
5. Translate a document (OpenXML, no Word needed).
6. Close a document: waits for the Office edition answer (Word automation).
7. Organise beyond dedup: propose-then-approve plans.
8. Email: last, after the mail client is known.

# Inbox
<!-- drop raw ideas here; they get expanded into the Tasks section -->

