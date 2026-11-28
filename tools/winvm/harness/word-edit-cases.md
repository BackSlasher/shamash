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
| 10 | `copy_paragraph_confirm` (after 9d, paragraph 2 highlighted) | `{}` | `pasted paragraph 2 of "ירושלים בתקופה העות'מאנית.docx" into "מאמר על ירושלים - לעריכה.docx" at paragraph 3; document: מאמר על ירושלים - לעריכה.docx;` ... `; undo available` | `$d = $w.ActiveDocument` is the target; `$d.Paragraphs.Count` is 16. The cursor was 10 characters into paragraph 3, so the paste splits it: paragraph 3 reads `3. שלישית,2. הסולטאן סולימאן המפואר בנה...` and paragraph 4 starts `בתקופה העות'מאנית המאוחרת`. The pasted words keep their formatting: `$d.Range($d.Paragraphs.Item(3).Range.Start+13, $d.Paragraphs.Item(3).Range.Start+19).Font.Bold` -eq -1 and `.Italic` -eq -1 and `.Font.Color` is red (the run `הסולטאן`); `$w.Selection.Start` equals `$d.Paragraphs.Item(4).Range.Start`; the source is closed (`$w.Documents.Count` is 1); `Get-FileHash` of the source file = H0; `copy-session.json` is gone |
| 10b | `copy_paragraph_confirm` after `copy_paragraph_start` with `paragraph_number` 2, `{"plain":true}` | | `pasted paragraph 2 of ... at paragraph 3 without formatting; document:` | same text as case 10 but no bold, italic or colour in the pasted range (`.Font.Bold` -eq 0, `.Font.Color` -eq -16777216), no underline or highlight |
| 10c | `undo_last_edit` after 10 | `{}` | `undone: 1 step; document: מאמר על ירושלים - לעריכה.docx` | `$d.Paragraphs.Count` is back to 15 and paragraph 3 reads `3. שלישית`: ONE undo removes the whole paste (UndoRecord) |
| 11 | `copy_paragraph_cancel` after a start | `{}` | `cancelled; back in "מאמר על ירושלים - לעריכה.docx", nothing was pasted` | the editing document is in front, source closed, `$d.Paragraphs.Count` 15, cursor where it was (paragraph 3), `copy-session.json` gone, source hash = H0 |
| 11b | `copy_paragraph_cancel` with no copy in progress | `{}` | `no paragraph copy was in progress` | none |
| 12 | `copy_paragraph_start` | `{"source":"מאמרים/ירושלים בתקופה העות'מאנית.docx","paragraph_number":9}` | `error: the document has 5 paragraphs; there is no paragraph 9` | the source is NOT left open (`$w.Documents.Count` is 1) |
| 12b | `copy_paragraph_start` | `{"source":"מאמרים/ירושלים בתקופה העות'מאנית.docx","words":"פיל"}` | `error: no paragraph contains "פיל"` | same, no source left open |
| 12c | `copy_paragraph_start` | `{"source":"מאמרים/מאמר על ירושלים - לעריכה.docx"}` | `error: the source is the document being edited; name another document` | nothing opened |
| 12d | `copy_paragraph_start` | `{"source":"C:\\Windows\\win.ini"}` | `error: path not under a document root` | none |
| 12e | `copy_paragraph_next` / `copy_paragraph_confirm` with no state | `{}` | `error: no paragraph copy is in progress; call copy_paragraph_start first` | none |
| 13 | `copy_paragraph_start` with the source ALREADY open (user opened it), then `copy_paragraph_confirm` | `{"source":"...","paragraph_number":2}` | as 9 / 10 | after the confirm the source is STILL open (the tool did not open it) and not dirty |

Things to look at, not assert: the highlight is visible in the source window after the call (the screenshot is the test); the Word window really comes to the front on a VM with several windows (`Activate`); whether `Range.Select()` scrolls the paragraph into view in a long source; how the paragraph split looks when the cursor is mid-paragraph (cases 10 and 10b paste at the cursor, so paragraph 3 is cut in two and the pasted text joins the first half; a cursor at a paragraph start or on an empty paragraph gives a clean paragraph, which is what he will normally have).

## Picture

Extra seed: the `תמונות` folder (`הכותל בחורף.png`, `הכותל בקיץ.png`, `סבתא ברחובות.png`, 240x160 or 160x240 pixels, no DPI chunk so Word shows them about 6.35 x 4.2 cm) copied to `$env:USERPROFILE\Pictures\` (so `Pictures\תמונות\...`); wait for the indexer (about 75 s). H1 = `(Get-FileHash "$env:USERPROFILE\Pictures\תמונות\הכותל בחורף.png").Hash` before the cases. Editing seed open, cursor in paragraph 3.

| # | tool | JSON | result starts with | Word state to check |
| --- | --- | --- | --- | --- |
| 14 | `search_documents` | `{"query":"כותל","kind":"picture"}` | `1. ` with BOTH `הכותל בחורף.png` and `הכותל בקיץ.png` and the folder column `Pictures/תמונות` | none; with `kind` omitted the PNGs are not listed |
| 14b | `search_documents` | `{"query":"סבתא","kind":"picture"}` | one line `סבתא ברחובות.png` | none |
| 15 | `insert_picture` | `{"path":"Pictures/תמונות/הכותל בחורף.png"}` | `inserted picture "הכותל בחורף.png" (` ... `cm, wrap: inline) after paragraph 3 starting "3. שלישית, בתקופה`... `; document: מאמר על ירושלים - לעריכה.docx;` ... `; undo available` | `$d.InlineShapes.Count` is 1; `$d.Shapes.Count` is 0; the picture's range start is inside paragraph 3 (`$d.InlineShapes.Item(1).Range.Start` between paragraph 3's start and end); `$d.InlineShapes.Item(1).LinkFormat` is not used (embedded: `$d.InlineShapes.Item(1).Type` is 1, wdInlineShapePicture, not 4 linked); `Get-FileHash` = H1 |
| 15b | `insert_picture` | `{"path":"תמונות/הכותל בקיץ.png","wrap":"square","width_cm":8}` | `inserted picture "הכותל בקיץ.png" (8x5.3 cm, wrap: square) after paragraph 3` | `$d.Shapes.Count` is 1, `$d.Shapes.Item(1).WrapFormat.Type` is 0, `.Width` is about 226.8 (8 cm), `.LockAspectRatio` is -1, `.Height` about 151.2 |
| 15c | `insert_picture` | `{"path":"הכותל בחורף.png","wrap":"tight"}` / `"top_bottom"` | `... wrap: tight)` / `... wrap: top_bottom)` | `$d.Shapes.Item(1).WrapFormat.Type` is 1 / 4 |
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
