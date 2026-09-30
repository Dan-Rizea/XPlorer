# 🎯 XPlorer

**XPlorer** is an ultra-compact, intelligent Web UI Element Inspector and Selector Tool designed for QA engineers, RPA developers, and test automation specialists (Playwright, Selenium, Puppeteer, UiPath).

It provides a floating, responsive desktop widget that connects to a dedicated Chrome instance, allowing you to indicate, inspect, test, and organize web elements in real time.

---

## ✨ Features

### 🔍 Interactive Visual Element Spy
- **Precision Element Snapping:** Hover over any element on the page with a real-time glowing selection box and click to inspect.
- **Layer Selection:** Hover over a covering element and use <kbd>Alt</kbd> + mouse wheel or <kbd>Tab</kbd> / <kbd>Shift+Tab</kbd> to cycle through elements at that location, including covered inputs and controls with `pointer-events: none`. The badge shows the selected layer.
- **DOM Navigation:** <kbd>↑</kbd> selects a parent (up to `#document`); <kbd>↓</kbd> selects a child control. This also reaches hidden inputs within a wrapper. Zero-sized controls highlight their wrapper and are marked as hidden. Click or press <kbd>Enter</kbd> to capture; <kbd>Esc</kbd> cancels.
- **Frame Documents:** Inspect elements inside same-origin, cross-origin, and nested iframe `#document` nodes. Saved actions run in the original tab and frame. Open shadow roots are supported, including nested roots.
- **Optimized Selector Generation:** Automatically generates stable, robust **XPath** and **CSS** selectors.
- **Instant Inline Naming:** As soon as an element is captured, the name field is automatically focused with the text highlighted so you can name your selector instantly and press <kbd>Enter</kbd>.

### ⏱️ Delay Timer (Inspect Dynamic Menus & Tooltips)
- **One-Click Delay Cycling:** Toggle between `0s`, `3s`, `5s`, and `10s` directly from the top toolbar.
- **Countdown Feedback:** Visual countdown in the title bar lets you trigger hover menus, dropdowns, and modal transitions before the element spy activates.

### ⚡ Live Actionability Testing Dock
Test real browser interactions on your selected element with a single click:
- **👆 Click Element:** Executes a real click action.
- **🖐️ Hover Element:** Simulates a 3-second mouse hover to verify hover effects and submenus.
- **↕ Scroll to Element:** Smoothly scrolls the element into view.
- **🔤 Get Text / Value:** Extracts inner text or input values and displays them in the status feed.
- **⌨️ Type / Clear Popup:** A sleek floating input bar directly above the button to type text into elements or clear their values.

### 📦 Saved Selectors Repository
- **Organized Table View:** Keep track of all captured selectors in one place.
- **Quick Row Actions:**
  - 📋 **Copy Path:** Copies the XPath / CSS selector to your clipboard.
  - ✏️ **Rename:** Quickly rename the selector alias.
  - 🗑️ **Delete:** Remove unwanted items.
  - 🧹 **Clear All:** Reset repository in one click.

### 🪟 Compact Floating Design & Mini Mode
- **Custom Frameless Dark Theme:** Designed with sleek slate-dark aesthetics and responsive layout.
- **Live Action History in Title Bar:** Real-time feedback (`Ready`, `⏳ Indicating...`, `✅ Clicked`, etc.) across the top caption bar.
- **🔽 Mini Mode:** Collapse the repository table to shrink XPlorer into an ultra-compact `155px` floating toolbar.
- **📌 Always On Top:** Pin the window so it floats over your browser and IDE.
- **🌐 Bring Chrome to Front:** Quickly focus the connected browser window.
- **Responsive Layout:** Smoothly resize the window from a tiny `180px` widget to full screen with responsive button scaling.

---

## 🚀 Getting Started

### Prerequisites
- Windows 10 / 11
- [.NET Framework 4.7.2](https://dotnet.microsoft.com/download/dotnet-framework/net472) or higher / [.NET SDK](https://dotnet.microsoft.com/download)

### Installation & Run

1. **Clone the repository:**
   ```bash
   git clone https://github.com/Dan-Rizea/XPlorer.git
   cd XPlorer/WebUIExplorer
   ```

2. **Build the project:**
   ```powershell
   dotnet build
   ```

3. **Run XPlorer:**
   ```powershell
   dotnet run
   ```

On the first launch, XPlorer will automatically download a dedicated Chromium browser build via PuppeteerSharp.

---

## 🛠️ Tech Stack

- **Framework:** C# / WPF (.NET Framework 4.7.2)
- **Browser Automation:** [PuppeteerSharp](https://github.com/hardkoded/puppeteer-sharp) (Chrome DevTools Protocol)
- **UI & Styling:** [Material Design in XAML Toolkit](https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit)
- **Serialization:** [Newtonsoft.Json](https://www.newtonsoft.com/json)

### Browser regression checks

On Windows, build and run the headless Chromium smoke tests:

```powershell
dotnet build tests/InspectorSmokeTests.csproj
& ./tests/bin/Debug/net472/InspectorSmokeTests.exe
```

The runner defaults to the installed Google Chrome executable. Pass another Chromium executable path as its first argument if needed. It serves temporary local fixtures and runs the original smoke tests plus 86 extended scenarios covering input types, selectors, dynamic DOM changes, dialogs/popovers, SVG/MathML, frame lifecycles, shadow roots, action behavior, cancellation, and large pages.

Use `--app-flags` to reproduce XPlorer's configured browser flags, or `--filter="frame hover"` to run matching extended scenarios. Each complete run writes a browser-specific Markdown report in `tests/`. Tests use headless browsers and never initialize the application's browser-launching WPF startup window.

---

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
