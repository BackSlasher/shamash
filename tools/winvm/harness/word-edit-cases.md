# Word editing tools: direct cases

Run by the maintainer on the VM, no model involved. Tools are called straight through the link:

```
tools/pclink/client.sh ws://127.0.0.1:8777/ --tool <name> '<json>'
```

## Setup

1. Seed docs generated (`tools/seed-docs/gen.py`) into `~/vm/shamash-win11/shared/seed`; copy `tools/winvm/harness/` to `~/vm/shamash-win11/shared/harness/`.
2. Fresh copy of the document and Word opened in the interactive session, cursor in paragraph 3 (the seed has 6 numbered paragraphs, then a 3x3 table; paragraphs 1 to 6 are the numbered ones, the table cells are 7 to 15; footnotes: 2; revisions: 0):

```
tools/winvm/ssh.sh 'Copy-Item -Force "\\host.lan\Data\seed\מאמרים\מאמר על ירושלים - לעריכה.docx" "$env:USERPROFILE\Documents\מאמרים\"'
tools/winvm/ssh.sh '& "\\host.lan\Data\harness\word-edit.ps1" -Path "$env:USERPROFILE\Documents\מאמרים\מאמר על ירושלים - לעריכה.docx"'
```

Add `-SelectParagraph2` to select paragraph 2 instead, `-TrackOn` to open with tracking on. Expected output: `opened: ...; cursor paragraph: 3; selected characters: 0; track changes: False`.
Between cases that need a clean state, rerun the second line after closing Word (or press undo, see case 2).

## Reading Word's state back

`$w` below is the running Word as seen from ssh (the ROT is per session, so use the interactive task; this helper runs the read in it):

```
$q = 'try{$w=[Runtime.InteropServices.Marshal]::GetActiveObject("Word.Application");$d=$w.ActiveDocument;$s=$w.Selection;<EXPR>}catch{"ERROR "+$_}'
```

Simplest: take a screenshot (`tools/winvm/screenshot.sh`) and compare, or run the expression with `-Inner`-style task as in `word-edit.ps1`. The expressions to evaluate (each is `<EXPR>` above, run in the interactive session) are listed per case. Paragraph k = `$d.Paragraphs.Item(k)`.

## Cases

Every result below also contains `document: מאמר על ירושלים - לעריכה.docx` and, for mutations, ends `; undo available`. The FIRST mutation of the document since the service started also carries `backup: <path under %LocalAppData%\Shamash\backups>` before `; undo available`.

| # | tool | JSON | result starts with | Word state to check |
| --- | --- | --- | --- | --- |
| 1 | `describe_active_document` | `{}` | `document: מאמר על ירושלים - לעריכה.docx; path: ` and contains `saved: yes; track changes: off; revisions: 0; paragraphs: 15; cursor: paragraph 3 of 15, starts "3. שלישית, בתקופה"` and ends `selection: none; footnotes: 2` | nothing changes: `$d.Saved` stays True |
| 1b | `describe_active_document` (opened with `-SelectParagraph2`) | `{}` | contains `cursor: paragraph 2 of 15` and `selection: "2. שנית, הסולטאן סולימאן המפואר`... | none |
| 2 | `set_track_changes` | `{"on":true}` | `track changes: was off, now on; document:` | `$d.TrackRevisions` is True |
| 2b | `set_track_changes` | `{"on":false}` | `track changes: was on, now off; document:` | `$d.TrackRevisions` is False |
| 3 | `set_paragraph_spacing` | `{"scope":"paragraph","line_spacing":"1.5","space_after_points":6}` | `line spacing: 1.5 (was single), space after: 6 pt (was ` ... `for paragraph 3; document:` | `$d.Paragraphs.Item(3).LineSpacingRule` = 1 and `.SpaceAfter` = 6; paragraphs 2 and 4 unchanged (`.LineSpacingRule` still 0 or as seeded) |
| 3b | `set_paragraph_spacing` | `{"scope":"selection","line_spacing":"double"}` with `-SelectParagraph2` | `line spacing: double (was ` ... `for paragraph 2; document:` | `$d.Paragraphs.Item(2).LineSpacingRule` = 2, paragraph 3 unchanged |
| 3c | `set_paragraph_spacing` | `{"scope":"selection","line_spacing":"double"}` with the cursor only | `error: nothing is selected; use scope paragraph or document` | nothing changes |
| 3d | `set_paragraph_spacing` | `{"scope":"document","line_spacing":"single"}` | `line spacing: single (was mixed or ...) for the whole document (15 paragraphs); document:` | every `$d.Paragraphs.Item(k).LineSpacingRule` = 0; fonts and text unchanged |
| 3e | `set_paragraph_spacing` | `{"scope":"paragraph"}` | `error: give line_spacing, space_after_points or both` | none |
| 4 | `insert_text_at_cursor` | `{"text":"©"}` | `inserted "©" at paragraph 3; document:` | `$d.Paragraphs.Item(3).Range.Text` has `©` 10 characters after its start |
| 4b | `insert_text_at_cursor` with `-SelectParagraph2` | `{"text":"©"}` | `error: text is selected, so typing would replace it` | paragraph 2 text unchanged |
| 4c | `insert_text_at_cursor` | `{"text":"a\nb\nc"}` | `error: more than one paragraph break` | none |
| 5 | `add_footnote` | `{"text":"ראו שם, עמ' 7."}` | `footnote ` then a number, ` added at the cursor in paragraph 3; document:` | `$d.Footnotes.Count` = 3; the new one has `.Range.Text` = the text and `.Reference.Start` inside paragraph 3 |
| 5b | `add_footnote` | `{"text":"ראו גם שם.","after_words":"העות'מאנית המאוחרת"}` | `footnote 3 added after "העות'מאנית המאוחרת" in paragraph 3` (numbering follows position: the new one before the old footnote 2 becomes 2) | the reference sits right after those words |
| 5c | `add_footnote` | `{"text":"x","after_words":"לא קיים"}` | `error: "לא קיים" not found in the current paragraph` | `$d.Footnotes.Count` unchanged |
| 6 | `undo_last_edit` after 3 | `{}` | `undone: 1 step; document:` | paragraph 3 `.LineSpacingRule` back to its old value |
| 6b | `undo_last_edit` | `{"count":2}` | `undone: 2 steps; document:` | the last two edits are gone |
| 6c | `undo_last_edit` on a freshly opened document | `{}` | `error: nothing to undo` | none |
| 7 | `restore_backup` (after any mutation 2 to 5) | `{}` | `restored the backup of "מאמר על ירושלים - לעריכה.docx" to ...\מאמר על ירושלים - לעריכה (לפני שמש).docx; the original was not touched` | the new file exists next to the original and has the ORIGINAL content (0 revisions, spacing as seeded, 2 footnotes); the original file is unchanged |
| 7b | `restore_backup` again | `{}` | same, ending `(לפני שמש) 2.docx` | both copies exist, nothing overwritten |
| 8 | any tool with Word closed | e.g. `describe_active_document` `{}` | `error: no document is open in Word` | Word is NOT started (`Get-Process WINWORD` is empty) |

## Things to look at, not assert

- Case 2 / 2b: whether Word puts the track-changes toggle on the undo stack. The tool says `undo available` regardless; if `undo_last_edit` after case 2 answers `nothing to undo` or undoes something else, tell me and the wording changes.
- Case 5: where Word leaves the cursor and the view after `Footnotes.Add` (it may jump to the footnote pane in Draft view).
- Case 3 with a `Heading`-style paragraph or inside the table: result must still read back.
- Backups: `%LocalAppData%\Shamash\backups\<yyyyMMdd-HHmmss> מאמר על ירושלים - לעריכה.docx` exists after the first mutation, has the content from before it, and there is exactly one per service run. A second mutation adds no new file and no `backup:` note.
