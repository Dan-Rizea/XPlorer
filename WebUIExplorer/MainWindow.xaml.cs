using System;
using System.Collections.Generic;
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
            IPage activePage = null;
            if (pages.Length == 0)
            {
                activePage = await _browser.NewPageAsync();
            }
            else
            {
                activePage = pages.LastOrDefault(p => !p.IsClosed) ?? pages.FirstOrDefault();
            }

            if (activePage != null)
            {
                await EnsureBrowserWindowVisibleAsync(activePage);
            }

            // Hook browser target events so if user opens new pages while spying, we inject into them
            _browser.TargetCreated += async (s, ev) =>
            {
                if (_isSpying)
                {
                    try
                    {
                        var target = ev.Target;
                        var page = await target.PageAsync();
                        if (page != null)
                        {
                            await SetupPageForSpyingAsync(page);
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
                        "--start-maximized"
                    }
                });
            }
        }
    }

    private async Task<IPage> GetActivePageAsync()
    {
        if (_browser == null) return null;
        var pages = await _browser.PagesAsync();
        if (pages.Length == 0)
        {
            return await _browser.NewPageAsync();
        }
        return pages.LastOrDefault(p => !p.IsClosed) ?? pages.FirstOrDefault();
    }

    private async Task EnsureBrowserWindowVisibleAsync(IPage page)
    {
        if (page == null || page.IsClosed) return;
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
            StatusTextBlock.Text = "Click any element to save and inspect (or ESC)...";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 152, 0)); // Orange

            var pages = await _browser.PagesAsync();
            if (pages.Length == 0)
            {
                var newPage = await _browser.NewPageAsync();
                pages = new[] { newPage };
            }

            // Bring active page to front
            var activePage = pages.LastOrDefault(p => !p.IsClosed) ?? pages.First();
            try { await activePage.BringToFrontAsync(); } catch { }

            // Inject into all open pages
            foreach (var page in pages.Where(p => !p.IsClosed))
            {
                await SetupPageForSpyingAsync(page);
            }
        }
        catch (Exception ex)
        {
            await StopSpyingAsync();
            MessageBox.Show($"Failed to start indicator: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task SetupPageForSpyingAsync(IPage page)
    {
        if (page == null || page.IsClosed) return;

        try
        {
            // Expose C# functions if not already exposed
            try
            {
                await page.ExposeFunctionAsync("onElementSelected", new Func<string, bool>(jsonProps =>
                {
                    Dispatcher.Invoke(new Action(async () =>
                    {
                        await StopSpyingAsync(userCancelled: false);
                        ProcessCapturedElement(jsonProps);
                    }));
                    return true;
                }));
            }
            catch { /* Already exposed on this page instance */ }

            try
            {
                await page.ExposeFunctionAsync("onSpyCancelled", new Func<bool>(() =>
                {
                    Dispatcher.Invoke(new Action(async () =>
                    {
                        await StopSpyingAsync(userCancelled: true);
                    }));
                    return true;
                }));
            }
            catch { /* Already exposed on this page instance */ }

            // Inject the inspector client script
            string inspectorJs = GetInspectorScript();
            await page.EvaluateExpressionAsync(inspectorJs);
        }
        catch { }
    }

    private async Task StopSpyingAsync(bool userCancelled = false)
    {
        _isSpying = false;

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
                SpyButton.ToolTip = "Indicate Element on Webpage";
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
                    try { await page.EvaluateExpressionAsync(cleanupJs); } catch { }
                }
            }
            catch { }
        }
    }

    private void ProcessCapturedElement(string json)
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
                IsShadowDom = result.IsShadowDom
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

    private async void ChromeButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var page = await GetActivePageAsync();
            if (page != null)
            {
                await EnsureBrowserWindowVisibleAsync(page);
            }

            // Find browser window processes and restore + foreground them
            var procs = System.Diagnostics.Process.GetProcessesByName("chrome")
                .Concat(System.Diagnostics.Process.GetProcessesByName("msedge"))
                .Concat(System.Diagnostics.Process.GetProcessesByName("chromium"));

            foreach (var proc in procs)
            {
                if (proc.MainWindowHandle != IntPtr.Zero)
                {
                    if (IsIconic(proc.MainWindowHandle))
                    {
                        ShowWindow(proc.MainWindowHandle, 9); // SW_RESTORE
                    }
                    SetForegroundWindow(proc.MainWindowHandle);
                }
            }

            StatusTextBlock.Text = "Chrome browser brought to forefront.";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94));
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "Could not activate Chrome: " + ex.Message;
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

    private async Task ExecuteActionInBrowserAsync(string actionName, string jsFunction)
    {
        var (xpath, css) = GetActiveSelectors();
        if (string.IsNullOrEmpty(xpath) && string.IsNullOrEmpty(css))
        {
            StatusTextBlock.Text = "⚠️ Please select an element from repository first.";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 152, 0));
            return;
        }

        var page = await GetActivePageAsync();
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

            var resultJson = await page.EvaluateFunctionAsync<string>(jsFunction, xpath, css);
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
                function search(root) {
                    try {
                        let el = root.querySelector(sel);
                        if (el) return el;
                    } catch(e) {}
                    let all = root.querySelectorAll('*');
                    for (let n of all) {
                        if (n.shadowRoot) {
                            let found = search(n.shadowRoot);
                            if (found) return found;
                        }
                    }
                    return null;
                }
                return search(document);
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
                    let list = document.querySelectorAll(css);
                    count = list.length;
                    if (count > 0) el = list[0];
                    if (!el) {
                        el = findDeep(css);
                        if (el) count = 1;
                    }
                } catch(e) {}
            }

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
                function search(root) {
                    try {
                        let el = root.querySelector(sel);
                        if (el) return el;
                    } catch(e) {}
                    let all = root.querySelectorAll('*');
                    for (let n of all) {
                        if (n.shadowRoot) {
                            let found = search(n.shadowRoot);
                            if (found) return found;
                        }
                    }
                    return null;
                }
                return search(document);
            }

            let el = null;
            if (xpath && !xpath.includes('[Shadow DOM')) {
                try {
                    let res = document.evaluate(xpath, document, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null);
                    el = res.singleNodeValue;
                } catch(e) {}
            }
            if (!el && css) {
                try { el = document.querySelector(css) || findDeep(css); } catch(e) {}
            }

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

        var page = await GetActivePageAsync();
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
                    function search(root) {
                        try {
                            let el = root.querySelector(sel);
                            if (el) return el;
                        } catch(e) {}
                        let all = root.querySelectorAll('*');
                        for (let n of all) {
                            if (n.shadowRoot) {
                                let found = search(n.shadowRoot);
                                if (found) return found;
                            }
                        }
                        return null;
                    }
                    return search(document);
                }

                let el = null;
                if (xpath && !xpath.includes('[Shadow DOM')) {
                    try {
                        let res = document.evaluate(xpath, document, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null);
                        el = res.singleNodeValue;
                    } catch(e) {}
                }
                if (!el && css) {
                    try { el = document.querySelector(css) || findDeep(css); } catch(e) {}
                }

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

            var resultJson = await page.EvaluateFunctionAsync<string>(js, xpath, css);
            var result = JsonConvert.DeserializeObject<HoverActionResult>(resultJson);

            if (result != null && result.Success)
            {
                try
                {
                    await page.Mouse.MoveAsync(result.X, result.Y);
                }
                catch { }

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
                function search(root) {
                    try {
                        let el = root.querySelector(sel);
                        if (el) return el;
                    } catch(e) {}
                    let all = root.querySelectorAll('*');
                    for (let n of all) {
                        if (n.shadowRoot) {
                            let found = search(n.shadowRoot);
                            if (found) return found;
                        }
                    }
                    return null;
                }
                return search(document);
            }

            let el = null;
            if (xpath && !xpath.includes('[Shadow DOM')) {
                try {
                    let res = document.evaluate(xpath, document, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null);
                    el = res.singleNodeValue;
                } catch(e) {}
            }
            if (!el && css) {
                try { el = document.querySelector(css) || findDeep(css); } catch(e) {}
            }

            if (!el) return JSON.stringify({ success: false, message: 'Element not found to scroll into view.' });

            el.scrollIntoView({ behavior: 'smooth', block: 'center' });
            return JSON.stringify({ success: true, message: 'Scrolled <' + el.tagName.toLowerCase() + '> into view.' });
        }";

        await ExecuteActionInBrowserAsync("Scroll To", js);
    }

    private async void TestGetTextButton_Click(object sender, RoutedEventArgs e)
    {
        string js = @"(xpath, css) => {
            function findDeep(sel) {
                function search(root) {
                    try {
                        let el = root.querySelector(sel);
                        if (el) return el;
                    } catch(e) {}
                    let all = root.querySelectorAll('*');
                    for (let n of all) {
                        if (n.shadowRoot) {
                            let found = search(n.shadowRoot);
                            if (found) return found;
                        }
                    }
                    return null;
                }
                return search(document);
            }

            let el = null;
            if (xpath && !xpath.includes('[Shadow DOM')) {
                try {
                    let res = document.evaluate(xpath, document, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null);
                    el = res.singleNodeValue;
                } catch(e) {}
            }
            if (!el && css) {
                try { el = document.querySelector(css) || findDeep(css); } catch(e) {}
            }

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

        var page = await GetActivePageAsync();
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
                    function search(root) {
                        try {
                            let el = root.querySelector(sel);
                            if (el) return el;
                        } catch(e) {}
                        let all = root.querySelectorAll('*');
                        for (let n of all) {
                            if (n.shadowRoot) {
                                let found = search(n.shadowRoot);
                                if (found) return found;
                            }
                        }
                        return null;
                    }
                    return search(document);
                }

                let el = null;
                if (xpath && !xpath.includes('[Shadow DOM')) {
                    try {
                        let res = document.evaluate(xpath, document, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null);
                        el = res.singleNodeValue;
                    } catch(e) {}
                }
                if (!el && css) {
                    try { el = document.querySelector(css) || findDeep(css); } catch(e) {}
                }

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

            var resultJson = await page.EvaluateFunctionAsync<string>(js, xpath, css, textToType);
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
                function search(root) {
                    try {
                        let el = root.querySelector(sel);
                        if (el) return el;
                    } catch(e) {}
                    let all = root.querySelectorAll('*');
                    for (let n of all) {
                        if (n.shadowRoot) {
                            let found = search(n.shadowRoot);
                            if (found) return found;
                        }
                    }
                    return null;
                }
                return search(document);
            }

            let el = null;
            if (xpath && !xpath.includes('[Shadow DOM')) {
                try {
                    let res = document.evaluate(xpath, document, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null);
                    el = res.singleNodeValue;
                } catch(e) {}
            }
            if (!el && css) {
                try { el = document.querySelector(css) || findDeep(css); } catch(e) {}
            }

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
        return @"
        (function() {
            if (window._uiExpActive) {
                if (window._uiExpCleanup) window._uiExpCleanup();
            }
            window._uiExpActive = true;

            // Create highlight overlay element
            let overlay = document.getElementById('_ui_exp_overlay');
            if (!overlay) {
                overlay = document.createElement('div');
                overlay.id = '_ui_exp_overlay';
                overlay.style.cssText = 'position:fixed; z-index:2147483647; pointer-events:none; border:2px solid #FF1744; background:rgba(255,23,68,0.18); box-sizing:border-box; transition:all 0.04s ease; display:none; border-radius:3px;';
                
                let badge = document.createElement('div');
                badge.id = '_ui_exp_badge';
                badge.style.cssText = 'position:absolute; top:-26px; left:0; background:#FF1744; color:#fff; font-family:Consolas,monospace; font-size:11px; font-weight:bold; padding:2px 8px; border-radius:3px 3px 0 0; white-space:nowrap; box-shadow:0 2px 5px rgba(0,0,0,0.3); pointer-events:none;';
                overlay.appendChild(badge);
                
                (document.body || document.documentElement).appendChild(overlay);
            }

            let lastHovered = null;

            // Shadow DOM piercing elementFromPoint
            function getDeepElement(x, y) {
                let el = document.elementFromPoint(x, y);
                while (el && el.shadowRoot) {
                    let inner = el.shadowRoot.elementFromPoint(x, y);
                    if (!inner || inner === el) break;
                    el = inner;
                }
                return el;
            }

            function isInsideShadowRoot(el) {
                let root = el.getRootNode ? el.getRootNode() : null;
                return root && root instanceof ShadowRoot;
            }

            function getShadowHost(el) {
                let root = el.getRootNode ? el.getRootNode() : null;
                return (root && root instanceof ShadowRoot) ? root.host : null;
            }

            function updateOverlay(el) {
                if (!el || el === overlay || el === document.body || el === document.documentElement) {
                    overlay.style.display = 'none';
                    return;
                }
                let rect = el.getBoundingClientRect();
                if (rect.width === 0 && rect.height === 0) {
                    overlay.style.display = 'none';
                    return;
                }
                overlay.style.display = 'block';
                overlay.style.top = rect.top + 'px';
                overlay.style.left = rect.left + 'px';
                overlay.style.width = rect.width + 'px';
                overlay.style.height = rect.height + 'px';

                let badge = document.getElementById('_ui_exp_badge');
                if (badge) {
                    let idStr = el.id ? '#' + el.id : '';
                    let clsStr = (typeof el.className === 'string' && el.className.trim()) 
                        ? '.' + el.className.trim().split(/\s+/).slice(0, 2).join('.') 
                        : '';
                    let shadowStr = isInsideShadowRoot(el) ? ' [Shadow DOM]' : '';
                    badge.innerText = el.tagName.toLowerCase() + idStr + clsStr + shadowStr + ' [' + Math.round(rect.width) + 'x' + Math.round(rect.height) + ']';
                    if (rect.top < 28) {
                        badge.style.top = '0px';
                        badge.style.borderRadius = '0 0 3px 3px';
                    } else {
                        badge.style.top = '-24px';
                        badge.style.borderRadius = '3px 3px 0 0';
                    }
                }
            }

            function isUniqueXPath(xpath, contextNode = document) {
                try {
                    let res = document.evaluate(xpath, contextNode, null, XPathResult.ORDERED_NODE_SNAPSHOT_TYPE, null);
                    return res.snapshotLength === 1;
                } catch(e) {
                    return false;
                }
            }

            function generateXPath(el) {
                if (!el || el.nodeType !== 1) return '';
                let inShadow = isInsideShadowRoot(el);
                let tag = el.tagName.toLowerCase();

                // 1. By ID
                if (el.id && !el.id.includes(' ') && !el.id.includes('""')) {
                    let xpath = '//*[@id=""' + el.id + '""]';
                    if (isUniqueXPath(xpath)) return xpath;
                    let tagXpath = '//' + tag + '[@id=""' + el.id + '""]';
                    if (isUniqueXPath(tagXpath)) return tagXpath;
                }

                // 2. By data-testid / data-test / data-qa
                for (let attr of ['data-testid', 'data-test', 'data-qa', 'data-cy']) {
                    let val = el.getAttribute(attr);
                    if (val) {
                        let xpath = '//*[@' + attr + '=""' + val + '""]';
                        if (isUniqueXPath(xpath)) return xpath;
                    }
                }

                // 3. By Name
                if (el.name) {
                    let xpath = '//' + tag + '[@name=""' + el.name + '""]';
                    if (isUniqueXPath(xpath)) return xpath;
                }

                // 4. By Placeholder
                let placeholder = el.getAttribute('placeholder');
                if (placeholder) {
                    let xpath = '//' + tag + '[@placeholder=""' + placeholder + '""]';
                    if (isUniqueXPath(xpath)) return xpath;
                }

                // 5. By Aria-Label
                let ariaLabel = el.getAttribute('aria-label');
                if (ariaLabel) {
                    let xpath = '//' + tag + '[@aria-label=""' + ariaLabel + '""]';
                    if (isUniqueXPath(xpath)) return xpath;
                }

                // 6. By Text (for buttons, links, labels)
                if (['button', 'a', 'label', 'h1', 'h2', 'h3', 'span', 'p'].includes(tag)) {
                    let txt = (el.innerText || '').trim();
                    if (txt && txt.length > 0 && txt.length < 50 && !txt.includes('""') && !txt.includes('\n')) {
                        let xpath = '//' + tag + '[normalize-space()=""' + txt + '""]';
                        if (isUniqueXPath(xpath)) return xpath;
                    }
                }

                // 7. Hierarchical fallback
                function getFullXPath(node) {
                    if (!node || node.nodeType !== 1) return '';
                    if (node === document.body) return '/html/body';
                    if (node === document.documentElement) return '/html';
                    
                    let ix = 1;
                    let siblings = node.parentNode ? node.parentNode.children : [];
                    for (let i = 0; i < siblings.length; i++) {
                        let sib = siblings[i];
                        if (sib === node) {
                            let parentXPath = getFullXPath(node.parentNode);
                            return (parentXPath ? parentXPath + '/' : '/') + node.tagName.toLowerCase() + '[' + ix + ']';
                        }
                        if (sib.nodeType === 1 && sib.tagName === node.tagName) {
                            ix++;
                        }
                    }
                    return '';
                }

                let fullPath = getFullXPath(el);
                if (inShadow) {
                    return fullPath ? fullPath + ' [Inside Shadow Root]' : '//' + tag + ' [Inside Shadow Root]';
                }
                return fullPath;
            }

            function isUniqueCss(selector, root = document) {
                try {
                    return root.querySelectorAll(selector).length === 1;
                } catch(e) {
                    return false;
                }
            }

            function generateCss(el) {
                if (!el || el.nodeType !== 1) return '';
                let inShadow = isInsideShadowRoot(el);
                let host = getShadowHost(el);
                let rootContext = inShadow && host && host.shadowRoot ? host.shadowRoot : document;
                let tag = el.tagName.toLowerCase();

                // 1. By ID
                if (el.id && !el.id.includes(' ') && !/^\d/.test(el.id)) {
                    let sel = '#' + CSS.escape(el.id);
                    if (isUniqueCss(sel, rootContext)) {
                        return inShadow && host ? generateCss(host) + ' >>> ' + sel : sel;
                    }
                }

                // 2. By data-testid / name / aria-label
                for (let attr of ['data-testid', 'data-test', 'name', 'placeholder', 'aria-label']) {
                    let val = el.getAttribute(attr);
                    if (val) {
                        let sel = tag + '[' + attr + '=""' + CSS.escape(val) + '""]';
                        if (isUniqueCss(sel, rootContext)) {
                            return inShadow && host ? generateCss(host) + ' >>> ' + sel : sel;
                        }
                    }
                }

                // 3. By Class
                if (typeof el.className === 'string' && el.className.trim()) {
                    let classes = el.className.trim().split(/\s+/).filter(c => !c.includes(':') && !/^\d/.test(c));
                    if (classes.length > 0) {
                        let classSel = tag + '.' + classes.map(c => CSS.escape(c)).join('.');
                        if (isUniqueCss(classSel, rootContext)) {
                            return inShadow && host ? generateCss(host) + ' >>> ' + classSel : classSel;
                        }
                    }
                }

                // 4. Hierarchical path
                function getFullCss(node) {
                    if (!node || node.nodeType !== 1) return '';
                    if (node === document.body) return 'body';
                    if (node === document.documentElement) return 'html';
                    
                    let parent = node.parentElement;
                    if (!parent) return node.tagName.toLowerCase();
                    
                    let siblings = Array.from(parent.children).filter(c => c.tagName === node.tagName);
                    let index = siblings.indexOf(node) + 1;
                    let nodeTag = node.tagName.toLowerCase() + (siblings.length > 1 ? ':nth-of-type(' + index + ')' : '');
                    
                    let parentSel = getFullCss(parent);
                    return parentSel ? parentSel + ' > ' + nodeTag : nodeTag;
                }

                let localCss = getFullCss(el);
                if (inShadow && host) {
                    return generateCss(host) + ' >>> ' + localCss;
                }
                return localCss;
            }

            function onMouseMove(e) {
                if (!window._uiExpActive) return;
                let target = getDeepElement(e.clientX, e.clientY);
                if (target && target !== lastHovered && target !== overlay && !overlay.contains(target)) {
                    lastHovered = target;
                    updateOverlay(target);
                }
            }

            function onClick(e) {
                if (!window._uiExpActive) return;
                e.preventDefault();
                e.stopPropagation();
                e.stopImmediatePropagation();

                let target = lastHovered || getDeepElement(e.clientX, e.clientY);
                if (!target || target === overlay || overlay.contains(target)) return;

                let inShadow = isInsideShadowRoot(target);
                let host = getShadowHost(target);

                // Build element properties dictionary
                let attrs = {};
                attrs['Tag Name'] = target.tagName.toLowerCase();
                if (inShadow) {
                    let rootNode = target.getRootNode();
                    attrs['Shadow Root'] = 'Inside Shadow DOM (mode: ' + (rootNode ? rootNode.mode : 'open') + ')';
                    if (host) attrs['Shadow Host'] = '<' + host.tagName.toLowerCase() + (host.id ? '#' + host.id : '') + '>';
                }
                if (target.id) attrs['ID'] = target.id;
                if (typeof target.className === 'string' && target.className.trim()) attrs['Class'] = target.className.trim();
                if (target.name) attrs['Name'] = target.name;
                if (target.type) attrs['Type'] = target.type;
                if (target.value !== undefined && target.value !== '') attrs['Value'] = target.value;
                if (target.getAttribute('placeholder')) attrs['Placeholder'] = target.getAttribute('placeholder');
                if (target.getAttribute('aria-label')) attrs['Aria Label'] = target.getAttribute('aria-label');
                if (target.getAttribute('role')) attrs['Role'] = target.getAttribute('role');
                if (target.getAttribute('data-testid')) attrs['Data TestID'] = target.getAttribute('data-testid');
                if (target.href) attrs['Href'] = target.href;
                if (target.src) attrs['Src'] = target.src;
                if (target.title) attrs['Title'] = target.title;

                let txt = (target.innerText || '').trim();
                if (txt) {
                    attrs['Inner Text'] = txt.length > 150 ? txt.substring(0, 150) + '...' : txt;
                }

                let rect = target.getBoundingClientRect();
                attrs['Bounding Box'] = Math.round(rect.x) + ', ' + Math.round(rect.y) + ' (' + Math.round(rect.width) + 'x' + Math.round(rect.height) + ')';

                function generateHierarchy(node) {
                    let path = [];
                    let curr = node;
                    while (curr) {
                        if (curr.nodeType === 1) {
                            let tag = curr.tagName.toLowerCase();
                            let label = '<' + tag;
                            if (curr.id) label += ' id=""' + curr.id + '""';
                            if (curr.name) label += ' name=""' + curr.name + '""';
                            if (curr.getAttribute('type')) label += ' type=""' + curr.getAttribute('type') + '""';
                            if (curr.getAttribute('data-testid')) label += ' data-testid=""' + curr.getAttribute('data-testid') + '""';
                            if (curr.className && typeof curr.className === 'string' && curr.className.trim()) {
                                label += ' class=""' + curr.className.trim().split(/\s+/).slice(0, 2).join(' ') + '""';
                            }
                            label += '>';
                            path.unshift(label);
                        }

                        if (curr.parentElement) {
                            curr = curr.parentElement;
                        } else {
                            let root = curr.getRootNode ? curr.getRootNode() : null;
                            if (root && root instanceof ShadowRoot) {
                                path.unshift('#shadow-root (' + root.mode + ')');
                                curr = root.host;
                            } else {
                                break;
                            }
                        }
                    }
                    return path.join(' ');
                }

                let xpath = generateXPath(target);
                let css = generateCss(target);
                let hierarchy = generateHierarchy(target);

                // Playwright locator generation
                let playwrightCode = '';
                if (inShadow && host) {
                    let hostCss = generateCss(host);
                    let innerCss = target.id ? '#' + CSS.escape(target.id) : target.tagName.toLowerCase();
                    playwrightCode = 'page.Locator(\""' + hostCss + '\"").Locator(\""' + innerCss + '\"")';
                } else if (css) {
                    playwrightCode = 'page.Locator(\""' + css + '\"")';
                }

                // Selenium code generation
                let seleniumCode = '';
                if (inShadow && host) {
                    let hostCss = generateCss(host);
                    let innerCss = target.id ? '#' + CSS.escape(target.id) : target.tagName.toLowerCase();
                    seleniumCode = 'driver.FindElement(By.CssSelector(\""' + hostCss + '\"")).GetShadowRoot().FindElement(By.CssSelector(\""' + innerCss + '\""))';
                }

                let payload = {
                    TagName: target.tagName,
                    XPath: xpath,
                    Css: css,
                    Playwright: playwrightCode,
                    Selenium: seleniumCode,
                    Hierarchy: hierarchy,
                    IsShadowDom: inShadow,
                    Attributes: attrs
                };

                window._uiExpCleanup();

                if (window.onElementSelected) {
                    window.onElementSelected(JSON.stringify(payload));
                }
            }

            function blockEvent(e) {
                if (!window._uiExpActive) return;
                e.preventDefault();
                e.stopPropagation();
                e.stopImmediatePropagation();
            }

            let interceptedEvents = ['mousedown', 'mouseup', 'pointerdown', 'pointerup', 'dblclick', 'contextmenu'];

            function onKeyDown(e) {
                if (!window._uiExpActive) return;
                if (e.key === 'Escape' || e.keyCode === 27) {
                    e.preventDefault();
                    e.stopPropagation();
                    window._uiExpCleanup();
                    if (window.onSpyCancelled) {
                        window.onSpyCancelled();
                    }
                }
            }

            window._uiExpCleanup = function() {
                window._uiExpActive = false;
                document.removeEventListener('mousemove', onMouseMove, true);
                document.removeEventListener('click', onClick, true);
                window.removeEventListener('click', onClick, true);
                document.removeEventListener('keydown', onKeyDown, true);

                interceptedEvents.forEach(evt => {
                    document.removeEventListener(evt, blockEvent, true);
                    window.removeEventListener(evt, blockEvent, true);
                });

                if (overlay) {
                    overlay.style.display = 'none';
                }
            };

            interceptedEvents.forEach(evt => {
                document.addEventListener(evt, blockEvent, true);
                window.addEventListener(evt, blockEvent, true);
            });

            document.addEventListener('mousemove', onMouseMove, true);
            document.addEventListener('click', onClick, true);
            window.addEventListener('click', onClick, true);
            document.addEventListener('keydown', onKeyDown, true);
        })();
        ";
    }
}