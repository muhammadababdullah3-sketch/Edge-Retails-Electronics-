# Safe theme token attachment: hex colors + TextBlock/Run typography when Style is not already set.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent
$views = Join-Path $repoRoot 'src\EdgeRetails.Desktop\Views'
$files = Get-ChildItem -Path $views -Recurse -Filter '*.xaml' | Where-Object { $_.Name -notmatch '^Sprint1' }

function Resolve-TextStyle([string]$tag) {
    $fs = $null; $fw = $null
    if ($tag -match 'FontSize="([^"]+)"') { $fs = $Matches[1] }
    if ($tag -match 'FontWeight="([^"]+)"') { $fw = $Matches[1] }
    $key = "$fs|$fw"
    $map = @{
        '24|Bold' = 'Text.PageTitle'
        '20|Bold' = 'Text.SectionTitle'
        '18|Bold' = 'Text.SectionTitle'
        '18|SemiBold' = 'Text.DetailTitle'
        '32|Bold' = 'Text.AmountDialog'
        '28|Bold' = 'Text.AmountHero'
        '22|Bold' = 'Text.AmountLarge'
        '13.5|SemiBold' = 'Text.PosLineTitle'
        '13|SemiBold' = 'Text.Body.Secondary'
        '14|Bold' = 'Text.Control'
        '14|SemiBold' = 'Text.Body'
        '12|SemiBold' = 'Text.TableHeader'
        '11|Bold' = 'Text.PosBadge'
        '11|SemiBold' = 'Text.KpiLabel'
        '12.5|SemiBold' = 'Text.Body'
        '11.5|Bold' = 'Text.PosBadge'
        '10.5|' = 'Text.PosMeta'
        '|SemiBold' = 'Text.Body'
        '|Bold' = 'Text.Control'
    }
    if ($map.ContainsKey($key)) { return $map[$key] }
    if ($fs) {
        $solo = @{
            '13.5' = 'Text.PosLineTitle'
            '13' = 'Text.Secondary'
            '12.5' = 'Text.FormLabel'
            '12' = 'Text.FormLabel'
            '11.5' = 'Text.PosMeta'
            '11' = 'Text.PosMeta'
            '10.5' = 'Text.MonoHint'
            '10' = 'Text.MonoHint'
            '16' = 'Text.DetailTitle'
            '15' = 'Text.Body'
            '18' = 'Text.SectionTitle'
            '20' = 'Text.SectionTitle'
            '14' = 'Text.Body'
        }
        if ($solo.ContainsKey($fs)) { return $solo[$fs] }
    }
    return $null
}

function Apply-TagTypography([string]$content) {
    $rx = [regex]'(?si)<TextBlock\b(?:(?!>).)*?>'
    $sb = [System.Text.StringBuilder]::new()
    $last = 0
    foreach ($m in $rx.Matches($content)) {
        $sb.Append($content.Substring($last, $m.Index - $last)) | Out-Null
        $tag = $m.Value
        $after = $content.Substring($m.Index + $m.Length)
        $hasElementStyle = $after -match '^\s*<(TextBlock|Run)\.Style\b'
        if ($tag -match '\bStyle=' -or $hasElementStyle) {
            if ($hasElementStyle -and $tag -notmatch '\bStyle=') {
                $tag = $tag -replace '\s+Font(Size|Weight)="[^"]*"', ''
            }
            $sb.Append($tag) | Out-Null
        }
        else {
            $styleKey = Resolve-TextStyle $tag
            if ($styleKey) {
                $tag = $tag -replace '\s+Font(Size|Weight)="[^"]*"', ''
                if ($tag -match '<(TextBlock|Run)\b') {
                    $el = $Matches[1]
                    $tag = $tag -replace '<(TextBlock|Run)\b', "<$el Style=`"{StaticResource $styleKey}`""
                }
            }
            $sb.Append($tag) | Out-Null
        }
        $last = $m.Index + $m.Length
    }
    $sb.Append($content.Substring($last)) | Out-Null
    return $sb.ToString()
}

function Apply-LineTypography([string]$line) {
    if ($line -notmatch '<TextBlock\b') { return $line }
    if ($line -match '\bStyle=') { return $line }
    $l = $line
    $l = $l -replace 'FontSize="24"\s+FontWeight="Bold"', 'Style="{StaticResource Text.PageTitle}"'
    $l = $l -replace 'FontSize="20"\s+FontWeight="Bold"', 'Style="{StaticResource Text.SectionTitle}"'
    $l = $l -replace 'FontSize="18"\s+FontWeight="Bold"', 'Style="{StaticResource Text.SectionTitle}"'
    $l = $l -replace 'FontSize="18"\s+FontWeight="SemiBold"', 'Style="{StaticResource Text.DetailTitle}"'
    $l = $l -replace 'FontSize="32"\s+FontWeight="Bold"', 'Style="{StaticResource Text.AmountDialog}"'
    $l = $l -replace 'FontSize="28"\s+FontWeight="Bold"', 'Style="{StaticResource Text.AmountHero}"'
    $l = $l -replace 'FontSize="22"\s+FontWeight="Bold"', 'Style="{StaticResource Text.AmountLarge}"'
    $l = $l -replace 'FontSize="13\.5"\s+FontWeight="SemiBold"', 'Style="{StaticResource Text.PosLineTitle}"'
    $l = $l -replace 'FontSize="13"\s+FontWeight="SemiBold"', 'Style="{StaticResource Text.Body.Secondary}"'
    $l = $l -replace 'FontSize="14"\s+FontWeight="Bold"', 'Style="{StaticResource Text.Control}"'
    $l = $l -replace 'FontSize="14"\s+FontWeight="SemiBold"', 'Style="{StaticResource Text.Body}"'
    $l = $l -replace 'FontSize="12"\s+FontWeight="SemiBold"', 'Style="{StaticResource Text.TableHeader}"'
    $l = $l -replace 'FontSize="11"\s+FontWeight="Bold"', 'Style="{StaticResource Text.PosBadge}"'
    $l = $l -replace 'FontSize="11"\s+FontWeight="SemiBold"', 'Style="{StaticResource Text.KpiLabel}"'
    if ($l -notmatch '\bStyle=') {
        $l = $l -replace 'FontSize="13\.5"', 'Style="{StaticResource Text.PosLineTitle}"'
        $l = $l -replace 'FontSize="13"', 'Style="{StaticResource Text.Secondary}"'
        $l = $l -replace 'FontSize="12"', 'Style="{StaticResource Text.FormLabel}"'
        $l = $l -replace 'FontSize="11"', 'Style="{StaticResource Text.PosMeta}"'
        $l = $l -replace 'FontSize="10"', 'Style="{StaticResource Text.MonoHint}"'
        $l = $l -replace 'FontSize="16"', 'Style="{StaticResource Text.DetailTitle}"'
        $l = $l -replace 'FontSize="15"', 'Style="{StaticResource Text.Body}"'
        $l = $l -replace 'FontSize="18"', 'Style="{StaticResource Text.SectionTitle}"'
        $l = $l -replace 'FontSize="20"', 'Style="{StaticResource Text.SectionTitle}"'
        $l = $l -replace 'FontSize="14"', 'Style="{StaticResource Text.Body}"'
    }
    if ($l -match '\bStyle=') {
        $l = $l -replace '\s+Font(Size|Weight)="[^"]*"', ''
    }
    return $l
}

function Apply-Global([string]$content) {
    $c = $content
    $c = $c -replace 'Foreground="#FFFFFF"', 'Foreground="{StaticResource Brush.White}"'
    $c = $c -replace 'Stroke="#FFFFFF"', 'Stroke="{StaticResource Brush.White}"'
    $c = $c -replace 'Color="#4F46E5"', 'Color="{StaticResource Color.Brand.Primary}"'
    $c = $c -replace 'Color="#DC2626"', 'Color="{StaticResource Color.Danger}"'
    $c = $c -replace 'Color="#0A2540"', 'Color="{StaticResource Color.Shadow.Deep}"'
    $c = $c -replace 'Background="#1AEF4444"', 'Background="{DynamicResource Brush.Badge.Success.Background}"'
    $c = $c -replace 'BorderBrush="#EF4444"', 'BorderBrush="{DynamicResource Brush.Badge.Danger.Border}"'
    return $c
}

$changed = 0
foreach ($f in $files) {
    $lines = [IO.File]::ReadAllLines($f.FullName)
    $out = foreach ($line in $lines) { Apply-LineTypography $line }
    $text = ($out -join "`r`n") + "`r`n"
    $text = Apply-TagTypography $text
    $text = Apply-Global $text
    $raw = [IO.File]::ReadAllText($f.FullName)
    if ($text -ne $raw) {
        [IO.File]::WriteAllText($f.FullName, $text)
        $changed++
    }
}
Write-Host "Updated $changed files"
