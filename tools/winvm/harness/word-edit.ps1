<#
Opens a seed document in Word in the INTERACTIVE session of the VM user (the service attaches to the Word of its own session; a Word
started straight from ssh lives in session 0 and the tools would say "no document is open in Word") and puts the cursor in paragraph 3.

  tools/winvm/ssh.sh '& "\\host.lan\Data\harness\word-edit.ps1" -Path "C:\Users\shamash\Documents\מאמרים\מאמר על ירושלים - לעריכה.docx"'
  ... -SelectParagraph2   selects paragraph 2 instead (cursor variant "selection")
  ... -TrackOn            opens with track changes on

Prints one line: cursor paragraph, selection length, track changes. Copy the harness folder to ~/vm/shamash-win11/shared/harness/ first.
#>
param(
    [Parameter(Mandatory)][string]$Path,
    [switch]$SelectParagraph2,
    [switch]$TrackOn,
    [switch]$Inner
)
$ErrorActionPreference = 'Stop'

if (-not $Inner) {
    # Re-run this script in the interactive session through a one-shot scheduled task, then wait for it.
    $local = Join-Path $env:TEMP 'word-edit-inner.ps1'
    Copy-Item -Force $PSCommandPath $local
    $out = Join-Path $env:TEMP 'word-edit-inner.out'
    Remove-Item -Force $out -ErrorAction SilentlyContinue
    $args2 = "-NoProfile -ExecutionPolicy Bypass -File `"$local`" -Path `"$Path`" -Inner" + $(if ($SelectParagraph2) { ' -SelectParagraph2' }) + $(if ($TrackOn) { ' -TrackOn' })
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $args2
    $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive
    Register-ScheduledTask -TaskName 'ShamashWordEdit' -Action $action -Principal $principal -Force | Out-Null
    Start-ScheduledTask -TaskName 'ShamashWordEdit'
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 1
        if ((Get-ScheduledTask -TaskName 'ShamashWordEdit').State -ne 'Running' -and (Test-Path $out)) { break }
    }
    Unregister-ScheduledTask -TaskName 'ShamashWordEdit' -Confirm:$false
    if (Test-Path $out) { Get-Content -Encoding UTF8 $out } else { 'ERROR: the interactive task did not finish' }
    return
}

$out = Join-Path $env:TEMP 'word-edit-inner.out'
try {
try { $word = [Runtime.InteropServices.Marshal]::GetActiveObject('Word.Application') } catch { $word = New-Object -ComObject Word.Application }
$word.Visible = $true
$doc = $null
foreach ($d in $word.Documents) { if ($d.FullName -eq $Path) { $doc = $d } }
if (-not $doc) { $doc = $word.Documents.Open($Path) }
$doc.Activate()
$doc.TrackRevisions = [bool]$TrackOn
if ($SelectParagraph2) {
    $r = $doc.Paragraphs.Item(2).Range
    $word.Selection.SetRange($r.Start, $r.End - 1)       # the text of paragraph 2 without its paragraph mark
} else {
    $r = $doc.Paragraphs.Item(3).Range
    $word.Selection.SetRange($r.Start + 10, $r.Start + 10)   # an insertion point inside paragraph 3
}
$sel = $word.Selection
$p = $doc.Range(0, $sel.Paragraphs.Item(1).Range.Start).Paragraphs.Count
if ($sel.Paragraphs.Item(1).Range.Start -gt 0) { $p++ } else { $p = 1 }
"opened: $($doc.Name); cursor paragraph: $p; selected characters: $($sel.End - $sel.Start); track changes: $($doc.TrackRevisions)" | Set-Content -Encoding UTF8 $out
} catch { "ERROR: $($_.Exception.Message)" | Set-Content -Encoding UTF8 $out }
