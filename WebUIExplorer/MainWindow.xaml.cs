using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Runtime.InteropServices;
using PuppeteerSharp;
using Newtonsoft.Json;

namespace WebUIExplorer;

public partial class MainWindow : Window
{
    private IBrowser _browser;
    private bool _isSpying = false;
    private CancellationTokenSource _delayCts;
    private SavedElementItem _selectedElement;
    private readonly ConcurrentDictionary<IPage, bool> _inspectorPages = new ConcurrentDictionary<IPage, bool>();
    private int _spySession;
    private bool _inspectorReady;

    public ObservableCollection<SavedElementItem> SavedElements { get; set; } = new ObservableCollection<SavedElementItem>();

    public class SavedElementItem : INotifyPropertyChanged
    {
        private string _name;
        private string _hierarchy;

        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(nameof(Name)); }
        }

        public string TagName { get; set; }
        public string TagBadge { get; set; }

        public string Hierarchy
        {
            get => _hierarchy;
            set { _hierarchy = value; OnPropertyChanged(nameof(Hierarchy)); }
        }

        public string XPath { get; set; }
        public string Css { get; set; }
        public bool IsShadowDom { get; set; }
        public string FrameId { get; set; }
        [JsonIgnore]
        public IPage CapturedPage { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    public class ElementCaptureResult
    {
        public string TagName { get; set; }
        public string XPath { get; set; }
        public string Css { get; set; }
        public string Playwright { get; set; }
        public string Selenium { get; set; }
        public string Hierarchy { get; set; }
        public bool IsShadowDom { get; set; }
        public string FrameId { get; set; }
        public Dictionary<string, string> Attributes { get; set; } = new Dictionary<string, string>();
    }

    public class ActionResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public int MatchCount { get; set; }
    }

    public class HoverActionResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public decimal X { get; set; }
        public decimal Y { get; set; }
    }

    public class InspectorEvent
    {
        public string Capture { get; set; }
        public bool Cancelled { get; set; }
    }

    public MainWindow()
    {
        InitializeComponent();
        SavedElementsDataGrid.ItemsSource = SavedElements;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusTextBlock.Text = "Connecting or launching browser...";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 193, 7)); // Yellow
            SpyButton.IsEnabled = false;

            _browser = await InitializeBrowserAsync();

            var pages = await _browser.PagesAsync();
            IPage activePage = pages.FirstOrDefault(IsInspectableWebPage) ?? await _browser.NewPageAsync();

            if (activePage != null)
            {
                await EnsureBrowserWindowVisibleAsync(activePage);
            }

            // Hook browser target events so if user opens new pages while spying, we inject into them (ignoring DevTools internal pages)
            _browser.TargetCreated += async (s, ev) =>
            {
                if (_isSpying)
                {
                    try
                    {
                        var target = ev.Target;
                        if (target != null && target.Type == TargetType.Page)
                        {
                            var page = await target.PageAsync();
                            if (page != null && IsInspectableWebPage(page))
                            {
                                await SetupPageForSpyingAsync(page);
                            }
                        }
                    }
                    catch { }
                }
            };

            StatusTextBlock.Text = "Ready. Open any website in the browser.";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // Green
            SpyButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "Error: " + ex.Message;
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(244, 67, 54)); // Red
            MessageBox.Show($"Failed to initialize browser: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task<IBrowser> InitializeBrowserAsync()
    {
        string debugUrl = "http://127.0.0.1:9222";

        // 1. Try to connect to an already running browser on the debugging port
        try
        {
            var existingBrowser = await Puppeteer.ConnectAsync(new ConnectOptions
            {
                BrowserURL = debugUrl,
                DefaultViewport = null
            });
            if (existingBrowser != null && !existingBrowser.IsClosed)
            {
                return existingBrowser;
            }
        }
        catch { }

        // 2. Ensure Chromium is downloaded
        StatusTextBlock.Text = "Checking Chromium binary...";
        var browserFetcher = new BrowserFetcher();
        await browserFetcher.DownloadAsync();

        StatusTextBlock.Text = "Launching browser...";
        string userDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XPlorer", "BrowserData");
        Directory.CreateDirectory(userDataDir);

        // 3. Try launching with persistent UserDataDir and remote-debugging-port
        try
        {
            return await Puppeteer.LaunchAsync(new LaunchOptions
            {
                Headless = false,
                DefaultViewport = null,
                UserDataDir = userDataDir,
                Args = new[]
                {
                    "--remote-debugging-port=9222",
                    "--disable-blink-features=AutomationControlled",
                    "--disable-web-security",
                    "--disable-site-isolation-trials",
                    "--disable-features=IsolateOrigins,site-per-process",
                    "--start-maximized"
                },
                IgnoreDefaultArgs = false
            });
        }
        catch
        {
            // If locked or in use, try connecting to debugUrl once more
            try
            {
                return await Puppeteer.ConnectAsync(new ConnectOptions 
                { 
                    BrowserURL = debugUrl, 
                    DefaultViewport = null 
                });
            }
            catch
            {
                // Fallback: Launch without fixed user-data-dir so it always succeeds even if other instances are open
                return await Puppeteer.LaunchAsync(new LaunchOptions
                {
                    Headless = false,
                    DefaultViewport = null,
                    Args = new[]
                    {
                        "--remote-debugging-port=9222",
                        "--disable-blink-features=AutomationControlled",
                        "--disable-web-security",
                        "--disable-site-isolation-trials",
                        "--disable-features=IsolateOrigins,site-per-process",
                        "--start-maximized"
                    }
                });
            }
        }
    }

    private static bool IsInspectableWebPage(IPage page)
    {
        if (page == null || page.IsClosed) return false;
        try
        {
            string url = page.Url ?? "";
            if (string.IsNullOrWhiteSpace(url)) return true;
            if (url.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("chrome-devtools://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("edge://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("view-source:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<IPage> GetActivePageAsync()
    {
        if (_browser == null || _browser.IsClosed) return null;
        try
        {
            var pages = await _browser.PagesAsync();
            if (pages == null || pages.Length == 0)
            {
                return await _browser.NewPageAsync();
            }

            // Prefer the active/last opened genuine web page (excluding DevTools internal panels)
            var validPages = pages.Where(IsInspectableWebPage).ToArray();
            var openPage = validPages.LastOrDefault(p => !p.IsClosed);
            if (openPage != null) return openPage;

            // If only DevTools or closed pages exist, create/return an inspectable page
            return await _browser.NewPageAsync();
        }
        catch
        {
            return null;
        }
    }

    private async Task EnsureBrowserWindowVisibleAsync(IPage page)
    {
        if (page == null || page.IsClosed || !IsInspectableWebPage(page)) return;
        try
        {
            await page.SetViewportAsync(null);
            var cdp = await page.Target.CreateCDPSessionAsync();
            try
            {
                await cdp.SendAsync("Emulation.clearDeviceMetricsOverride");
            }
            catch { }

            var res = await cdp.SendAsync<Newtonsoft.Json.Linq.JObject>("Browser.getWindowForTarget");
            if (res != null && res["windowId"] != null)
            {
                long windowId = (long)res["windowId"];
                string state = res["bounds"]?["windowState"]?.ToString();
                if (state == "minimized")
                {
                    await cdp.SendAsync("Browser.setWindowBounds", new
                    {
                        windowId = windowId,
                        bounds = new { windowState = "normal" }
                    });
                }
            }
            await page.BringToFrontAsync();
        }
        catch { }
    }

    private int _delaySeconds = 0;
    private readonly int[] _delays = { 0, 3, 5, 10 };
    private int _delayIndex = 0;

    private void TimerCycleButton_Click(object sender, RoutedEventArgs e)
    {
        _delayIndex = (_delayIndex + 1) % _delays.Length;
        _delaySeconds = _delays[_delayIndex];
        if (TimerText != null) TimerText.Text = _delaySeconds == 0 ? "0s" : $"{_delaySeconds}s";
        if (TimerCycleButton != null) TimerCycleButton.ToolTip = $"Delay Timer: {_delaySeconds}s (Click to cycle 0s -> 3s -> 5s -> 10s)";
        StatusTextBlock.Text = $"Delay timer: {_delaySeconds}s.";
    }

    private async void SpyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_browser == null) return;

        if (!_isSpying)
        {
            await StartSpyingWithDelayAsync();
        }
        else
        {
            await StopSpyingAsync(userCancelled: true);
        }
    }

    private async Task StartSpyingWithDelayAsync()
    {
        int delaySeconds = _delaySeconds;

        if (delaySeconds > 0)
        {
            _isSpying = true;
            _delayCts = new CancellationTokenSource();
            var token = _delayCts.Token;

            if (SpyIcon != null)
            {
                SpyIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.Close;
                SpyIcon.Foreground = new SolidColorBrush(Color.FromRgb(255, 255, 255));
            }
            if (SpyButton != null)
            {
                SpyButton.Background = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red
                SpyButton.ToolTip = "Cancel Indicating (ESC)";
            }
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 152, 0)); // Orange

            // Bring browser to front so user can interact freely (open dropdowns, etc.)
            var activePage = await GetActivePageAsync();
            if (activePage != null)
            {
                try { await activePage.BringToFrontAsync(); } catch { }
            }

            try
            {
                for (int i = delaySeconds; i > 0; i--)
                {
                    StatusTextBlock.Text = $"⏳ Indicating in {i}s... Prepare your UI";
                    await Task.Delay(1000, token);
                }
            }
            catch (TaskCanceledException)
            {
                await StopSpyingAsync(userCancelled: true);
                return;
            }
        }

        await StartSpyingAsync();
    }

    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 && !_isSpying && _browser != null)
        {
            if (_delaySeconds == 0)
            {
                _delayIndex = 1;
                _delaySeconds = 3;
                if (TimerText != null) TimerText.Text = "3s";
            }
            await StartSpyingWithDelayAsync();
        }
        else if (e.Key == Key.Escape && _isSpying)
        {
            await StopSpyingAsync(userCancelled: true);
        }
    }

    private async Task StartSpyingAsync()
    {
        try
        {
            _isSpying = true;
            _spySession++;
            _inspectorReady = true;

            // Update UI state to Cancel mode
            if (SpyIcon != null)
            {
                SpyIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.Close;
                SpyIcon.Foreground = new SolidColorBrush(Color.FromRgb(255, 255, 255));
            }
            if (SpyButton != null)
            {
                SpyButton.Background = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red
                SpyButton.ToolTip = "Cancel Indicating (ESC)";
            }
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 152, 0)); // Orange
            StatusTextBlock.Text = "Hover; Alt+Wheel/Tab: layers; Up/Down: parent/child; click to save.";

            var pages = await _browser.PagesAsync();
            var validPages = pages.Where(IsInspectableWebPage).ToArray();
            if (validPages.Length == 0)
            {
                var newPage = await _browser.NewPageAsync();
                validPages = new[] { newPage };
            }

            // Bring active inspected web page to front
            var activePage = validPages.LastOrDefault(p => !p.IsClosed) ?? validPages.First();
            try { await activePage.BringToFrontAsync(); } catch { }

            // Inject only into genuine open web pages (skip devtools)
            foreach (var page in validPages.Where(p => !p.IsClosed))
            {
                await SetupPageForSpyingAsync(page);
            }
            _ = PollInspectorEventsAsync(_spySession);
        }
        catch (Exception ex)
        {
            await StopSpyingAsync();
            MessageBox.Show($"Failed to start indicator: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task SetupPageForSpyingAsync(IPage page)
    {
        if (!_inspectorReady || page == null || page.IsClosed || !IsInspectableWebPage(page)) return;

        try
        {
            if (_inspectorPages.TryAdd(page, true))
            {
                page.FrameNavigated += async (sender, args) =>
                {
                    if (_inspectorReady) await SetupFrameForSpyingAsync(args.Frame, _spySession);
                };
                page.FrameAttached += async (sender, args) =>
                {
                    if (_inspectorReady) await SetupFrameForSpyingAsync(args.Frame, _spySession);
                };
                page.Close += (sender, args) => _inspectorPages.TryRemove(page, out _);
            }

            // DevTools' #document nodes belong to separate execution contexts, including
            // cross-origin and nested frames. Install locally rather than piercing from the top.
            int session = _spySession;
            await Task.WhenAll(page.Frames.Select(frame => SetupFrameForSpyingAsync(frame, session)));
        }
        catch { }
    }

    private async Task SetupFrameForSpyingAsync(IFrame frame, int session)
    {
        if (!_inspectorReady || session != _spySession || frame.Detached) return;
        try
        {
            string script = GetInspectorScript();
            await WithInspectorTimeoutAsync(frame.EvaluateExpressionAsync(
                "window._uiExpFrameId = " + JsonConvert.SerializeObject(frame.Id) + ";\n" +
                "window._uiExpSession = " + session + ";\n" + script));
            // A cancellation/capture can happen while evaluation is in flight.
            if (!_inspectorReady || session != _spySession)
                await WithInspectorTimeoutAsync(frame.EvaluateExpressionAsync("if (window._uiExpSession === " + session + " && window._uiExpCleanup) window._uiExpCleanup();"));
        }
        catch { /* A frame may detach or replace its execution context during navigation. */ }
    }

    private static async Task WithInspectorTimeoutAsync(Task task)
    {
        if (await Task.WhenAny(task, Task.Delay(1000)) != task)
        {
            // Context replacement can leave an old Puppeteer evaluation pending forever.
            _ = task.ContinueWith(t => { var observed = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            throw new TimeoutException("The frame execution context is changing.");
        }
        await task;
    }

    private async Task PollInspectorEventsAsync(int session)
    {
        while (_inspectorReady && session == _spySession)
        {
            try
            {
                var pages = await _browser.PagesAsync();
                foreach (var page in pages.Where(IsInspectableWebPage))
                {
                    var events = await Task.WhenAll(page.Frames.Select(async frame =>
                    {
                        try
                        {
                            var read = frame.EvaluateExpressionAsync<InspectorEvent>(@"({
                                Capture: window._uiExpCapture || null,
                                Cancelled: !!window._uiExpCancelled
                            })");
                            await WithInspectorTimeoutAsync(read);
                            return await read;
                        }
                        catch { return null; }
                    }));
                    if (!_inspectorReady || session != _spySession) return;
                    var result = events.FirstOrDefault(ev => ev != null && (ev.Capture != null || ev.Cancelled));
                    if (result != null)
                    {
                        await StopSpyingAsync(userCancelled: result.Capture == null);
                        if (result.Capture != null) ProcessCapturedElement(result.Capture, page);
                        return;
                    }
                }
            }
            catch { /* The browser can close while indication is active. */ }
            await Task.Delay(100);
        }
    }

    private async Task StopSpyingAsync(bool userCancelled = false)
    {
        _isSpying = false;
        _inspectorReady = false;

        if (_delayCts != null)
        {
            try { _delayCts.Cancel(); _delayCts.Dispose(); } catch { }
            _delayCts = null;
        }

        Dispatcher.Invoke(new Action(() =>
        {
            if (SpyIcon != null)
            {
                SpyIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.CrosshairsGps;
                SpyIcon.Foreground = new SolidColorBrush(Color.FromRgb(0, 0, 0));
            }
            if (SpyButton != null)
            {
                SpyButton.Background = new SolidColorBrush(Color.FromRgb(255, 255, 255)); // White
                SpyButton.ToolTip = "Indicate Element: Alt+Wheel or Tab cycles layers; Up/Down selects parent/child; Click or Enter captures; Esc cancels.";
            }

            if (userCancelled)
            {
                StatusTextBlock.Text = "Inspection cancelled.";
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(158, 158, 158)); // Grey
            }
            else
            {
                StatusTextBlock.Text = "Element saved to repository!";
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94)); // Green
            }

            // Bring WebUIExplorer to front
            this.Activate();
            this.Focus();
        }));

        if (_browser != null)
        {
            try
            {
                var pages = await _browser.PagesAsync();
                string cleanupJs = @"
                    if (window._uiExpCleanup) {
                        window._uiExpCleanup();
                    }
                ";
                foreach (var page in pages.Where(p => !p.IsClosed))
                {
                    foreach (var frame in page.Frames)
                    {
                        try { await WithInspectorTimeoutAsync(frame.EvaluateExpressionAsync(cleanupJs)); } catch { }
                    }
                }
            }
            catch { }
        }
    }

    private void ProcessCapturedElement(string json, IPage capturedPage)
    {
        try
        {
            var result = JsonConvert.DeserializeObject<ElementCaptureResult>(json);
            if (result == null) return;

            // Generate smart default name
            string defaultName = "";
            if (result.Attributes.ContainsKey("ID") && !string.IsNullOrWhiteSpace(result.Attributes["ID"]))
            {
                defaultName = result.Attributes["ID"].Trim();
            }
            else if (result.Attributes.ContainsKey("Name") && !string.IsNullOrWhiteSpace(result.Attributes["Name"]))
            {
                defaultName = $"{result.TagName.ToLower()}_{result.Attributes["Name"].Trim()}";
            }
            else if (result.Attributes.ContainsKey("Data TestID") && !string.IsNullOrWhiteSpace(result.Attributes["Data TestID"]))
            {
                defaultName = result.Attributes["Data TestID"].Trim();
            }
            else if (result.Attributes.ContainsKey("Inner Text") && !string.IsNullOrWhiteSpace(result.Attributes["Inner Text"]))
            {
                string text = new string(result.Attributes["Inner Text"].Where(c => char.IsLetterOrDigit(c) || c == '_').Take(20).ToArray());
                defaultName = !string.IsNullOrEmpty(text) ? $"{result.TagName.ToLower()}_{text}" : $"{result.TagName.ToLower()}_{SavedElements.Count + 1}";
            }
            else
            {
                defaultName = $"{result.TagName.ToLower()}_{SavedElements.Count + 1}";
            }

            string badge = $"<{result.TagName.ToLower()}>";
            if (result.IsShadowDom)
            {
                badge += " ⚡";
            }

            var newItem = new SavedElementItem
            {
                Name = defaultName,
                TagName = result.TagName,
                TagBadge = badge,
                Hierarchy = result.Hierarchy ?? "",
                XPath = result.XPath ?? "",
                Css = result.Css ?? "",
                IsShadowDom = result.IsShadowDom,
                FrameId = result.FrameId,
                CapturedPage = capturedPage
            };

            SavedElements.Add(newItem);
            SavedSelectorsTitleBlock.Text = $"Saved Selectors ({SavedElements.Count} elements)";

            SavedElementsDataGrid.SelectedItem = newItem;

            StatusTextBlock.Text = $"Saved '{newItem.Name}'. Type to rename (Enter to save).";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94));

            FocusItemNameBox(newItem);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error displaying element: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void FocusItemNameBox(SavedElementItem item)
    {
        if (item == null) return;
        
        if (SavedSelectorsCard.Visibility != Visibility.Visible)
        {
            ToggleSavedSelectors_Click(null, null);
        }

        SavedElementsDataGrid.SelectedItem = item;
        SavedElementsDataGrid.ScrollIntoView(item);

        this.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                this.Activate();
                await Task.Delay(80);

                var row = (DataGridRow)SavedElementsDataGrid.ItemContainerGenerator.ContainerFromItem(item);
                if (row != null)
                {
                    var textBox = FindVisualChild<TextBox>(row);
                    if (textBox != null)
                    {
                        textBox.Focus();
                        textBox.SelectAll();
                    }
                }
            }
            catch { }
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null) return null;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild) return typedChild;
            var descendant = FindVisualChild<T>(child);
            if (descendant != null) return descendant;
        }
        return null;
    }

    private void ElementNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Keyboard.ClearFocus();
            StatusTextBlock.Text = $"Name saved: '{_selectedElement?.Name}'. Ready.";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94));
            e.Handled = true;
        }
    }

    private void SavedElementsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SavedElementsDataGrid.SelectedItem is SavedElementItem item)
        {
            _selectedElement = item;
            StatusTextBlock.Text = $"Selected '{item.Name}'. Ready.";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94));
        }
    }

    private void RowCopy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is SavedElementItem item)
        {
            string selector = !string.IsNullOrEmpty(item.Hierarchy) 
                ? item.Hierarchy 
                : (!string.IsNullOrEmpty(item.Css) ? item.Css : item.XPath);

            if (!string.IsNullOrEmpty(selector))
            {
                Clipboard.SetText(selector);
                StatusTextBlock.Text = $"Copied '{item.Name}' selector to clipboard!";
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94));
            }
        }
    }

    private void RowEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is SavedElementItem item)
        {
            _selectedElement = item;
            FocusItemNameBox(item);
            StatusTextBlock.Text = $"Editing '{item.Name}'. Type name and press Enter.";
        }
    }

    private void RowPlay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is SavedElementItem item)
        {
            _selectedElement = item;
            SavedElementsDataGrid.SelectedItem = item;
            ValidateButton_Click(this, new RoutedEventArgs());
        }
    }

    private void RowDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is SavedElementItem item)
        {
            SavedElements.Remove(item);
            SavedSelectorsTitleBlock.Text = $"Saved Selectors ({SavedElements.Count} elements)";
            if (_selectedElement == item)
            {
                _selectedElement = SavedElements.LastOrDefault();
                SavedElementsDataGrid.SelectedItem = _selectedElement;
            }
            StatusTextBlock.Text = $"Deleted '{item.Name}'.";
        }
    }

    private void ClearAllElements_Click(object sender, RoutedEventArgs e)
    {
        SavedElements.Clear();
        _selectedElement = null;
        SavedSelectorsTitleBlock.Text = "Saved Selectors (0 elements)";
        StatusTextBlock.Text = "Repository cleared.";
        StatusDot.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94));
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private void FocusProcessWindow(int processId)
    {
        if (processId <= 0) return;

        EnumWindows((hWnd, lParam) =>
        {
            if (IsWindowVisible(hWnd))
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == processId)
                {
                    if (IsIconic(hWnd))
                    {
                        ShowWindow(hWnd, 9); // SW_RESTORE
                    }
                    SetForegroundWindow(hWnd);
                    return false; // Found window, stop enumeration
                }
            }
            return true;
        }, IntPtr.Zero);
    }

    private async void ChromeButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_browser == null || _browser.IsClosed)
            {
                StatusTextBlock.Text = "Reconnecting to browser...";
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 193, 7));
                _browser = await InitializeBrowserAsync();
            }

            var page = await GetActivePageAsync();
            if (page != null && !page.IsClosed)
            {
                await EnsureBrowserWindowVisibleAsync(page);
            }

            // Restore and foreground only the specific browser instance managed by XPlorer
            int browserPid = _browser?.Process?.Id ?? 0;
            if (browserPid > 0)
            {
                FocusProcessWindow(browserPid);
            }

            StatusTextBlock.Text = "Chrome browser brought to forefront.";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94));
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "Could not activate Chrome: " + ex.Message;
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(244, 67, 54));
        }
    }

    private double _expandedHeight = 490;

    private void ToggleSavedSelectors_Click(object sender, RoutedEventArgs e)
    {
        if (SavedSelectorsCard.Visibility == Visibility.Visible)
        {
            // Collapse panel & enter mini compact mode
            _expandedHeight = this.Height > 200 ? this.Height : 460;
            SavedSelectorsCard.Visibility = Visibility.Collapsed;
            MiddleSeparatorRow1.Height = new GridLength(0);
            MiddleSeparatorRow2.Height = new GridLength(8);
            SavedSelectorsRow.Height = new GridLength(0);
            
            this.MinHeight = 150;
            this.Height = 155;

            if (TogglePanelDockIcon != null) TogglePanelDockIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.ChevronDoubleUp;
            StatusTextBlock.Text = "Mini mode: Selectors hidden.";
        }
        else
        {
            // Expand panel
            SavedSelectorsCard.Visibility = Visibility.Visible;
            MiddleSeparatorRow1.Height = new GridLength(10);
            MiddleSeparatorRow2.Height = new GridLength(10);
            SavedSelectorsRow.Height = new GridLength(1, GridUnitType.Star);

            this.MinHeight = 250;
            this.Height = _expandedHeight;

            if (TogglePanelDockIcon != null) TogglePanelDockIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.ChevronDoubleDown;
            StatusTextBlock.Text = "Selectors panel expanded.";
        }
    }

    private void PinWindowButton_Click(object sender, RoutedEventArgs e)
    {
        this.Topmost = !this.Topmost;
        
        if (this.Topmost)
        {
            if (PinWindowIcon != null)
            {
                PinWindowIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.Pin;
                PinWindowIcon.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248)); // Blue
            }
            if (PinWindowButton != null)
            {
                PinWindowButton.ToolTip = "Window Pinned (Always on Top). Click to unpin.";
            }
            StatusTextBlock.Text = "Window pinned (Always on Top).";
        }
        else
        {
            if (PinWindowIcon != null)
            {
                PinWindowIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.PinOffOutline;
                PinWindowIcon.Foreground = new SolidColorBrush(Color.FromRgb(143, 150, 163)); // Subtle Gray
            }
            if (PinWindowButton != null)
            {
                PinWindowButton.ToolTip = "Window Unpinned. Click to pin on top.";
            }
            StatusTextBlock.Text = "Window unpinned from top.";
        }
    }

    #region Custom Title Bar Handlers

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
    {
        this.WindowState = WindowState.Minimized;
    }

    private void MaximizeWindow_Click(object sender, RoutedEventArgs e)
    {
        if (this.WindowState == WindowState.Maximized)
        {
            this.WindowState = WindowState.Normal;
            if (MaximizeIcon != null) MaximizeIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.WindowMaximize;
        }
        else
        {
            this.WindowState = WindowState.Maximized;
            if (MaximizeIcon != null) MaximizeIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.WindowRestore;
        }
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        this.Close();
    }

    #endregion

    #region Actionability Testing

    private (string xpath, string css) GetActiveSelectors()
    {
        string xpath = _selectedElement?.XPath ?? "";
        string css = _selectedElement?.Css ?? "";
        return (xpath, css);
    }

    private Task<IPage> GetActionPageAsync()
    {
        return _selectedElement?.CapturedPage != null
            ? Task.FromResult(_selectedElement.CapturedPage)
            : GetActivePageAsync();
    }

    private IFrame GetActionFrame(IPage page)
    {
        if (string.IsNullOrEmpty(_selectedElement?.FrameId)) return page.MainFrame;
        var frame = page.Frames.FirstOrDefault(f => f.Id == _selectedElement.FrameId && !f.Detached);
        if (frame == null) throw new InvalidOperationException("The captured frame is no longer available. Indicate the element again.");
        return frame;
    }

    private async Task ExecuteActionInBrowserAsync(string actionName, string jsFunction)
    {
        var (xpath, css) = GetActiveSelectors();
        if (string.IsNullOrEmpty(xpath) && string.IsNullOrEmpty(css))
        {
            StatusTextBlock.Text = "⚠️ Please select an element from repository first.";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 152, 0));
            return;
        }

        var page = await GetActionPageAsync();
        if (page == null || page.IsClosed)
        {
            StatusTextBlock.Text = "❌ No active browser tab found.";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(244, 67, 54));
            return;
        }

        try
        {
            string targetName = _selectedElement?.Name ?? "element";
            StatusTextBlock.Text = $"Executing {actionName} on '{targetName}'...";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 193, 7));

            var resultJson = await GetActionFrame(page).EvaluateFunctionAsync<string>(jsFunction, xpath, css);
            var result = JsonConvert.DeserializeObject<ActionResult>(resultJson);

            if (result.Success)
            {
                StatusTextBlock.Text = $"✅ {result.Message}";
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94));
            }
            else
            {
                StatusTextBlock.Text = $"❌ {result.Message}";
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(244, 67, 54));
            }
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"❌ {actionName} failed: {ex.Message}";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(244, 67, 54));
        }
    }

    private async void ValidateButton_Click(object sender, RoutedEventArgs e)
    {
        string js = @"(xpath, css) => {
            function findDeep(sel) {
                // The >>> segments explicitly identify each shadow host, including nested roots.
                let root = document;
                let el = null;
                for (const part of sel.split(' >>> ')) {
                    el = root.querySelector(part);
                    if (!el) return null;
                    root = el.shadowRoot;
                }
                return el;
            }

            let el = null;
            let count = 0;
            if (xpath && !xpath.includes('[Shadow DOM')) {
                try {
                    let res = document.evaluate(xpath, document, null, XPathResult.ORDERED_NODE_SNAPSHOT_TYPE, null);
                    count = res.snapshotLength;
                    if (count > 0) el = res.snapshotItem(0);
                } catch(e) {}
            }
            
            if (!el && css) {
                try {
                    let list = css.includes(' >>> ') ? [findDeep(css)].filter(Boolean) : document.querySelectorAll(css);
                    count = list.length;
                    if (count > 0) el = list[0];
                    if (!el) {
                        el = findDeep(css);
                        if (el) count = 1;
                    }
                } catch(e) {}
            }

            if (el && el.nodeType === 9) el = el.documentElement;

            if (!el || count === 0) {
                return JSON.stringify({ success: false, message: '0 elements match selector.', matchCount: 0 });
            }

            el.scrollIntoView({ behavior: 'smooth', block: 'center' });

            // Flash highlight effect
            let origOutline = el.style.outline;
            let origBoxShadow = el.style.boxShadow;
            el.style.outline = '3px solid #00E676';
            el.style.boxShadow = '0 0 15px #00E676';
            setTimeout(() => {
                el.style.outline = origOutline;
                el.style.boxShadow = origBoxShadow;
            }, 1800);

            let uniqueText = count === 1 ? '(Unique match)' : '(' + count + ' matches found!)';
            let tag = el.tagName.toLowerCase();
            return JSON.stringify({ 
                success: true, 
                message: 'Found <' + tag + '> ' + uniqueText, 
                matchCount: count 
            });
        }";

        await ExecuteActionInBrowserAsync("Highlight", js);
    }

    private async void TestClickButton_Click(object sender, RoutedEventArgs e)
    {
        string js = @"(xpath, css) => {
            function findDeep(sel) {
                // The >>> segments explicitly identify each shadow host, including nested roots.
                let root = document;
                let el = null;
                for (const part of sel.split(' >>> ')) {
                    el = root.querySelector(part);
                    if (!el) return null;
                    root = el.shadowRoot;
                }
                return el;
            }

            let el = null;
            if (xpath && !xpath.includes('[Shadow DOM')) {
                try {
                    let res = document.evaluate(xpath, document, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null);
                    el = res.singleNodeValue;
                } catch(e) {}
            }
            if (!el && css) {
                try { el = findDeep(css); } catch(e) {}
            }

            if (el && el.nodeType === 9) el = el.documentElement;

            if (!el) return JSON.stringify({ success: false, message: 'Element not found to click.' });

            el.scrollIntoView({ behavior: 'auto', block: 'center' });
            
            // Trigger visual click ripple
            let origBg = el.style.backgroundColor;
            el.style.backgroundColor = 'rgba(41, 182, 246, 0.4)';
            setTimeout(() => { el.style.backgroundColor = origBg; }, 300);

            el.click();
            return JSON.stringify({ success: true, message: 'Clicked <' + el.tagName.toLowerCase() + '> successfully!' });
        }";

        await ExecuteActionInBrowserAsync("Click", js);
    }

    private async void TestHoverButton_Click(object sender, RoutedEventArgs e)
    {
        var (xpath, css) = GetActiveSelectors();
        if (string.IsNullOrEmpty(xpath) && string.IsNullOrEmpty(css))
        {
            StatusTextBlock.Text = "⚠️ Please select an element from repository first.";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 152, 0));
            return;
        }

        var page = await GetActionPageAsync();
        if (page == null || page.IsClosed)
        {
            StatusTextBlock.Text = "❌ No active browser tab found.";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(244, 67, 54));
            return;
        }

        try
        {
            StatusTextBlock.Text = "Hovering for 3 seconds...";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(56, 189, 248)); // Sky Blue

            string js = @"async (xpath, css) => {
                function findDeep(sel) {
                    // The >>> segments explicitly identify each shadow host, including nested roots.
                    let root = document;
                    let el = null;
                    for (const part of sel.split(' >>> ')) {
                        el = root.querySelector(part);
                        if (!el) return null;
                        root = el.shadowRoot;
                    }
                    return el;
                }

                let el = null;
                if (xpath && !xpath.includes('[Shadow DOM')) {
                    try {
                        let res = document.evaluate(xpath, document, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null);
                        el = res.singleNodeValue;
                    } catch(e) {}
                }
                if (!el && css) {
                    try { el = findDeep(css); } catch(e) {}
                }

                if (el && el.nodeType === 9) el = el.documentElement;

                if (!el) return JSON.stringify({ success: false, message: 'Element not found to hover.' });

                el.scrollIntoView({ behavior: 'auto', block: 'center' });
                await new Promise(r => setTimeout(r, 60));
                
                let rect = el.getBoundingClientRect();
                let centerX = rect.left + rect.width / 2;
                let centerY = rect.top + rect.height / 2;

                // Dispatch synthetic events
                let mouseOpts = { bubbles: true, cancelable: true, clientX: centerX, clientY: centerY, view: window };
                el.dispatchEvent(new PointerEvent('pointerover', mouseOpts));
                el.dispatchEvent(new PointerEvent('pointerenter', mouseOpts));
                el.dispatchEvent(new MouseEvent('mouseover', mouseOpts));
                el.dispatchEvent(new MouseEvent('mouseenter', mouseOpts));
                el.dispatchEvent(new MouseEvent('mousemove', mouseOpts));

                // Visual hover indicator
                let origOutline = el.style.outline;
                let origBoxShadow = el.style.boxShadow;
                el.style.outline = '3px solid #38BDF8';
                el.style.boxShadow = '0 0 15px rgba(56, 189, 248, 0.6)';

                let interval = setInterval(() => {
                    el.dispatchEvent(new MouseEvent('mousemove', mouseOpts));
                }, 200);

                setTimeout(() => {
                    clearInterval(interval);
                    el.style.outline = origOutline;
                    el.style.boxShadow = origBoxShadow;
                }, 3000);

                return JSON.stringify({ 
                    success: true, 
                    message: 'Hovering over <' + el.tagName.toLowerCase() + '> for 3 seconds.',
                    x: centerX,
                    y: centerY
                });
            }";

            var resultJson = await GetActionFrame(page).EvaluateFunctionAsync<string>(js, xpath, css);
            var result = JsonConvert.DeserializeObject<HoverActionResult>(resultJson);

            if (result != null && result.Success)
            {
                // Puppeteer's element box is expressed in top-level coordinates,
                // including nested/cross-origin frame offsets. Native pointer movement
                // is required for CSS :hover; dispatching events alone is insufficient.
                var handle = await GetActionFrame(page).EvaluateFunctionHandleAsync(@"(xpath, css) => {
                    let el = null;
                    if (xpath) {
                        try { el = document.evaluate(xpath, document, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null).singleNodeValue; } catch (_) {}
                    }
                    if (!el && css) {
                        let root = document;
                        for (const part of css.split(' >>> ')) {
                            el = root.querySelector(part);
                            if (!el) return null;
                            root = el.shadowRoot;
                        }
                    }
                    return el && el.nodeType === 9 ? el.documentElement : el;
                }", xpath, css);
                try
                {
                    if (handle is IElementHandle element)
                    {
                        var box = await element.BoundingBoxAsync();
                        if (box != null)
                        {
                            await page.BringToFrontAsync();
                            await page.Mouse.MoveAsync(box.X + box.Width / 2, box.Y + box.Height / 2);
                        }
                    }
                }
                finally { await handle.DisposeAsync(); }

                StatusTextBlock.Text = $"✅ {result.Message}";
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94));
            }
            else
            {
                StatusTextBlock.Text = $"❌ {result?.Message ?? "Hover failed"}";
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(244, 67, 54));
            }
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"❌ Hover failed: {ex.Message}";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(244, 67, 54));
        }
    }

    private async void TestScrollButton_Click(object sender, RoutedEventArgs e)
    {
        string js = @"(xpath, css) => {
            function findDeep(sel) {
                // The >>> segments explicitly identify each shadow host, including nested roots.
                let root = document;
                let el = null;
                for (const part of sel.split(' >>> ')) {
                    el = root.querySelector(part);
                    if (!el) return null;
                    root = el.shadowRoot;
                }
                return el;
            }

            let el = null;
            if (xpath && !xpath.includes('[Shadow DOM')) {
                try {
                    let res = document.evaluate(xpath, document, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null);
                    el = res.singleNodeValue;
                } catch(e) {}
            }
            if (!el && css) {
                try { el = findDeep(css); } catch(e) {}
            }

            if (el && el.nodeType === 9) el = el.documentElement;

            if (!el) return JSON.stringify({ success: false, message: 'Element not found on page to scroll to.' });

            // Target element to scroll - if SVG child, use its parent element
            let scrollTarget = el;
            if (scrollTarget instanceof SVGElement && !(scrollTarget instanceof SVGSVGElement)) {
                scrollTarget = scrollTarget.closest('svg') || scrollTarget.parentElement || scrollTarget;
            }

            // 1. Scroll any scrollable parent containers (divs with overflow: auto/scroll)
            let parent = scrollTarget.parentElement;
            while (parent && parent !== document.body && parent !== document.documentElement) {
                let style = window.getComputedStyle(parent);
                let overflowY = style.overflowY || style.overflow;
                let overflowX = style.overflowX || style.overflow;
                let isScrollableY = (overflowY === 'auto' || overflowY === 'scroll' || overflowY === 'overlay') && parent.scrollHeight > parent.clientHeight;
                let isScrollableX = (overflowX === 'auto' || overflowX === 'scroll' || overflowX === 'overlay') && parent.scrollWidth > parent.clientWidth;

                if (isScrollableY || isScrollableX) {
                    let parentRect = parent.getBoundingClientRect();
                    let elRect = scrollTarget.getBoundingClientRect();
                    if (isScrollableY) {
                        let targetScrollTop = parent.scrollTop + (elRect.top - parentRect.top) - (parent.clientHeight / 2) + (elRect.height / 2);
                        parent.scrollTo({ top: targetScrollTop, behavior: 'smooth' });
                    }
                    if (isScrollableX) {
                        let targetScrollLeft = parent.scrollLeft + (elRect.left - parentRect.left) - (parent.clientWidth / 2) + (elRect.width / 2);
                        parent.scrollTo({ left: targetScrollLeft, behavior: 'smooth' });
                    }
                }
                parent = parent.parentElement;
            }

            // 2. Standard and fallback window scrolling
            try {
                scrollTarget.scrollIntoView({ behavior: 'smooth', block: 'center', inline: 'center' });
            } catch(e) {
                try {
                    scrollTarget.scrollIntoView(true);
                } catch(e2) {}
            }

            // 3. Fallback absolute window coordinate scroll if window did not center it
            let rect = scrollTarget.getBoundingClientRect();
            let absoluteY = window.pageYOffset + rect.top - (window.innerHeight / 2) + (rect.height / 2);
            let absoluteX = window.pageXOffset + rect.left - (window.innerWidth / 2) + (rect.width / 2);
            if (Math.abs(rect.top - window.innerHeight / 2) > 100) {
                try {
                    window.scrollTo({ top: Math.max(0, absoluteY), left: Math.max(0, absoluteX), behavior: 'smooth' });
                } catch(e) {}
            }

            // 4. Flash visual amber highlight glow so the user sees exactly where the element is
            let origOutline = el.style.outline;
            let origBoxShadow = el.style.boxShadow;
            let origTransition = el.style.transition;
            el.style.transition = 'all 0.2s ease-in-out';
            el.style.outline = '3px solid #F59E0B';
            el.style.boxShadow = '0 0 20px rgba(245, 158, 11, 0.8)';
            setTimeout(() => {
                el.style.outline = origOutline;
                el.style.boxShadow = origBoxShadow;
                el.style.transition = origTransition;
            }, 1800);

            let tag = el.tagName.toLowerCase();
            let idAttr = el.id ? '#' + el.id : '';
            return JSON.stringify({
                success: true,
                message: 'Scrolled <' + tag + idAttr + '> into center view.'
            });
        }";

        await ExecuteActionInBrowserAsync("Scroll To", js);
    }

    private async void TestGetTextButton_Click(object sender, RoutedEventArgs e)
    {
        string js = @"(xpath, css) => {
            function findDeep(sel) {
                // The >>> segments explicitly identify each shadow host, including nested roots.
                let root = document;
                let el = null;
                for (const part of sel.split(' >>> ')) {
                    el = root.querySelector(part);
                    if (!el) return null;
                    root = el.shadowRoot;
                }
                return el;
            }

            let el = null;
            if (xpath && !xpath.includes('[Shadow DOM')) {
                try {
                    let res = document.evaluate(xpath, document, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null);
                    el = res.singleNodeValue;
                } catch(e) {}
            }
            if (!el && css) {
                try { el = findDeep(css); } catch(e) {}
            }

            if (el && el.nodeType === 9) el = el.documentElement;

            if (!el) return JSON.stringify({ success: false, message: 'Element not found.' });

            el.scrollIntoView({ behavior: 'smooth', block: 'center' });

            // Flash cyan highlight
            let origOutline = el.style.outline;
            let origBoxShadow = el.style.boxShadow;
            el.style.outline = '3px solid #00BCD4';
            el.style.boxShadow = '0 0 15px #00BCD4';
            setTimeout(() => {
                el.style.outline = origOutline;
                el.style.boxShadow = origBoxShadow;
            }, 1500);

            let tag = el.tagName.toLowerCase();
            let textVal = (el.innerText || el.textContent || '').trim();
            let hasValue = 'value' in el && el.value !== undefined && el.value !== '';
            let rawValue = hasValue ? String(el.value) : '';

            let parts = [];
            if (textVal) {
                let dispText = textVal.length > 50 ? textVal.substring(0, 50) + '...' : textVal;
                parts.push('Text: ""' + dispText + '""');
            }
            if (hasValue) {
                let dispVal = rawValue.length > 50 ? rawValue.substring(0, 50) + '...' : rawValue;
                parts.push('Value: ""' + dispVal + '""');
            }
            if (el.type === 'checkbox' || el.type === 'radio') {
                parts.push('Checked: ' + el.checked);
            }
            if (parts.length === 0) {
                if (el.getAttribute('placeholder')) parts.push('Placeholder: ""' + el.getAttribute('placeholder') + '""');
                else if (el.getAttribute('aria-label')) parts.push('Aria-label: ""' + el.getAttribute('aria-label') + '""');
                else parts.push('(Empty text/value)');
            }

            return JSON.stringify({
                success: true,
                message: '<' + tag + '> ' + parts.join(' | ')
            });
        }";

        await ExecuteActionInBrowserAsync("Get Text/Value", js);
    }

    private void TestTypeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        TypeInputPopup.IsOpen = !TypeInputPopup.IsOpen;
        if (TypeInputPopup.IsOpen)
        {
            if (TypeToggleIcon != null) TypeToggleIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.Keyboard;
            this.Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(50);
                TypeInputBox.Focus();
                TypeInputBox.SelectAll();
            }, System.Windows.Threading.DispatcherPriority.Input);
        }
        else
        {
            if (TypeToggleIcon != null) TypeToggleIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.KeyboardOutline;
        }
    }

    private void CloseTypeInput_Click(object sender, RoutedEventArgs e)
    {
        TypeInputPopup.IsOpen = false;
        if (TypeToggleIcon != null) TypeToggleIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.KeyboardOutline;
    }

    private void TypeInputPopup_Closed(object sender, EventArgs e)
    {
        if (TypeToggleIcon != null) TypeToggleIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.KeyboardOutline;
    }

    private void TypeInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            TestTypeButton_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseTypeInput_Click(sender, e);
            e.Handled = true;
        }
    }

    private async void TestTypeButton_Click(object sender, RoutedEventArgs e)
    {
        string textToType = TypeInputBox.Text ?? "";
        TypeInputPopup.IsOpen = false;
        var (xpath, css) = GetActiveSelectors();
        if (string.IsNullOrEmpty(xpath) && string.IsNullOrEmpty(css))
        {
            StatusTextBlock.Text = "⚠️ Please select an element from repository first.";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 152, 0));
            return;
        }

        var page = await GetActionPageAsync();
        if (page == null || page.IsClosed)
        {
            StatusTextBlock.Text = "❌ No active browser tab found.";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(244, 67, 54));
            return;
        }

        try
        {
            StatusTextBlock.Text = "Typing into element...";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 193, 7));

            string js = @"(xpath, css, text) => {
                function findDeep(sel) {
                    // The >>> segments explicitly identify each shadow host, including nested roots.
                    let root = document;
                    let el = null;
                    for (const part of sel.split(' >>> ')) {
                        el = root.querySelector(part);
                        if (!el) return null;
                        root = el.shadowRoot;
                    }
                    return el;
                }

                let el = null;
                if (xpath && !xpath.includes('[Shadow DOM')) {
                    try {
                        let res = document.evaluate(xpath, document, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null);
                        el = res.singleNodeValue;
                    } catch(e) {}
                }
                if (!el && css) {
                    try { el = findDeep(css); } catch(e) {}
                }

                if (el && el.nodeType === 9) return JSON.stringify({ success: false, message: 'Select an input or editable element; a #document cannot accept text-input actions.' });

                if (!el) return JSON.stringify({ success: false, message: 'Element not found to type into.' });

                el.scrollIntoView({ behavior: 'auto', block: 'center' });
                el.focus();

                if ('value' in el) {
                    el.value = text;
                    el.dispatchEvent(new Event('input', { bubbles: true }));
                    el.dispatchEvent(new Event('change', { bubbles: true }));
                } else if (el.isContentEditable) {
                    el.innerText = text;
                    el.dispatchEvent(new Event('input', { bubbles: true }));
                } else {
                    el.innerText = text;
                }

                return JSON.stringify({ success: true, message: 'Typed ""' + text + '"" into <' + el.tagName.toLowerCase() + '>.' });
            }";

            var resultJson = await GetActionFrame(page).EvaluateFunctionAsync<string>(js, xpath, css, textToType);
            var result = JsonConvert.DeserializeObject<ActionResult>(resultJson);

            if (result.Success)
            {
                StatusTextBlock.Text = $"✅ {result.Message}";
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(76, 175, 80));
            }
            else
            {
                StatusTextBlock.Text = $"❌ {result.Message}";
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(244, 67, 54));
            }
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"❌ Type Into failed: {ex.Message}";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(244, 67, 54));
        }
    }

    private async void TestClearTextButton_Click(object sender, RoutedEventArgs e)
    {
        string js = @"(xpath, css) => {
            function findDeep(sel) {
                // The >>> segments explicitly identify each shadow host, including nested roots.
                let root = document;
                let el = null;
                for (const part of sel.split(' >>> ')) {
                    el = root.querySelector(part);
                    if (!el) return null;
                    root = el.shadowRoot;
                }
                return el;
            }

            let el = null;
            if (xpath && !xpath.includes('[Shadow DOM')) {
                try {
                    let res = document.evaluate(xpath, document, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null);
                    el = res.singleNodeValue;
                } catch(e) {}
            }
            if (!el && css) {
                try { el = findDeep(css); } catch(e) {}
            }

            if (el && el.nodeType === 9) return JSON.stringify({ success: false, message: 'Select an input or editable element; a #document cannot accept text-input actions.' });

            if (!el) return JSON.stringify({ success: false, message: 'Element not found to clear.' });

            el.focus();
            if ('value' in el) {
                el.value = '';
                el.dispatchEvent(new Event('input', { bubbles: true }));
                el.dispatchEvent(new Event('change', { bubbles: true }));
            } else if (el.isContentEditable) {
                el.innerText = '';
                el.dispatchEvent(new Event('input', { bubbles: true }));
            }

            return JSON.stringify({ success: true, message: 'Cleared value of <' + el.tagName.toLowerCase() + '>.' });
        }";

        await ExecuteActionInBrowserAsync("Clear Input", js);
    }

    #endregion

    private string GetInspectorScript()
    {
        using (var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("WebUIExplorer.Inspector.js"))
        using (var reader = new StreamReader(stream))
        {
            return reader.ReadToEnd();
        }
    }
}
