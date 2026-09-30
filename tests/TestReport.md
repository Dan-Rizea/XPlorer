# Browser regression test report

Completed on 2026-10-01 (Europe/Bucharest).

All 86 extended scenarios passed in four browser/configuration combinations: **344 successful scenario executions and 1,660 successful assertions**, including the original smoke suite in each run.

| Browser and detailed report | Configuration | Extended cases | Assertions | 10,000-node layer scan |
|---|---|---|---|---|
| [Chrome 154](Chrome-results.md) | Default security/site isolation | 86/86 | 415 | 28 ms |
| [Chrome 154](Chrome-app-flags-results.md) | XPlorer browser flags | 86/86 | 415 | 29 ms |
| [Chromium 121](BundledChromium-results.md) | Default security/site isolation | 86/86 | 415 | 34 ms |
| [Chromium 121](BundledChromium-app-flags-results.md) | XPlorer browser flags | 86/86 | 415 | 28 ms |

Browser builds: Chrome 154.0.8037.58 and Chromium 121.0.6167.85. The latter is the version supplied for PuppeteerSharp 14. The application and test runner build with zero warnings and zero errors. JavaScript syntax and Git whitespace checks passed.

## Coverage

- Opaque/transparent covers, pointer-events:none, visibility:hidden, opacity:0, display:none, zero-size inputs, disabled/readonly controls, transformed and RTL layouts.
- Fourteen input types, textarea, select, buttons, and true/empty/plaintext-only contenteditable fields.
- IDs containing both quote types, punctuation, spaces, backslashes and Unicode; duplicate IDs; attribute/class/text selectors; hierarchical fallback; SVG and MathML namespaces. Each generated selector is checked against the exact captured node.
- Forward/backward layer cycling, wraparound, multiple overlapping inputs, parent/child/document navigation, tiny pointer jitter, click without a preceding move, Enter capture, native click interception and restoration after cancellation.
- Page/container scrolling, pseudo-element covers, native dialogs/popovers, dialogs opened after inspection starts, and selection overlays after body replacement.
- Dynamic control insertion/replacement, reinjection, 25 rapid start/cancel cycles, navigation, and 10,000-node pages.
- Same-origin, cross-origin, nested, srcdoc, opaque sandboxed, dynamically attached and shadow-hosted frames; origin changes, frame detachment and captured-tab closure.
- Nested/open shadow roots, slotted inputs, hidden controls inside shadow roots, and accessible host selection for closed roots.
- Highlight, click, real native frame hover/CSS state, scroll, get text/value, type and clear; actions retain the captured frame/tab even when another tab is opened. Document-level text-input actions reject the request and preserve the DOM.

## Fixes made after failing tests

- Parent/child navigation now recognizes empty and plaintext-only contenteditable attributes.
- XPath generation handles element namespaces for SVG/MathML.
- Layer cycling refreshes its candidates when page scripts replace controls.
- Hidden controls inside open shadow roots are reachable.
- Highlights use the browser top layer and the active modal's container; removed overlays are restored after body replacement.
- Host frame evaluations have a deadline so context replacement cannot indefinitely stall inspection/cancellation.
- Hover moves the real pointer using the captured element's top-level bounding box, including frame offsets.
- Typing/clearing a selected #document returns an error instead of changing page content.

The harness was also corrected to avoid running App.xaml's browser-launching StartupUri, to initialize all tabs for each inspection session, and to establish hover assertions with valid selectors and deterministic frame-local pointer movement.

## Limits

Edge 154.0.4258.37 could not launch through PuppeteerSharp 14 with either legacy or current headless flags. It is **untested**, not counted as passing. The launcher reported `PuppeteerSharp.ProcessException: Failed to launch browser!` without an additional diagnostic.

Tests use temporary local fixtures and native browser input in headless mode. They do not constitute testing every external website or visual QA of WPF window layout, dragging, pinning, clipboard integration, or interactive browser windows. Closed shadow root internals remain inaccessible; their host can be selected.

## Reproduce

From the repository root:

```powershell
dotnet build tests/InspectorSmokeTests.csproj
& ./tests/bin/Debug/net472/InspectorSmokeTests.exe
& ./tests/bin/Debug/net472/InspectorSmokeTests.exe --app-flags
```

Pass a Chromium executable path as the first argument for another browser build. `--filter="frame hover"` runs matching extended cases and writes a separate filtered report. Full runs generate one browser/configuration-specific report per run.
