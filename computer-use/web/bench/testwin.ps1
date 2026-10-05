# testwin.ps1 - a small WinForms target window for bench\desktop.ps1 (the desktop-layer regression bench).
#   Normal mode: Title CU-TEST-WINDOW with a static label, an edit box, a 保存 button (whose click rewrites
#   the label), a check box. -Blank creates a pure white window with no controls (blank-retry regression).
param([switch]$Blank, [int]$X = 80, [int]$Y = 80)
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$f = New-Object System.Windows.Forms.Form
$f.Text = $(if ($Blank) { "CU-TEST-BLANK" } else { "CU-TEST-WINDOW" })
$f.Size = New-Object System.Drawing.Size(900, 560)
$f.StartPosition = "Manual"
$f.Left = $X; $f.Top = $Y
$f.BackColor = [System.Drawing.Color]::White
if ($Blank) {
  # fully flat window (no border, no title bar): a cropped screen capture of it is one white rectangle
  $f.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
  $f.Bounds = New-Object System.Drawing.Rectangle($X, $Y, 700, 420)
}
if (-not $Blank) {
  $script:lbl = New-Object System.Windows.Forms.Label
  $script:lbl.Text = "静态文本 STATIC-42"
  $script:lbl.Location = New-Object System.Drawing.Point(30, 30)
  $script:lbl.Size = New-Object System.Drawing.Size(500, 32)
  $script:lbl.Font = New-Object System.Drawing.Font("Segoe UI", 14)
  $f.Controls.Add($script:lbl)
  $script:box = New-Object System.Windows.Forms.TextBox
  $script:box.Location = New-Object System.Drawing.Point(30, 84)
  $script:box.Size = New-Object System.Drawing.Size(500, 32)
  $script:box.Font = New-Object System.Drawing.Font("Segoe UI", 12)
  $f.Controls.Add($script:box)
  $script:clicks = 0
  $script:btn = New-Object System.Windows.Forms.Button
  $script:btn.Text = "保存"
  $script:btn.Location = New-Object System.Drawing.Point(30, 150)
  $script:btn.Size = New-Object System.Drawing.Size(140, 48)
  $script:btn.Add_Click({
    $script:clicks++
    $script:lbl.Text = "已保存 SAVED-" + $script:clicks
  })
  $f.Controls.Add($script:btn)
  $script:chk = New-Object System.Windows.Forms.CheckBox
  $script:chk.Text = "启用选项"
  $script:chk.Location = New-Object System.Drawing.Point(30, 220)
  $script:chk.Size = New-Object System.Drawing.Size(220, 32)
  $f.Controls.Add($script:chk)
}
[void]$f.Show()
[System.Windows.Forms.Application]::Run($f)