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

### Find a document by what it is about                 status: idea
Ask: "תמצא בבקשה את המאמר על ירושלים בתקופה העות'מאנית"
What should happen: search his document folders by content (not just file name), rank, open the best match in Word, say what was found and where.
Touches: document folders (see environment), Word.
Must never: move or modify the file.
Spoken result: "מצאתי את X בתיקייה Y ופתחתי אותו" or "לא מצאתי, אולי תזכור מילה מהכותרת?".
Confirmation needed: none.
Seed data for the VM: ~50 .docx/.pdf in nested folders, a few on the same subject, Hebrew and English, some duplicates with different names.
Done when: the right file opens on screen.

### Translate a document                                status: idea
Ask: "תעביר את המאמר הזה לאנגלית"
What should happen: take the open or just-found document, translate it, save a new file beside the original with a language suffix, open it.
Touches: Word, the document's folder.
Must never: overwrite the original.
Spoken result: "תרגמתי ושמרתי בשם X, זה פתוח עכשיו".
Confirmation needed: none.
Seed data for the VM: a few Hebrew and English articles with headings, footnotes, tables.
Done when: a new .docx exists with the translation and formatting kept.

### Organise and deduplicate a folder                   status: idea
Ask: "תסדר לי את התיקייה של המאמרים"
What should happen: find exact and near duplicates, propose a grouping, ask before moving anything, then move (never delete) into subfolders or a `duplicates` folder.
Touches: document folders.
Must never: delete; move without saying what and where.
Spoken result: a short summary of what was moved.
Confirmation needed: before acting.
Seed data for the VM: the folder above with exact copies, older versions, "Copy of" files.
Done when: no file lost (count before = count after), duplicates grouped.

### Help with email                                     status: idea
Ask: "יש לי מייל חדש?", "תקרא לי את המייל מדני", "תענה לו שאני מסכים"
What should happen: read recent mail aloud (sender, subject, gist), draft replies, send only after he confirms.
Touches: his email client / account (see environment).
Must never: send without an explicit spoken confirmation; delete mail.
Spoken result: the draft read back, then "לשלוח?".
Confirmation needed: before sending.
Seed data for the VM: a test mailbox with a handful of Hebrew and English threads.
Done when: a reply arrives at a test address.

<!-- Add tasks below. Printing, calendar, reading a PDF aloud, summarising a long document, dictating a letter, renaming files, finding a photo... -->

# Inbox
find a specific file based on content, name, location etc.
open the file in windows explorer (open a new explorer that has this doc on focus)
open the file in the right program (e.g. word)
tell if a specific document is open
focus on an open document
close a specific document, optionally saving (ask user, maybe also check if is dirty)

