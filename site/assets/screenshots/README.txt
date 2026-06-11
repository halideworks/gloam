Screenshots to capture for the Gloam site
=========================================

All placeholders live in index.html (section id="screenshots"). Each figure has
a TODO comment naming the file expected here. Capture at 16:10 or similar wide
aspect, PNG, ideally 1600px+ wide (they are displayed at ~450px in a grid, so
2x for high-DPI). Dark theme, HDR monitor names visible where possible.

1. dashboard.png
   The multi-monitor dashboard ("Expert Dashboard"). Show at least two
   monitors with real EDID names, HDR/SDR status badges, and the per-monitor
   gamma mode selectors visible.

2. calibration-report.png
   A completed calibration report. Show the grade, the native vs. calibrated
   dE2000 summary (average / max / grayscale / primaries), and the
   enable/disable A/B toggle button.

3. verification-charts.png
   The detailed verification view. Show dE histograms, the worst/best patch
   lists, and (on an HDR run) the PQ tracking sweep chart.

4. night-mode-schedule.png
   The night mode settings. Show the sunset/sunrise schedule with location,
   fade duration, and the per-app exclusion list.

5. pdf-report.png
   The printer-friendly PDF export, first page. A screenshot of the PDF open
   in a viewer is fine; show the grade and summary table.

After dropping a file in, replace the corresponding placeholder div in
index.html with:

  <img src="assets/screenshots/<name>.png" alt="(keep the existing aria-label text as alt)" loading="lazy">

and delete the placeholder div and TODO comment.
