# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Alexander Graef <alex@graef.cc>
#
# This file is part of WaveDude Next, a 2D wave simulator.
#
# Based on the work of
#   Jeroen Vleggaar (Huygens Optics), author of Wave Dude 2D, Copyright (C) 2026
#     <https://www.youtube.com/@HuygensOptics>, <https://www.huygensoptics.com>
#   0x23 (Diffraction Limited), author of WaveSimulator2D
#     <https://www.youtube.com/@DiffractionLimited>, <https://github.com/0x23/WaveSimulator2D>
#
# WaveDude Next is free software: you can redistribute it and/or modify it under
# the terms of the GNU General Public License as published by the Free Software
# Foundation, either version 3 of the License, or (at your option) any later
# version. It is distributed WITHOUT ANY WARRANTY. See the LICENSE.md file or
# <https://www.gnu.org/licenses/>.

# Animated WebP pictures of example projects, field and intensity, for the README (2026-11-14): candidates to look
# through, at a reduced size and frame rate, sped up by one factor for all (-Speed, 2 unless given), each ending at
# its example's cut-off frame (the table below).
#
# Each example is rendered by AnimationRender, the console beside this script, which is built first: no window, the
# app's own export (the scene with its parts, a full-quality run, the layers' colours, brightness and outline, the
# probes' graph boxes and markers) with every rendered frame piped into ffmpeg, which picks the frames of the
# animation's rate, scales them and writes the WebP. Nothing lies between the rendered pixels and the scaler, no
# video and no file; nothing is written but the animations. A run is only as long as its animation and its frames
# stay in memory (up to 23 GB for the longest of these examples at its full length); one that does not fit the free
# memory is refused and reported, unless -AllowDiskCache lets the rest go to the app's frame cache on disk.
#
# The output folder then holds <example>_field.webp and <example>_intensity.webp and index.html, a page with every
# animation in the folder beside its facts (size, frames, seconds, megabytes) to look through.
#
# Needs ffmpeg with the libwebp encoder on the path (or -Ffmpeg <path>). Everything runs below normal priority, so
# it stays out of the way of work at the machine.
#
#   powershell -ExecutionPolicy Bypass -File _tools\animation_render\ExampleAnimations.ps1
#       [-Examples 3,12,...] [-CutOff <frame>] [-Out <folder>] [-Width <px>] [-Fps <n>] [-Speed <factor>]
#       [-Quality <1..100>] [-Ffmpeg <path>] [-AllowDiskCache] [-Detail]

param(
	# the examples to make, by their numbers in the File > Examples menu, with commas between; none named: those of
	# the table below. Text, since "powershell -File" hands a list over as one word (which as a number read 26,18 as 2618)
	[string[]]$Examples = @(),
	# the frame the animations of this run end at, in place of the table's: for trying a cut-off on an example
	[int]$CutOff = -1,
	# where the animations and the review page go
	[string]$Out = (Join-Path $env:TEMP "WaveDudeNext\ExampleAnimations"),
	# width of an animation in pixels; the height follows from the canvas
	[int]$Width = 480,
	# frames per second of an animation
	[int]$Fps = 15,
	# the speed-up of every animation against the project's video frame rate: 2 plays twice as fast, in half the
	# frames and half the time; 1 is the export's own speed
	[double]$Speed = 2,
	# quality of the lossy WebP frames, 1 to 100
	[ValidateRange(1, 100)]
	[int]$Quality = 75,
	[string]$Ffmpeg = "ffmpeg",
	# lets a run's frames go to the app's frame cache on disk where they do not fit the free memory
	[switch]$AllowDiskCache,
	# shows the library's log from Info up (the engine, the run, the export) rather than warnings alone
	[switch]$Detail
)

# The examples worth an animation, and the frame each one's animation ends at: the frame number as the timeline
# shows it, the last frame kept; 0 keeps the project's whole export range. Past that frame nothing new happens.
# An example named with -Examples that is not listed here runs whole.
$cutOffs = @{
	 3 = 0
	12 = 0
	13 = 0
	15 = 0
	16 = 0
	18 = 0
	19 = 0
	20 = 0
	22 = 0
	23 = 0
	26 = 0
	27 = 0
	28 = 0
	30 = 0
	31 = 0
}
$numbers = @($Examples | ForEach-Object { $_ -split "[,; ]+" } | Where-Object { $_ -match "^\d+$" } | ForEach-Object { [int]$_ })
if ($numbers.Count -eq 0) { $numbers = @($cutOffs.Keys | Sort-Object) }

$invariant = [Globalization.CultureInfo]::InvariantCulture
$layers = @("field", "intensity")
$script:problems = 0

function Problem([string]$text) { $script:problems++; Write-Output ("PROBLEM  " + $text) }

# an example by its number, with the cut-off of this run
function ExampleOf([int]$number) {
	$file = @(Get-ChildItem (Join-Path $PSScriptRoot "..\..\Examples") -Filter ("Example{0:00}_*.wdproj" -f $number))
	if ($file.Count -ne 1) { return $null }
	$cut = 0
	if ($CutOff -ge 0) { $cut = $CutOff } elseif ($cutOffs.ContainsKey($number)) { $cut = [int]$cutOffs[$number] }
	return [pscustomobject]@{
		File   = $file[0]
		Stem   = $file[0].BaseName
		Title  = ("{0}. {1}" -f $number, (($file[0].BaseName -replace "^Example\d+_", "") -replace "_", " "))
		CutOff = $cut
	}
}
function AnimationOf($example, [string]$layer) { return (Join-Path $Out ("{0}_{1}.webp" -f $example.Stem, $layer)) }

# what an animated WebP holds, read from its chunks: the canvas, the frames and their time
function WebpFacts([string]$path) {
	$b = [IO.File]::ReadAllBytes($path)
	# a 24-bit number, low byte first (as an int: a byte shifted stays a byte)
	$number = { param([int]$at) [int]$b[$at] + ([int]$b[$at + 1] -shl 8) + ([int]$b[$at + 2] -shl 16) }
	$width = 0; $height = 0; $frames = 0; $ms = 0; $pos = 12
	while ($pos + 8 -le $b.Length) {
		$id = [Text.Encoding]::ASCII.GetString($b, $pos, 4); $size = [BitConverter]::ToUInt32($b, $pos + 4)
		if ($id -eq "VP8X") { $width = 1 + (& $number ($pos + 12)); $height = 1 + (& $number ($pos + 15)) }
		if ($id -eq "ANMF") { $frames++; $ms += (& $number ($pos + 20)) }
		$pos += 8 + $size + ($size % 2)
	}
	return [pscustomobject]@{ Width = $width; Height = $height; Frames = $frames; Seconds = $ms / 1000.0; Bytes = $b.Length }
}
function Megabytes([long]$bytes) { return ($bytes / 1MB).ToString("0.0", $invariant) }

# the page to look through: every animation in the folder, whichever run made it, a row per example, the field
# beside the intensity, each with its facts as read from the file
function WriteReviewPage() {
	$html = New-Object Text.StringBuilder
	$null = $html.AppendLine("<!DOCTYPE html>")
	$null = $html.AppendLine("<html lang=""en""><head><meta charset=""utf-8""><title>WaveDude Next: example animations</title>")
	$null = $html.AppendLine("<style>body{background:#1e1e1e;color:#ddd;font:14px 'Segoe UI',sans-serif;margin:24px}h2{font-size:16px;margin:28px 0 8px}")
	$null = $html.AppendLine("div.pair{display:flex;gap:16px;flex-wrap:wrap}figure{margin:0}figcaption{color:#999;margin-top:4px}img{display:block;max-width:100%}</style></head><body>")
	$null = $html.AppendLine("<h1>Example animations</h1>")
	# the names sort by the example's two-digit number, the field before the intensity
	$title = $null; $count = 0
	foreach ($file in @(Get-ChildItem $Out -Filter "Example*.webp" | Sort-Object Name)) {
		if ($file.BaseName -notmatch "^Example(\d+)_(.+)_(field|intensity)$") { continue }
		$number = [int]$Matches[1]; $name = $Matches[2] -replace "_", " "; $layer = $Matches[3]
		$facts = WebpFacts $file.FullName
		if (("{0}. {1}" -f $number, $name) -ne $title) {
			if ($title -ne $null) { $null = $html.AppendLine("</div>") }
			$title = "{0}. {1}" -f $number, $name
			$null = $html.AppendLine("<h2>" + [Net.WebUtility]::HtmlEncode($title) + "</h2><div class=""pair"">")
		}
		$null = $html.AppendLine(("<figure><img loading=""lazy"" src=""{0}"" width=""{1}"" height=""{2}"" alt=""{3}""><figcaption>{3}: {1} &times; {2}, {4} frames, {5} s, {6} MB</figcaption></figure>" -f
			[Uri]::EscapeDataString($file.Name), $facts.Width, $facts.Height, $layer, $facts.Frames, $facts.Seconds.ToString("0.0", $invariant), (Megabytes $facts.Bytes)))
		$count++
	}
	if ($title -ne $null) { $null = $html.AppendLine("</div>") }
	$null = $html.AppendLine("</body></html>")
	[IO.File]::WriteAllText((Join-Path $Out "index.html"), $html.ToString())
	return $count
}

# ---- the run ----

# below normal priority, which the renderer and ffmpeg inherit: a background job, not the machine's first concern
[Diagnostics.Process]::GetCurrentProcess().PriorityClass = [Diagnostics.ProcessPriorityClass]::BelowNormal

$chosen = @()
foreach ($number in $numbers) {
	$example = ExampleOf $number
	if ($example -eq $null) { Problem "example $number`: no single Example file with that number" } else { $chosen += $example }
}

$encoders = ""
if (Get-Command $Ffmpeg -ErrorAction SilentlyContinue) { $encoders = (& $Ffmpeg -hide_banner -encoders) -join "`n" }
if ($encoders -notmatch "libwebp") { Problem "ffmpeg with the libwebp encoder is needed: '$Ffmpeg' is not it"; $chosen = @() }

# the renderer, built as it is now
$tool = Join-Path $PSScriptRoot "bin\Release\net10.0-windows\AnimationRender.exe"
if ($chosen.Count -gt 0) {
	$build = & dotnet build (Join-Path $PSScriptRoot "AnimationRender.csproj") -c Release -nologo -v q
	if ($LASTEXITCODE -ne 0 -or -not (Test-Path $tool)) {
		$build | Select-Object -Last 20 | ForEach-Object { Write-Output ("    " + $_) }
		Problem "the renderer did not build"; $chosen = @()
	}
}

New-Item -ItemType Directory -Force $Out | Out-Null
$made = 0; $total = 0
foreach ($example in $chosen) {
	$a = @($example.File.FullName, "--out", $Out, "--last-frame", "$($example.CutOff)", "--width", "$Width", "--fps", "$Fps",
		"--speed", $Speed.ToString("0.###", $invariant), "--quality", "$Quality", "--ffmpeg", $Ffmpeg)
	if ($AllowDiskCache) { $a += "--allow-disk-cache" }
	if ($Detail) { $a += "--verbose" }
	$started = [DateTime]::Now
	& $tool @a
	if ($LASTEXITCODE -ne 0) { Problem ("{0}: not rendered (exit code {1}), see the lines above" -f $example.Title, $LASTEXITCODE); continue }
	foreach ($layer in $layers) {
		$animation = AnimationOf $example $layer
		if (-not (Test-Path $animation)) { Problem ("{0}: no animation of the {1}" -f $example.Title, $layer); continue }
		$facts = WebpFacts $animation
		$made++; $total += $facts.Bytes
		Write-Output ("    {0,-9} {1,4} x {2,-4} {3,5} frames {4,6} s {5,6} MB   {6}" -f $layer, $facts.Width, $facts.Height, $facts.Frames,
			$facts.Seconds.ToString("0.0", $invariant), (Megabytes $facts.Bytes), $(if ($example.CutOff -gt 0) { "cut at frame $($example.CutOff)" } else { "whole" }))
	}
	Write-Output ("    {0:0} s" -f ([DateTime]::Now - $started).TotalSeconds)
}
$listed = WriteReviewPage
Write-Output ("{0} animations made, {1} MB; the page to look through lists {2}: {3}" -f $made, (Megabytes $total), $listed, (Join-Path $Out "index.html"))
Write-Output ("{0} problem(s)" -f $script:problems)
exit $script:problems
