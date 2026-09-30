using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PuppeteerSharp;
using WebUIExplorer;

internal static partial class Program
{
    private static MainWindow window;
    private static IPage page;
    private static IBrowser testBrowser;
    private static int session;
    private static int assertions;
    private static bool useAppFlags;
    private static string caseFilter;
    private static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    [STAThread]
    private static void Main(string[] args)
    {
        // Do not initialize App.xaml: its StartupUri would launch a second XPlorer
        // window/browser when the dispatcher runs.
        var app = new Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
        {
            useAppFlags = args.Contains("--app-flags");
            caseFilter = args.FirstOrDefault(a => a.StartsWith("--filter="))?.Substring(9);
            try { await Run(args.FirstOrDefault(a => !a.StartsWith("--")) ?? @"C:\Program Files\Google\Chrome\Application\chrome.exe"); }
            catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
            finally { app.Shutdown(); Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
        });
        Dispatcher.Run();
    }

    private static object Call(string method, params object[] args) =>
        typeof(MainWindow).GetMethod(method, Private).Invoke(window, args);
    private static Task CallAsync(string method, params object[] args) => (Task)Call(method, args);
    private static void Set(string field, object value) => typeof(MainWindow).GetField(field, Private).SetValue(window, value);
    private static string Status => ((TextBlock)window.FindName("StatusTextBlock")).Text;
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        assertions++;
        Console.WriteLine("PASS: " + message);
    }
    private static async Task Until(Func<Task<bool>> predicate, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var check = predicate();
            if (await Task.WhenAny(check, Task.Delay(1000)) == check && await check) return;
            await Task.Delay(25);
        }
        throw new Exception("Timed out: " + message + "; status=" + Status);
    }
    private static async Task Start()
    {
        Set("_isSpying", true);
        Set("_inspectorReady", true);
        Set("_spySession", ++session);
        foreach (var tab in await testBrowser.PagesAsync())
            await CallAsync("SetupPageForSpyingAsync", tab);
        _ = CallAsync("PollInspectorEventsAsync", session);
        foreach (var frame in page.Frames)
            Assert(await frame.EvaluateExpressionAsync<bool>("!!window._uiExpActive"), "inspector installed in " + frame.Url);
    }
    private static async Task Hover(IFrame frame, string selector)
    {
        var el = await frame.QuerySelectorAsync(selector);
        var box = await el.BoundingBoxAsync();
        await page.Mouse.MoveAsync(box.X + Math.Min(12, box.Width / 2), box.Y + Math.Min(12, box.Height / 2));
        await Until(async () => await frame.EvaluateExpressionAsync<bool>("!!document.getElementById('_ui_exp_badge')?.textContent"), "hover badge");
    }
    private static Task<bool> Badge(IFrame frame, string text) => frame.EvaluateFunctionAsync<bool>(
        "text => (document.getElementById('_ui_exp_badge')?.textContent || '').includes(text)", text);
    private static async Task Capture(IFrame frame, string id, bool click = false)
    {
        int before = window.SavedElements.Count;
        if (click)
        {
            await page.Mouse.DownAsync();
            await page.Mouse.UpAsync();
        }
        else await page.Keyboard.PressAsync("Enter");
        await Until(() => Task.FromResult(window.SavedElements.Count == before + 1), "capture " + id);
        var saved = window.SavedElements.Last();
        Assert(saved.Name == id, "captured " + id);
        Assert(saved.FrameId == frame.Id && saved.CapturedPage == page, "capture preserves page and frame");
        foreach (var f in page.Frames)
            Assert(await f.EvaluateExpressionAsync<bool>("!window._uiExpActive && !document.getElementById('_ui_exp_overlay')"), "cleanup in " + f.Url);
    }
    private static async Task Action(string method)
    {
        ((TextBlock)window.FindName("StatusTextBlock")).Text = "waiting";
        Call(method, null, null);
        await Until(() => Task.FromResult(Status.StartsWith("✅") || Status.StartsWith("❌")), method);
        Assert(Status.StartsWith("✅"), method + ": " + Status);
    }

    private static async Task Run(string executable)
    {
        string[] browserArgs = useAppFlags
            ? new[] { "--headless=new", "--disable-web-security", "--disable-site-isolation-trials", "--disable-features=IsolateOrigins,site-per-process" }
            : new[] { "--headless=new" };
        using (var browser = await Puppeteer.LaunchAsync(new LaunchOptions { ExecutablePath = executable, Headless = true, Args = browserArgs }))
        {
            testBrowser = browser;
            window = new MainWindow();
            Set("_browser", browser);
            page = await browser.NewPageAsync();
            using (var server = new FixtureServer(url =>
            {
                return url.Contains("/nested") ? "<input id='shared' value='nested'>"
                    : url.Contains("/child") ? "<input id='shared' value='child'><iframe src='http://nested.test/'></iframe>"
                    : @"<style>
                        body{margin:8px} .wrapper{position:relative;width:250px;height:42px;margin-bottom:10px}
                        input{width:240px;height:36px} .cover{position:absolute;inset:0;background:#ccc;z-index:2}
                        iframe{display:block;width:400px;height:180px;border:5px solid black;margin-top:12px}
                        #shadow{display:block;margin-top:10px}
                        </style>
                        <input id='shared' value='top'>
                        <div class='wrapper'><input id='covered'><div id='cover' class='cover'></div></div>
                        <div class='wrapper'><input id='no-pointer' style='pointer-events:none'><div id='pointer-cover' class='cover'></div></div>
                        <div class='wrapper'><input id='hidden' type='hidden'><div id='hidden-cover' class='cover'></div></div>
                        <div id='shadow'></div><iframe src='http://child.test/'></iframe>
                        <script>window.fixtureClicks=0;window.addEventListener('click',()=>window.fixtureClicks++);
                        const host=document.querySelector('#shadow').attachShadow({mode:'open'});
                        host.innerHTML='<div id=""inner-host""></div>';
                        host.querySelector('div').attachShadow({mode:'open'}).innerHTML='<input id=""shadow-input"" style=""width:200px;height:35px""><div id=""shadow-cover"" style=""position:absolute;top:0;left:0;width:220px;height:40px;background:silver""></div>';
                        host.querySelector('div').style.position='relative';</script>";
            }))
            {
                if (caseFilter != null)
                {
                    await ExtendedCases(browser, server, executable);
                    return;
                }
                await page.GoToAsync(server.Url, new NavigationOptions { ReferrerPolicy = "noReferrer" });
                await Until(() => Task.FromResult(page.Frames.Any(f => f.Url.Contains("/nested"))), "nested frames loaded: " + string.Join(", ", page.Frames.Select(f => f.Url)));
                var child = page.Frames.Single(f => f.Url.Contains("/child"));
                var nested = page.Frames.Single(f => f.Url.Contains("/nested"));
                if (useAppFlags)
                    Assert(new Uri(child.Url).Host != new Uri(page.Url).Host, "cross-origin fixtures use different hosts with application browser flags");
                else
                    Assert(await child.EvaluateExpressionAsync<bool>("window.frameElement === null"), "cross-origin fixture uses inaccessible frame document");

                foreach (var pair in new[] { ("#cover", "covered"), ("#pointer-cover", "no-pointer"), ("#hidden-cover", "hidden") })
                {
                    await Start();
                    await Hover(page.MainFrame, pair.Item1);
                    if (pair.Item2 == "hidden") await page.Keyboard.PressAsync("ArrowDown");
                    else
                    {
                        // Real wheel event at the native mouse position.
                        await page.Keyboard.DownAsync("Alt");
                        await page.Mouse.WheelAsync(0, 100);
                        await page.Keyboard.UpAsync("Alt");
                    }
                    // Hidden sibling is reached via its wrapper, then Down.
                    if (pair.Item2 == "hidden")
                    {
                        await page.Keyboard.PressAsync("ArrowUp");
                        await page.Keyboard.PressAsync("ArrowDown");
                    }
                    await Until(() => Badge(page.MainFrame, "input#" + pair.Item2), "layer " + pair.Item2);
                    if (pair.Item2 == "covered")
                    {
                        await page.Keyboard.DownAsync("Shift");
                        await page.Keyboard.PressAsync("Tab");
                        await page.Keyboard.UpAsync("Shift");
                        Assert(await Badge(page.MainFrame, "div#cover"), "reverse cycling returns to cover");
                        await page.Keyboard.PressAsync("Tab");
                    }
                    await Capture(page.MainFrame, pair.Item2, click: pair.Item2 == "covered");
                    Assert(await page.EvaluateExpressionAsync<int>("window.fixtureClicks") == 0, "capture does not trigger page click handlers");
                    await Action("ValidateButton_Click");
                }

                foreach (var frame in new[] { child, nested })
                {
                    await Start();
                    await Hover(frame, "#shared");
                    await Capture(frame, "shared");
                    Assert(window.SavedElements.Last().Hierarchy.Contains("#document"), "hierarchy identifies frame document");
                    // Open another tab to ensure actions still use the original capture.
                    var other = await browser.NewPageAsync();
                    ((TextBox)window.FindName("TypeInputBox")).Text = "frame-only";
                    await Action("TestTypeButton_Click");
                    Assert(await frame.EvaluateExpressionAsync<string>("document.querySelector('#shared').value") == "frame-only", "typing uses captured frame");
                    Assert(await page.EvaluateExpressionAsync<string>("document.querySelector('#shared').value") == "top", "duplicate top-level selector is untouched");
                    await Action("TestClearTextButton_Click");
                    await Action("TestScrollButton_Click");
                    await other.CloseAsync();
                }

                string detachedId = window.SavedElements.Last().FrameId;
                await child.EvaluateExpressionAsync("document.querySelector('iframe').remove()");
                await Until(() => Task.FromResult(!page.Frames.Any(f => f.Id == detachedId)), "frame detach");
                Call("ValidateButton_Click", null, null);
                await Until(() => Task.FromResult(Status.StartsWith("❌")), "detached-frame action error");
                Assert(Status.Contains("no longer available"), "detached frame reports an error instead of targeting another document");

                await Start();
                // A real pointer hit through two open shadow roots.
                var shadow = await page.EvaluateExpressionHandleAsync("document.querySelector('#shadow').shadowRoot.querySelector('#inner-host').shadowRoot.querySelector('#shadow-cover')");
                var shadowBox = await ((IElementHandle)shadow).BoundingBoxAsync();
                await page.Mouse.MoveAsync(shadowBox.X + 12, shadowBox.Y + 12);
                await page.Keyboard.PressAsync("Tab");
                await Until(() => Badge(page.MainFrame, "input#shadow-input"), "shadow layer");
                await Capture(page.MainFrame, "shadow-input");
                Assert(window.SavedElements.Last().Css.Split(new[] { " >>> " }, StringSplitOptions.None).Length == 3, "nested shadow selector chain");
                await Action("ValidateButton_Click");
                ((TextBox)window.FindName("TypeInputBox")).Text = "shadow value";
                await Action("TestTypeButton_Click");
                Assert(await page.EvaluateExpressionAsync<string>("document.querySelector('#shadow').shadowRoot.querySelector('#inner-host').shadowRoot.querySelector('#shadow-input').value") == "shadow value", "typing resolves nested shadow chain");

                await Start();
                await Hover(child, "#shared");
                await page.Keyboard.PressAsync("ArrowUp"); // body
                await page.Keyboard.PressAsync("ArrowUp"); // html
                await page.Keyboard.PressAsync("ArrowUp"); // #document
                await Until(() => child.EvaluateExpressionAsync<bool>("document.getElementById('_ui_exp_badge').textContent.startsWith('#document')"), "document selection");
                int beforeDocument = window.SavedElements.Count;
                await page.Keyboard.PressAsync("Enter");
                await Until(() => Task.FromResult(window.SavedElements.Count == beforeDocument + 1), "document capture");
                Assert(window.SavedElements.Last().TagName == "#document" && window.SavedElements.Last().XPath == "/", "document capture uses root XPath");
                await Action("ValidateButton_Click");
                await Action("TestScrollButton_Click");

                await Start();
                await child.GoToAsync(server.ChildUrl + "/reloaded", new NavigationOptions { ReferrerPolicy = "noReferrer" });
                await Until(async () => await child.EvaluateExpressionAsync<bool>("!!window._uiExpActive"), "inspector reinstalled on frame navigation");
                await Hover(child, "#shared");
                await page.Keyboard.PressAsync("Escape");
                await Until(async () => await page.MainFrame.EvaluateExpressionAsync<bool>("!window._uiExpActive"), "Escape cleanup across frames");
                foreach (var f in page.Frames)
                    Assert(await f.EvaluateExpressionAsync<bool>("!window._uiExpActive && !document.getElementById('_ui_exp_overlay')"), "cancel cleanup in " + f.Url);
                Console.WriteLine("All inspector browser smoke tests passed.");
                await ExtendedCases(browser, server, executable);
            }
        }
    }

    private sealed class FixtureServer : IDisposable
    {
        private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly Func<string, string> content;
        public string Url { get; }
        public string ChildUrl { get; }
        public FixtureServer(Func<string, string> content)
        {
            this.content = content;
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Url = "http://127.0.0.1:" + port + "/";
            ChildUrl = "http://localhost:" + port + "/child";
            _ = Serve();
        }
        private async Task Serve()
        {
            try
            {
                while (true)
                {
                    var client = await listener.AcceptTcpClientAsync();
                    _ = Respond(client);
                }
            }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }
        private async Task Respond(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var buffer = new byte[8192];
                int count = await stream.ReadAsync(buffer, 0, buffer.Length);
                string request = Encoding.ASCII.GetString(buffer, 0, count);
                string body = content(request.Split(' ')[1])
                    .Replace("http://child.test/", ChildUrl)
                    .Replace("http://nested.test/", Url + "nested");
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, 0, header.Length);
                await stream.WriteAsync(bytes, 0, bytes.Length);
            }
        }
        public void Dispose() => listener.Stop();
    }
}
