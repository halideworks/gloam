Screenshots to capture for the Gloam site
=========================================

Placeholders live in index.html. Four are in the section id="screenshots"
grid (dashboard.png is the full-width lead shot); night-mode-schedule.png
sits in the night mode feature section (id="night-mode"). Each figure has a
placeholder comment naming the file expected here. Capture at 16:10 or similar wide
aspect, PNG, ideally 1600px+ wide (2x for high-DPI). Dark theme, HDR monitor
names visible where possible.

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
   fade duration, and the night-mode auto-disable list.

5. pdf-report.png
   The printer-friendly PDF export, first page. A screenshot of the PDF open
   in a viewer is fine; show the grade and summary table.

After dropping a file in, replace the corresponding placeholder div in
index.html with:

  <img src="assets/screenshots/<name>.png" alt="(keep the existing aria-label text as alt)" loading="lazy">

Keep the surrounding <div class="frame"> wrapper (it draws the gradient
hairline border around the image), delete only the placeholder div and the
placeholder comment.
