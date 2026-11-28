# Word editing tools: direct cases

Run by the maintainer on the VM, no model involved. Tools are called straight through the link:

```
tools/pclink/client.sh ws://127.0.0.1:8777/ --tool <name> '<json>'
```

## Setup

1. Seed docs generated (`tools/seed-docs/gen.py`) into `~/vm/shamash-win11/shared/seed`; copy `tools/winvm/harness/` to `~/vm/shamash-win11/shared/harness/`.
2. Fresh copy of the document and Word opened in the interactive session, cursor in paragraph 3 (the seed paragraphs have rule 5 "multiple", space after 10 pt; the seed has 6 numbered paragraphs, then a 3x3 table; paragraphs 1 to 6 are the numbered ones, the table cells are 7 to 9, 11 to 13 and 15 to 17, with the row-end marks 10, 14, 18 and a final empty paragraph 19, so Word counts 19; footnotes: 2; revisions: 0):

```
tools/winvm/ssh.sh 'Copy-Item -Force "\\host.lan\Data\seed\מאמרים\מאמר על ירושלים - לעריכה.docx" "$env:USERPROFILE\Documents\מאמרים\"'
tools/winvm/ssh.sh 'powershell -NoProfile -ExecutionPolicy Bypass -File "\\host.lan\Data\harness\word-edit.ps1" -Path "$env:USERPROFILE\Documents\מאמרים\מאמר על ירושלים - לעריכה.docx"'
```

Add `-SelectParagraph2` to select paragraph 2 instead, `-TrackOn` to open with tracking on. Expected output: `opened: ...; cursor paragraph: 3; selected characters: 0; track changes: False`.
Between cases that need a clean state, close Word without saving and rerun both setup lines.

## Reading Word's state back

Word's ROT entry is per logon session, so reads go through the same interactive task as the setup (`$w` Word, `$d` ActiveDocument, `$s` Selection):

```
tools/winvm/ssh.sh 'powershell -NoProfile -ExecutionPolicy Bypass -File "\\host.lan\Data\harness\word-edit.ps1" -Eval "<expression>"'
# the remote PowerShell expands $, so escape it: -Eval "`$d.Paragraphs.Item(3).LineSpacingRule"
```

Paragraph k is `$d.Paragraphs.Item(k)`; the expressions to evaluate are in the last column below.

## Cases

Every result below also contains `document: מאמר על ירושלים - לעריכה.docx` and, for mutations, ends `; undo available`. The FIRST mutation of the document since the service started also carries `backup: <path under %LocalAppData%\Shamash\backups>` before `; undo available`.

| # | tool | JSON | result starts with | Word state to check |
| --- | --- | --- | --- | --- |
| 1 | `describe_active_document` | `{}` | `document: מאמר על ירושלים - לעריכה.docx; path: ` and contains `saved: yes; track changes: off; revisions: 0; paragraphs: 19; cursor: paragraph 3 of 19, starts "3. שלישית, בתקופה"` and ends `selection: none; footnotes: 2` | nothing changes: `$d.Saved` stays True |
| 1b | `describe_active_document` (opened with `-SelectParagraph2`) | `{}` | contains `cursor: paragraph 2 of 19` and `selection: "2. שנית, הסולטאן סולימאן המפואר`... | none |
| 2 | `set_track_changes` | `{"on":true}` | `track changes: was off, now on; document:` | `$d.TrackRevisions` is True; the result ends `not an undo step (set it back with set_track_changes)` (0.1.25 said `undo available`) |
| 2b | `set_track_changes` | `{"on":false}` | `track changes: was on, now off; document:` | `$d.TrackRevisions` is False |
| 3 | `set_paragraph_spacing` | `{"scope":"paragraph","line_spacing":"1.5","space_after_points":6}` | `line spacing: 1.5 (was multiple), space after: 6 pt (was 10 pt)` ... `for paragraph 3; document:` | `$d.Paragraphs.Item(3).LineSpacingRule` = 1 and `.SpaceAfter` = 6; paragraphs 2 and 4 unchanged (`.LineSpacingRule` still 0 or as seeded) |
| 3b | `set_paragraph_spacing` | `{"scope":"selection","line_spacing":"double"}` with `-SelectParagraph2` | `line spacing: double (was ` ... `for paragraph 2; document:` | `$d.Paragraphs.Item(2).LineSpacingRule` = 2, paragraph 3 unchanged |
| 3c | `set_paragraph_spacing` | `{"scope":"selection","line_spacing":"double"}` with the cursor only | `error: nothing is selected; use scope paragraph or document` | nothing changes |
| 3d | `set_paragraph_spacing` | `{"scope":"document","line_spacing":"single"}` | `line spacing: single (was mixed or ...) for the whole document (19 paragraphs); document:` | every `$d.Paragraphs.Item(k).LineSpacingRule` = 0 except the table row-end marks 10, 14, 18 which Word leaves at 5; fonts and text unchanged |
| 3e | `set_paragraph_spacing` | `{"scope":"paragraph"}` | `error: give line_spacing, space_after_points or both` | none |
| 4 | `insert_text_at_cursor` | `{"text":"©"}` | `inserted "©" at paragraph 3; document:` | `$d.Paragraphs.Item(3).Range.Text` has `©` 10 characters after its start |
| 4b | `insert_text_at_cursor` with `-SelectParagraph2` | `{"text":"©"}` | `error: text is selected, so typing would replace it` | paragraph 2 text unchanged |
| 4c | `insert_text_at_cursor` | `{"text":"a\nb\nc"}` | `error: more than one paragraph break` | none |
| 5 | `add_footnote` | `{"text":"ראו שם, עמ' 7."}` | `footnote ` then a number, ` added at the cursor in paragraph 3; document:` | `$d.Footnotes.Count` = 3; the new one has `.Range.Text` = the text and `.Reference.Start` inside paragraph 3 |
| 5b | `add_footnote` | `{"text":"ראו גם שם.","after_words":"העות'מאנית המאוחרת"}` | `footnote 2 added after "העות'מאנית המאוחרת" in paragraph 3` (numbering follows position: the new one before the old footnote 2 becomes 2) | the reference sits right after those words |
| 5c | `add_footnote` | `{"text":"x","after_words":"לא קיים"}` | `error: "לא קיים" not found in the current paragraph` | `$d.Footnotes.Count` unchanged |
| 6 | `undo_last_edit` after 3 | `{}` | `undone: 1 step; document:` | paragraph 3 `.LineSpacingRule` back to its old value |
| 6b | `undo_last_edit` | `{"count":2}` | `undone: 2 steps; document:` | the last two edits are gone |
| 6c | `undo_last_edit` on a freshly opened document | `{}` | `error: nothing to undo` | none |
| 7 | `restore_backup` (after any mutation 2 to 5) | `{}` | `restored the backup of "מאמר על ירושלים - לעריכה.docx" to ...\מאמר על ירושלים - לעריכה (לפני שמש).docx; the original was not touched` | the new file exists next to the original and has the ORIGINAL content (0 revisions, spacing as seeded, 2 footnotes); the original file is unchanged |
| 7b | `restore_backup` again | `{}` | same, ending `(לפני שמש) 2.docx` | both copies exist, nothing overwritten |
| 8 | any tool with Word closed | e.g. `describe_active_document` `{}` | `error: no document is open in Word` | Word is NOT started (`Get-Process WINWORD` is empty) |

## Things to look at, not assert

- Case 2 / 2b (answered 2026-11-28): the toggle is not an undo step in Word 2016; the tool now says so.
- Case 5: where Word leaves the cursor and the view after `Footnotes.Add` (it may jump to the footnote pane in Draft view).
- Case 3 with a `Heading`-style paragraph or inside the table: result must still read back.
- Backups: `%LocalAppData%\Shamash\backups\<yyyyMMdd-HHmmss> מאמר על ירושלים - לעריכה.docx` exists after the first mutation, has the content from before it, and there is exactly one per service run. A second mutation adds no new file and no `backup:` note.

## Copy paragraph

Extra seed: `מאמרים/ירושלים בתקופה העות'מאנית.docx` (the source: 5 numbered paragraphs, all with run formatting; paragraphs 2 and 4 contain `הסולטאן`; paragraph 2 has a bold-italic red run, an underlined run and a yellow highlight). Copy it to `$env:USERPROFILE\Documents\מאמרים\`. Record its bytes first: `(Get-FileHash "$env:USERPROFILE\Documents\מאמרים\ירושלים בתקופה העות'מאנית.docx").Hash` (call it H0). The editing seed is open with the cursor in paragraph 3 (`word-edit.ps1`, setup above). `$src` below is the source document: `$w.Documents.Item("ירושלים בתקופה העות'מאנית.docx")`. Source arguments are `מאמרים/ירושלים בתקופה העות'מאנית.docx` (any form `search_documents` shows works; the indexer must know the file).

| # | tool | JSON | result starts with | Word state to check |
| --- | --- | --- | --- | --- |
| 9 | `copy_paragraph_start` | `{"source":"מאמרים/ירושלים בתקופה העות'מאנית.docx","words":"סולטאן"}` | `candidate 1 of 2 in "ירושלים בתקופה העות'מאנית.docx": paragraph 2 starts "2. הסולטאן סולימאן המפואר בנה מחדש`... ending `; ask him "זאת?"` | `$w.ActiveDocument.Name` is the source; `$w.Selection.Range.Text` starts `2. הסולטאן`; `$w.Selection.Start` = `$src.Paragraphs.Item(2).Range.Start`; `$src.ReadOnly` True; `$src.Saved` True; file `%LocalAppData%\Shamash\copy-session.json` exists with `cursor` = the target cursor (paragraph 3 start + 10) |
| 9b | `copy_paragraph_next` | `{"hint":"יותר למטה"}` | `candidate 2 of 2 in "ירושלים בתקופה העות'מאנית.docx": paragraph 4 starts "4. בימי הסולטאן` | selection is paragraph 4 of the source, source still in front |
| 9c | `copy_paragraph_next` | `{}` | `no more candidates; ask him for other words or the paragraph number` | selection unchanged (still paragraph 4) |
| 9d | `copy_paragraph_next` | `{"hint":"יותר למעלה"}` | `candidate 1 of 2 in` ... `paragraph 2 starts` | selection back on paragraph 2 |
| 9e | `copy_paragraph_next` | `{"hint":"הפסקה עם הקונסוליות"}` | `candidate 1 of 1 in` ... `paragraph 3 starts "3. הקונסוליות` | selection is paragraph 3; the list is now just that one |
| 10 | `copy_paragraph_confirm` (after 9d, paragraph 2 highlighted) | `{}` | `pasted paragraph 2 of "ירושלים בתקופה העות'מאנית.docx" into "מאמר על ירושלים - לעריכה.docx" at paragraph 3; document: מאמר על ירושלים - לעריכה.docx;` ... `; undo available` | `$d = $w.ActiveDocument` is the target; `$d.Paragraphs.Count` is 20 (19 before). The cursor was 10 characters into paragraph 3, so the paste splits it: paragraph 3 reads `3. שלישית,2. הסולטאן סולימאן המפואר בנה...` and paragraph 4 starts `בתקופה העות'מאנית המאוחרת`. The pasted words keep their formatting: `$d.Range($d.Paragraphs.Item(3).Range.Start+13, $d.Paragraphs.Item(3).Range.Start+20).Font.Bold` -eq -1 and `.Italic` -eq -1 and `.Font.Color` is red (the run `הסולטאן`); `$w.Selection.Start` equals `$d.Paragraphs.Item(4).Range.Start`; the source is closed (`$w.Documents.Count` is 1); `Get-FileHash` of the source file = H0; `copy-session.json` is gone |
| 10b | `copy_paragraph_confirm` after `copy_paragraph_start` with `paragraph_number` 2, `{"plain":true}` | | `pasted paragraph 2 of ... at paragraph 3 without formatting; document:` | same text as case 10 but no bold, italic or colour in the pasted range (`.Font.Bold` -eq 0, `.Font.Color` -eq -16777216), no underline or highlight |
| 10c | `undo_last_edit` after 10 | `{}` | `undone: 1 step; document: מאמר על ירושלים - לעריכה.docx` | `$d.Paragraphs.Count` is back to 19 and paragraph 3 reads `3. שלישית`: ONE undo removes the whole paste (UndoRecord) |
| 11 | `copy_paragraph_cancel` after a start | `{}` | `cancelled; back in "מאמר על ירושלים - לעריכה.docx", nothing was pasted` | the editing document is in front, source closed, `$d.Paragraphs.Count` 19, cursor where it was (paragraph 3), `copy-session.json` gone, source hash = H0 |
| 11b | `copy_paragraph_cancel` with no copy in progress | `{}` | `no paragraph copy was in progress` | none |
| 12 | `copy_paragraph_start` | `{"source":"מאמרים/ירושלים בתקופה העות'מאנית.docx","paragraph_number":9}` | `error: the document has 5 paragraphs; there is no paragraph 9` | the source is NOT left open (`$w.Documents.Count` is 1) |
| 12b | `copy_paragraph_start` | `{"source":"מאמרים/ירושלים בתקופה העות'מאנית.docx","words":"פיל"}` | `error: no paragraph contains "פיל"` | same, no source left open |
| 12c | `copy_paragraph_start` | `{"source":"מאמרים/מאמר על ירושלים - לעריכה.docx"}` | `error: the source is the document being edited; name another document` | nothing opened |
| 12d | `copy_paragraph_start` | `{"source":"C:\\Windows\\win.ini"}` | `error: path not under a document root` | none |
| 12e | `copy_paragraph_next` / `copy_paragraph_confirm` with no state | `{}` | `error: no paragraph copy is in progress; call copy_paragraph_start first` | none |
| 13 | `copy_paragraph_start` with the source ALREADY open (user opened it), then `copy_paragraph_confirm` | `{"source":"...","paragraph_number":2}` | as 9 / 10 | after the confirm the source is STILL open (the tool did not open it) and not dirty |

Run 2026-11-28 on 0.1.25 (cac39739): the tools did their work but every `copy_paragraph_*` call that touched Word answered `error: InvalidComObjectException: Attempt has been made to use a COM object that does not have a backing class factory` (a Core bug, fixed in the source, needs redeploy): `start`, `next` and `cancel` finished their work and then threw while releasing a shared COM object, `confirm` (source opened by the tool, so listed before the target in `Documents`) pasted nothing. Until redeployed, the paste can only be driven when the source was opened BEFORE the target (case 13 order), and results must be read from Word's state, not from the text. Source `Saved` stayed True and the file hash equalled H0 throughout. The Word window of a read-only source also made `describe_active_document` fail on `TrackRevisions` (0x800A11FD); fixed the same way.

Things to look at, not assert: the highlight is visible in the source window after the call (the screenshot is the test); the Word window really comes to the front on a VM with several windows (`Activate`); whether `Range.Select()` scrolls the paragraph into view in a long source; how the paragraph split looks when the cursor is mid-paragraph (cases 10 and 10b paste at the cursor, so paragraph 3 is cut in two and the pasted text joins the first half; a cursor at a paragraph start or on an empty paragraph gives a clean paragraph, which is what he will normally have).

## Picture

Extra seed: the `תמונות` folder (`הכותל בחורף.png`, `הכותל בקיץ.png`, `סבתא ברחובות.png`, 240x160 or 160x240 pixels, no DPI chunk so Word shows them about 6.35 x 4.2 cm) copied to `$env:USERPROFILE\Pictures\` (so `Pictures\תמונות\...`); wait for the indexer (about 75 s). H1 = `(Get-FileHash "$env:USERPROFILE\Pictures\תמונות\הכותל בחורף.png").Hash` before the cases. Editing seed open, cursor in paragraph 3.

| # | tool | JSON | result starts with | Word state to check |
| --- | --- | --- | --- | --- |
| 14 | `search_documents` | `{"query":"כותל","kind":"picture"}` | `1. ` with BOTH `הכותל בחורף.png` and `הכותל בקיץ.png` and the folder column `Pictures/תמונות` | none; with `kind` omitted the PNGs are not listed |
| 14b | `search_documents` | `{"query":"סבתא","kind":"picture"}` | one line `סבתא ברחובות.png` | none |
| 15 | `insert_picture` | `{"path":"Pictures/תמונות/הכותל בחורף.png"}` | `inserted picture "הכותל בחורף.png" (` ... `cm, wrap: inline) after paragraph 3 starting "3. שלישית, בתקופה`... `; document: מאמר על ירושלים - לעריכה.docx;` ... `; undo available` | `$d.InlineShapes.Count` is 1; `$d.Shapes.Count` is 0; the picture's range start is inside paragraph 3 (`$d.InlineShapes.Item(1).Range.Start` between paragraph 3's start and end); `$d.InlineShapes.Item(1).LinkFormat` is not used (embedded: `$d.InlineShapes.Item(1).Type` is 3, wdInlineShapePicture, not 4 linked); `Get-FileHash` = H1 |
| 15b | `insert_picture` | `{"path":"תמונות/הכותל בקיץ.png","wrap":"square","width_cm":8}` | `inserted picture "הכותל בקיץ.png" (8x5.3 cm, wrap: square) after paragraph 3` | `$d.Shapes.Count` is 1, `$d.Shapes.Item(1).WrapFormat.Type` is 0, `.Width` is about 226.8 (8 cm), `.LockAspectRatio` is -1, `.Height` about 151.2 |
| 15c | `insert_picture` | `{"path":"תמונות/הכותל בחורף.png","wrap":"tight"}` / `{"path":"Pictures/תמונות/הכותל בחורף.png","wrap":"top_bottom"}` (a bare file name is `error: file not found`: the path is folder/name as `search_documents` shows it) | `... wrap: tight)` / `... wrap: top_bottom)` | `$d.Shapes.Item(1).WrapFormat.Type` is 1 / 4 |
| 15d | `insert_picture` with `-SelectParagraph2` | `{"path":"תמונות/סבתא ברחובות.png"}` | `inserted picture "סבתא ברחובות.png"` ... `after paragraph 2` | the selected text of paragraph 2 is NOT replaced (paragraph 2 text unchanged except the picture before it); `$d.InlineShapes.Count` is 1 |
| 15e | `insert_picture` | `{"path":"C:\\Windows\\Web\\Wallpaper\\Windows\\img0.jpg"}` | `error: path not under a document or picture folder` | none |
| 15f | `insert_picture` | `{"path":"מאמרים/מאמר על ירושלים - לעריכה.docx"}` | `error: not a picture file` | none |
| 15g | `insert_picture` | `{"path":"תמונות/אין.png"}` | `error: file not found` | none |
| 16 | `resize_last_picture` after 15 | `{"width_cm":3}` | `picture "הכותל בחורף.png" resized to 3x2 cm (was ` | `$d.InlineShapes.Item(1).Width` about 85.05, height about 56.7 (proportions kept) |
| 16b | `resize_last_picture` after 15b | `{"width_cm":4}` | `picture "הכותל בקיץ.png" resized to 4x2.7 cm` | `$d.Shapes.Item(1).Width` about 113.4 |
| 16c | `resize_last_picture` with nothing inserted and nothing selected (fresh service, state file removed: `Remove-Item $env:LOCALAPPDATA\Shamash\picture-session.json`) | `{"width_cm":3}` | `error: no picture selected` | none |
| 16d | `resize_last_picture` with a picture selected by the user (`-Eval '$d.InlineShapes.Item(1).Select()'`) and no session state | `{"width_cm":3}` | `picture "selected picture" resized to` | the selected picture is resized |
| 17 | `set_picture_wrap` after 15 (inline) | `{"wrap":"square"}` | `picture "הכותל בחורף.png" wrap: square (was inline)` | `$d.InlineShapes.Count` is 0, `$d.Shapes.Count` is 1 with `.WrapFormat.Type` 0; the picture did not jump to another page (`$d.Shapes.Item(1).Anchor.Start` still in paragraph 3) |
| 17b | `set_picture_wrap` after 17 | `{"wrap":"top_bottom"}` | `picture "הכותל בחורף.png" wrap: top_bottom (was square)` | `.WrapFormat.Type` is 4 |
| 17c | `set_picture_wrap` after 17b | `{"wrap":"inline"}` | `picture "הכותל בחורף.png" wrap: inline (was top_bottom)` | `$d.Shapes.Count` is 0, `$d.InlineShapes.Count` is 1 |
| 17d | `set_picture_wrap` twice with the same wrap | `{"wrap":"square"}` | second answer: `picture "..." already has wrap: square; document:` (no `undo available`) | no change |
| 17e | `set_picture_wrap` with an invalid value | `{"wrap":"behind"}` | `error: wrap must be inline, square, tight or top_bottom` | none |
| 18 | `undo_last_edit` after 15b (insert with wrap and width) | `{}` | `undone: 1 step` | the picture is gone: `$d.Shapes.Count` and `$d.InlineShapes.Count` both 0 (ONE undo for AddPicture + convert + size) |

Things to look at, not assert: the exact size Word gives a PNG without DPI (the cm figures in cases 15 and 16 are estimates; read the first result and adjust); whether `ConvertToShape` leaves the picture where it was on the page; the behaviour of case 17 when the paragraph is near the bottom of a page.
## Review (tracked changes)

Run 2026-11-28 on build cac39739: R1 to R15 all pass (R9 and the highlight of deletions: see its row). Seed: `מאמרים/מאמר עם הערות עורך.docx` (8 revisions: דני לוי has 2 insertions and 2 deletions, רינה כהן 2 insertions, 1 deletion and 1 formatting change; 8 numbered-less paragraphs, the revisions are one per paragraph in order). Copy and open it like the editing seed (`-Path` with this file; the script also sets tracking off):

```
tools/winvm/ssh.sh 'Copy-Item -Force "\\host.lan\Data\seed\מאמרים\מאמר עם הערות עורך.docx" "$env:USERPROFILE\Documents\מאמרים\"'
tools/winvm/ssh.sh 'powershell -NoProfile -ExecutionPolicy Bypass -File "\\host.lan\Data\harness\word-edit.ps1" -Path "$env:USERPROFILE\Documents\מאמרים\מאמר עם הערות עורך.docx"'
```

State checks are `-Eval` calls as above (`$d.Revisions.Count`, `$s.Text`). The service holds the sessions in memory, so deleting `%LocalAppData%\Shamash\review-sessions.json` while it runs does nothing. To start clean without restarting it: open the seed, `accept_all_changes` with `confirmed_count` 8, `review_changes_start` (a document without revisions drops its session), close the document without saving (`-Eval "`$d.Close(0)"`), delete the copy. A session that survives a closed-without-saving document keeps its pointer: the next `review_changes_answer` without a `review_changes_next` would answer the item the old sitting last showed, so always call `next` first (the model does). Dates in `next` come from the file (2026-11-18 and 2026-11-19) and may read one day off with the VM's time zone.

| # | tool | JSON | result starts with | Word state to check |
| --- | --- | --- | --- | --- |
| R1 | `review_changes_start` | `{}` | `review started: 8 changes (insertion 4, deletion 3, formatting 1); document: מאמר עם הערות עורך.docx; call review_changes_next` | `$d.Revisions.Count` = 8 |
| R2 | `review_changes_next` | `{}` | `revision 1 of 8: insertion by דני לוי (2026-11-18): "…` and contains `[עברה]` and ends `; answer with review_changes_answer; document: מאמר עם הערות עורך.docx` | the inserted word is highlighted on screen: `$s.Text` = `עברה ` (with its trailing space); `$d.Saved` stays True |
| R2b | `review_changes_next` again | `{}` | the same line as R2 | `$s.Text` unchanged |
| R3 | `review_changes_answer` | `{"answers":["accept","reject","skip"]}` | `accepted; rejected; skipped; remaining 6, next: revision 4 of 8: formatting by רינה כהן` and ends `; document: מאמר עם הערות עורך.docx; backup: ` ... `; undo available` (the backup note is there only if no earlier case of this run mutated the document) | `$d.Revisions.Count` = 6; paragraph 1 has no revision on `עברה` but still contains it; paragraph 2 still contains `הגדול` (rejected deletion); paragraph 3 still has its insertion; `$s.Text` is the bold-formatted phrase `שינו את מאזן הכוחות` |
| R3b | `review_changes_answer` with a wrong word | `{"answers":["maybe"]}` | `error: answers must be accept, reject or skip (got 'maybe')` | nothing changes |
| R4 | `review_changes_status` | `{}` | `review: session since ` ... `; revisions in the document: 6; accepted 1, rejected 1, skipped 1, remaining 6; document:` | none |
| R5 | resume: `-Eval "`$d.Save()"`, `-Eval "`$d.Close()"`, reopen with the setup line, then `review_changes_start` | `{}` | `review resumed (started ` ... `) at the next unanswered change: accepted 1, rejected 1, skipped 1, remaining 6; document:` | then `review_changes_next` gives `revision 4 of 8: formatting by רינה כהן` (not revision 1) |
| R6 | `accept_all_changes` (wrong count) | `{"author":"דני","confirmed_count":1}` | `error: 2 changes match by דני, confirm with confirmed_count 2; document:` | `$d.Revisions.Count` still 6 |
| R6b | `accept_all_changes` (no count) | `{"author":"דני"}` | the same error | still 6 |
| R7 | `accept_all_changes` (right count) | `{"author":"דני","confirmed_count":2}` | `accepted 2 changes by דני; 4 left in the document; document:` ... `; undo available` | `$d.Revisions.Count` = 4; no revision by דני לוי remains: `($d.Revisions | ? { $_.Author -like "*דני*" }).Count` is 0 |
| R8 | `reject_all_changes` | `{"type":"formatting","confirmed_count":1}` | `rejected 1 change of type formatting; 3 left in the document` | `$d.Revisions.Count` = 3; `$d.Paragraphs.Item(4).Range.Font.Bold` = 0 |
| R9 | `review_changes_next` | `{}` | `revision 5 of 8: deletion by רינה כהן` | the deleted words are the current revision: read `$d.Revisions.Item(n).Range.Text` and `$s.Start`/`$s.End` (= that range). The VM's Word opens in Simple Markup, where deleted text is hidden: `$s.Text` reads empty and the selection lands in the Revisions pane (Start/End are then pane offsets, e.g. 147-162), so nothing is visibly highlighted. From the build after 0.1.25 the tool sets All Markup and inline revisions before selecting, so `$s.Text` is the deleted text (needs redeploy; check it) |
| R10 | `review_changes_answer` | `{"answers":["accept","accept"]}` | `accepted; accepted; remaining 1, next: revision 3 of 8: insertion by רינה כהן` ... `; revisiting the skipped ones` | `$d.Revisions.Count` = 1 |
| R11 | `review_changes_answer` | `{"answers":["reject"]}` | `rejected; all answered or skipped; document:` ... `; undo available` | `$d.Revisions.Count` = 0; paragraph 3 no longer contains `ושכונות חדשות קמו מחוץ לחומות` |
| R12 | `review_changes_status` | `{}` | `review: session since ` ... `accepted 5, rejected 3, skipped 0, remaining 0` | none |
| R13 | `undo_last_edit` then `review_changes_start` | `{}` then `{}` | `undone: 1 step` then `review resumed` ... `remaining 1` (the rejected insertion came back, its item is pending again) | `$d.Revisions.Count` = 1 |
| R14 | `review_changes_start` on a document with no revisions (open the editing seed) | `{}` | `no tracked changes in this document; document: מאמר על ירושלים - לעריכה.docx` | none |
| R15 | any review tool with Word closed | `{}` | `error: no document is open in Word` | Word is NOT started |

R12 counts: items 1, 5, 6, 7, 8 were accepted (R3, R10, R7), items 2, 3, 4 rejected (R3, R11, R8); R7 and R8 turn the items they removed into accepted and rejected through the session, not through `review_changes_answer`.

## Recovery pane

Run 2026-11-28 on build cac39739 (see the notes after the table). Needs a REAL Word crash, so this runs by hand, never inside a scenario (the clean-desktop step of `check.py` never kills a Word that has a window; this section does, once). Copy the editing seed to a throwaway name and open it with `word-edit.ps1 -Path`.

Provoke the pane:

```
# AutoRecover every minute for this Word, a keystroke so the document is dirty
tools/winvm/ssh.sh 'powershell -NoProfile -ExecutionPolicy Bypass -File "\\host.lan\Data\harness\word-edit.ps1" -Eval "`$w.Options.SaveInterval = 1; `$w.Options.DefaultFilePath(5)"'      # prints the AutoRecover folder
tools/winvm/ssh.sh 'powershell -NoProfile -ExecutionPolicy Bypass -File "\\host.lan\Data\harness\word-edit.ps1" -Eval "`$s.TypeText(''x'')"'
# wait ~90 s, then look for the autosaved file (the folder printed above)
tools/winvm/ssh.sh 'Get-ChildItem "$env:APPDATA\Microsoft\Word" -Filter *.asd | Select Name, LastWriteTime'
# the one place Word is killed: this harness step only (it may wedge ssh, see tools/winvm/README.md; reconnect if so)
tools/winvm/ssh.sh 'Stop-Process -Name WINWORD -Force'
# start Word normally in the interactive session (a COM-started Word does not show the pane); the task needs the full path ("winword.exe" alone gives 0x80070002)
tools/winvm/ssh.sh 'schtasks /Create /TN shamash-test-open /TR "C:\PROGRA~1\MICROS~1\Office16\WINWORD.EXE" /SC ONCE /ST 00:00 /F | Out-Null; schtasks /Run /TN shamash-test-open | Out-Null; Start-Sleep 12'
# Word 2016 starts on its Start screen ("Word has recovered files that you might want to keep" + the link "Show Recovered Files"), not on Document1 with the pane: click that link
tools/pclink/client.sh ws://127.0.0.1:8777/ --tool click_control '{"window":"Word","name":"Show Recovered Files","kind":"link"}'
```

After the click Word shows an empty `Document1` and the Document Recovery pane on the side (the Start screen offer is NOT the pane and the tools do not report it). Then:

| # | tool | JSON | result | check |
| --- | --- | --- | --- | --- |
| C1 | `describe_office_dialogs` | `{}` | `no Office dialogs are open; recovery pane shown` | the pane is on screen (`tools/winvm/screenshot.sh`) |
| C2 | `describe_active_document` | `{}` | ends `; recovery pane shown` | none |
| C3 | `word_recovery` | `{"action":"check"}` | `recovery pane: shown; autosaved files: N (newest "<name>.asd", <today HH:mm>)` with N at least 1 | the folder listing above agrees |
| C4 | `word_recovery` | `{"action":"open"}` | `opened the autosaved file "<name>.asd" (...) as a new document "..."; the original and the recovered version are both open; the autosaved file was not changed` | `$w.Documents.Count` = 2 (or more); the recovered one contains the typed `x`; nothing was overwritten; if Word refuses (the file is in use) the result is `error: Office call failed ...`, which is also worth reporting |
| C5 | `word_recovery` | `{"action":"close"}` | `recovery pane closed (UI Automation); verified: not shown; autosaved files left untouched: N` | the pane is gone from the screenshot; the `.asd` files are all still there |
| C6 | `word_recovery` | `{"action":"check"}` | `recovery pane: not shown; autosaved files: N` | none |
| C7 | `word_recovery` | `{"action":"close"}` | `recovery pane: not shown; nothing to close; autosaved files: N` | no click happens |
| C8 | `describe_office_dialogs` | `{}` | `no Office dialogs are open` (no recovery note) | none |

If C5 says `closed (CommandBars)` the UI Automation click did not work and `DocumentRecoveryClose` exists; if it says `error: the recovery pane is still shown (...)`, copy the part in parentheses: `the first Close button belongs to Word's own window` means the title-bar Close comes before the pane in the control order (nothing was clicked), `the recovery pane was not found in the window` or `the list of controls was cut short` means the pane is not reached by the UI Automation walk. Also report C1 and C3 if the pane IS on screen but they say `not shown` (the pane's name is not what the code looks for): then run `describe_window` on the Word window and paste its element list.

Afterwards: close the recovered documents without saving, `-Eval "`$w.Options.SaveInterval = 10"`, and delete the test `.asd` files by hand (the tools never do).

Run 2026-11-28 (build cac39739): the pane was provoked once (`Stop-Process` after one AutoRecover write, `SaveInterval` 1). C1, C2, C3 FAILED: the pane was on screen but the tools said `no Office dialogs are open` / no note / `recovery pane: not shown; autosaved files: 2`. Cause: Word 2016 en-US has no element titled "Document Recovery"; `describe_window` lists the pane as `text: Word has recovered the following files. Save the ones you wish to keep. Available Files`, `link: Which file do I want to save?`, `button: Close` (first in the list, before the title-bar buttons), and the two list items are plain `<name>.docx` without `[Original]`/`[Recovered]`. Fixed in the source (that intro text is now a pane name; unit test with this exact order), needs redeploy. C4 passed (the recovered document opened read-only with the typed `x`, the pane went away, both `.asd` files still there; the result text claimed the original was open too, now reworded). C5 could not be run (the pane was gone after C4 and no second crash was provoked); C7 and C8 passed with no pane. The autosave folder is recursive: 2 `.asd` files (the file in the folder and the `...((Autosaved-...)).asd` in the subfolder), and `check` counted both. The `.asd` files were deleted by hand afterwards, `SaveInterval` reset to 10.
