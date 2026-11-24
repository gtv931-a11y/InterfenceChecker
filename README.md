# Interference Isolator (Revit 2026)

A **Autodesk Revit 2026** add-in built for `.NET 8` (`net8.0-windows`) with **WPF** to review HTML interference/clash reports, isolate clashing elements in a dedicated 3D coordination view, track resolution history, and export progress reports.

## Key Features

- **HTML Report Parsing (`HtmlReportParser`):** Parses interference rows from Revit HTML interference reports as well as Navisworks HTML reports (both table and clash list layouts, supporting English and Spanish labels) while deduplicating element pairs.
- **3D Isolation & Visual Highlighting (`Isolator`):**
  - Creates or reuses an `Isolated_Interference` 3D coordination view, sets a `1.15x` bounding box around the clashing elements, and zooms to fit.
  - Halftones surrounding elements while highlighting **Element A in red** and **Element B in blue** in the host view via `OverrideGraphicSettings` (falling back to selection when linked elements cannot be recolored directly in the host view).
  - Restores previous graphic overrides when switching rows, refreshing resolved clashes, resetting the view, or closing the window.
- **Host & Linked Model Support (`Sources`):** Independently assigns **Current Project** or any loaded `RevitLinkInstance` to **Element A** and **Element B**, transforming linked geometry and bounding boxes into host coordinates.
- **Clash Re-evaluation & History Tracking (`Session`, `Refresh`, `ReportWriter`):**
  - Persists session data, row statuses (`Pending`, `Resolved`, `Failed`), deleted element flags, timestamps, and modification history to `%AppData%\InterferenceIsolator`.
  - Re-evaluates clashes using host-space bounding box overlap and optional **Exact Geometry** intersection checks (including transformed solids across linked models).
  - Exports a formatted HTML progress report containing summary counts and each row's change history.
- **WPF User Interface (`StartWindow`, `MainWindow`, `Ui`):** Provides a report selection dialog and a review window with source selectors, an **Exact Geometry** toggle, status highlighting, view reset, and HTML export.

## Deployment Structure

The add-in uses a subfolder layout under `%AppData%\Autodesk\Revit\Addins\2026\`:

```text
Addins\2026\
├── InterferenceIsolator.addin
└── InterferenceIsolator\
    ├── InterferenceIsolator.dll
    ├── InterferenceIsolator.pdb
    ├── InterferenceIsolator.deps.json
    └── CheckIntersectionFormat.png
