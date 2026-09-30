using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using Newtonsoft.Json;
using PuppeteerSharp;
using WebUIExplorer;

internal static partial class Program
{
    private static readonly List<(string name, bool passed, string error)> results = new List<(string, bool, string)>();
    private const string FixtureCss = "<style>body{margin:8px}.wrap{position:relative;width:240px;height:44px;margin-bottom:10px}input,textarea,select,button,.edit{box-sizing:border-box;width:240px;height:40px}.cover{position:absolute;inset:0;z-index:10;background:silver}</style>";
    private static MainWindow.SavedElementItem Last => window.SavedElements.Last();

    private static async Task Scenario(string name, Func<Task> run)
    {
        if (caseFilter != null && name.IndexOf(caseFilter, StringComparison.OrdinalIgnoreCase) < 0) return;
        var timer = Stopwatch.StartNew();
        Console.WriteLine("SCENARIO START: " + name);
        try
        {
            await CallAsync("StopSpyingAsync", true);
            await run();
            results.Add((name, true, ""));
            Console.WriteLine("SCENARIO PASS: " + name + " (" + timer.ElapsedMilliseconds + " ms)");
        }
        catch (Exception ex)
        {
            results.Add((name, false, ex.Message));
            Console.WriteLine("SCENARIO FAIL: " + name + ": " + ex.Message);
        }
        finally { await CallAsync("StopSpyingAsync", true); }
    }

    private static async Task Html(string html)
    {
        await page.SetContentAsync(FixtureCss + html);
        await page.Mouse.MoveAsync(790, 590);
    }

    private static async Task SelectLayer(IFrame frame, string id, int limit = 60)
    {
        for (int i = 0; i < limit; i++)
        {
            if (await frame.EvaluateFunctionAsync<bool>("id => (document.getElementById('_ui_exp_badge')?.textContent.split('\\n')[0] || '').startsWith(id)", "input#" + id)) return;
            await page.Keyboard.PressAsync("Tab");
        }
        throw new Exception("Layer not reachable: " + id);
    }

    private static async Task CaptureAny(IFrame frame, bool click = false)
    {
        int before = window.SavedElements.Count;
        if (click) { await page.Mouse.DownAsync(); await page.Mouse.UpAsync(); }
        else await page.Keyboard.PressAsync("Enter");
        await Until(() => Task.FromResult(window.SavedElements.Count == before + 1), "capture");
        Assert(Last.CapturedPage == page && Last.FrameId == frame.Id, "correct capture context");
    }

    private static async Task CheckSelectors(IFrame frame, string expression = "document.querySelector('[data-fixture=target]')")
    {
        var checks = await frame.EvaluateFunctionAsync<string>(@"(xpath,css,expression) => {
            const target=(0,eval)(expression), result={};
            if(xpath){ const nodes=document.evaluate(xpath,document,null,XPathResult.ORDERED_NODE_SNAPSHOT_TYPE,null);result.xpath=nodes.snapshotLength===1 && nodes.snapshotItem(0)===target; }
            if(css){let root=document, el;for(const part of css.split(' >>> ')){const nodes=root.querySelectorAll(part);if(nodes.length!==1){result.css=false;return JSON.stringify(result);}el=nodes[0];root=el.shadowRoot;}result.css=el===target;}
            return JSON.stringify(result);
        }", Last.XPath, Last.Css, expression);
        var values = JsonConvert.DeserializeObject<Dictionary<string, bool>>(checks);
        Assert(values.Count > 0 && values.Values.All(v => v), "every generated selector uniquely resolves the captured node: " + checks);
    }

    private static async Task ExtendedCases(IBrowser browser, FixtureServer server, string executable)
    {
        string version = await browser.GetVersionAsync();
        foreach (var variant in new[] {
            ("opaque sibling cover", "", ""),
            ("transparent sibling cover", "", "opacity:0"),
            ("pointer-events:none input", "pointer-events:none", ""),
            ("visibility:hidden input", "visibility:hidden", ""),
            ("opacity:0 input", "opacity:0", ""),
            ("disabled input", "", ""),
            ("readonly input", "", ""),
            ("display:none input", "display:none", ""),
            ("zero-size input", "width:0;height:0;padding:0;border:0", ""),
            ("scaled wrapper", "", ""),
            ("rotated wrapper", "", ""),
            ("RTL page", "", "") })
        {
            await Scenario(variant.Item1, async () =>
            {
                string extra = variant.Item1.StartsWith("disabled") ? " disabled" : variant.Item1.StartsWith("readonly") ? " readonly" : "";
                string wrapStyle = variant.Item1 == "scaled wrapper" ? "transform:scale(.8);transform-origin:top left" : variant.Item1 == "rotated wrapper" ? "transform:rotate(8deg);margin:40px" : "";
                await Html((variant.Item1 == "RTL page" ? "<script>document.documentElement.dir='rtl'</script>" : "") +
                    "<div class='wrap' style='" + wrapStyle + "'><input id='target' data-fixture='target' style='" + variant.Item2 + "'" + extra + "><div id='cover' class='cover' style='" + variant.Item3 + "'></div></div>");
                await Start();
                await Hover(page.MainFrame, "#cover");
                await SelectLayer(page.MainFrame, "target");
                await CaptureAny(page.MainFrame, true);
                await CheckSelectors(page.MainFrame);
            });
        }

        foreach (string type in new[] { "text", "password", "email", "number", "date", "range", "checkbox", "radio", "file", "color", "hidden", "search", "tel", "url" })
            await Scenario("covered input type=" + type, async () =>
            {
                await Html("<div class='wrap'><input id='target' data-fixture='target' type='" + type + "'><div id='cover' class='cover'></div></div>");
                await Start(); await Hover(page.MainFrame, "#cover"); await SelectLayer(page.MainFrame, "target");
                await CaptureAny(page.MainFrame); await CheckSelectors(page.MainFrame);
            });

        foreach (var item in new[] {
            ("textarea", "<textarea id='target' data-fixture='target'></textarea>"),
            ("select", "<select id='target' data-fixture='target'><option>one</option></select>"),
            ("button", "<button id='target' data-fixture='target'>click</button>"),
            ("contenteditable=true", "<div class='edit' id='target' data-fixture='target' contenteditable='true'>edit</div>"),
            ("contenteditable empty", "<div class='edit' id='target' data-fixture='target' contenteditable>edit</div>"),
            ("contenteditable plaintext-only", "<div class='edit' id='target' data-fixture='target' contenteditable='plaintext-only'>edit</div>") })
            await Scenario("Down reaches covered " + item.Item1, async () =>
            {
                await Html("<div class='wrap' id='wrap'><div class='cover'></div>" + item.Item2 + "</div>");
                await Start(); await Hover(page.MainFrame, "#wrap .cover");
                await page.Keyboard.PressAsync("ArrowUp"); await page.Keyboard.PressAsync("ArrowDown");
                Assert(await Badge(page.MainFrame, "#target"), "Down selects control instead of the covering child");
                await CaptureAny(page.MainFrame); await CheckSelectors(page.MainFrame);
            });

        foreach (var attributes in new[] {
            ("both quote types in ID", "id=\"both'&quot;quotes\""),
            ("CSS punctuation ID", "id='9:brackets[.]# spaces\\end'"),
            ("Unicode ID", "id='输入-șț-😀'"),
            ("test ID quotes", "data-testid=\"a'&quot;b\""),
            ("name only", "name='account'"),
            ("placeholder only", "placeholder='Pick an account'"),
            ("aria label only", "aria-label='Account picker'"),
            ("unique class only", "class='account special:class'"),
            ("duplicate ID", "id='duplicate'"),
            ("no attributes", "") })
            await Scenario("selector: " + attributes.Item1, async () =>
            {
                await Html((attributes.Item1 == "duplicate ID" ? "<input id='duplicate'>" : "") + "<input data-fixture='target' " + attributes.Item2 + ">");
                await Start(); await Hover(page.MainFrame, "[data-fixture=target]");
                await CaptureAny(page.MainFrame); await CheckSelectors(page.MainFrame);
                await Action("ValidateButton_Click");
            });

        await Scenario("XPath text with quotes and whitespace", async () =>
        {
            await Html("<button data-fixture='target'> say &quot;hello&quot; and 'bye' </button>");
            await Start(); await Hover(page.MainFrame, "button"); await CaptureAny(page.MainFrame); await CheckSelectors(page.MainFrame);
        });
        await Scenario("SVG element selectors", async () =>
        {
            await Html("<svg width='250' height='100'><rect id='target' data-fixture='target' width='240' height='80' fill='silver'/></svg>");
            await Start(); await Hover(page.MainFrame, "rect"); await CaptureAny(page.MainFrame); await CheckSelectors(page.MainFrame);
            await Action("ValidateButton_Click");
        });
        await Scenario("multiple overlapping input layers and wraparound", async () =>
        {
            await Html("<div class='wrap'><input id='target' data-fixture='target'><input id='second' style='position:absolute;inset:0'><div id='cover' class='cover'></div></div>");
            await Start(); await Hover(page.MainFrame, "#cover"); await page.Keyboard.PressAsync("Tab");
            Assert(await Badge(page.MainFrame, "input#target"), "first covered input offered");
            await page.Keyboard.PressAsync("Tab"); Assert(await Badge(page.MainFrame, "input#second"), "second covered input offered");
            int count = await page.EvaluateExpressionAsync<int>("Number(document.getElementById('_ui_exp_badge').textContent.match(/\\[\\d+\\/(\\d+)\\]/)[1])");
            for (int i = 0; i < count; i++) await page.Keyboard.PressAsync("Tab");
            Assert(await Badge(page.MainFrame, "input#second"), "cycling wraps around");
        });
        await Scenario("tiny pointer jitter preserves selected layer", async () =>
        {
            await Html("<div class='wrap'><input id='target' data-fixture='target'><div id='cover' class='cover'></div></div>");
            await Start(); await Hover(page.MainFrame, "#cover"); await SelectLayer(page.MainFrame, "target");
            await page.Mouse.MoveAsync(21, 21); await CaptureAny(page.MainFrame, true); await CheckSelectors(page.MainFrame);
        });
        await Scenario("moving to another control resets layer selection", async () =>
        {
            await Html("<div class='wrap'><input id='first'><div id='cover' class='cover'></div></div><input id='target' data-fixture='target'>");
            await Start(); await Hover(page.MainFrame, "#cover"); await SelectLayer(page.MainFrame, "first");
            await Hover(page.MainFrame, "#target"); await CaptureAny(page.MainFrame, true); await CheckSelectors(page.MainFrame);
        });
        await Scenario("click without preceding mousemove", async () =>
        {
            await Html("<input id='target' data-fixture='target'>");
            var el = await page.QuerySelectorAsync("#target"); var box = await el.BoundingBoxAsync();
            await page.Mouse.MoveAsync(box.X + 10, box.Y + 10); await Start();
            await CaptureAny(page.MainFrame, true); await CheckSelectors(page.MainFrame);
        });
        await Scenario("ordinary wheel scroll remains usable", async () =>
        {
            await Html("<input id='target'><div style='height:2500px'></div>");
            await Start(); await Hover(page.MainFrame, "#target"); await page.Mouse.WheelAsync(0, 200);
            await Until(() => page.EvaluateExpressionAsync<bool>("scrollY>0"), "page scroll");
        });
        await Scenario("scrolled overflow container", async () =>
        {
            await Html("<div id='scroller' style='height:120px;width:260px;overflow:auto'><div style='height:500px'></div><div class='wrap'><input id='target' data-fixture='target'><div id='cover' class='cover'></div></div></div>");
            await page.EvaluateExpressionAsync("document.querySelector('#scroller').scrollTop=500");
            await Start(); await Hover(page.MainFrame, "#cover"); await SelectLayer(page.MainFrame, "target"); await CaptureAny(page.MainFrame); await CheckSelectors(page.MainFrame);
        });
        await Scenario("cover implemented by pseudo-element", async () =>
        {
            await Html("<style>#wrap:after{content:'';position:absolute;inset:0;z-index:10;background:silver}</style><div class='wrap' id='wrap'><input id='target' data-fixture='target'></div>");
            await Start(); await Hover(page.MainFrame, "#wrap"); await SelectLayer(page.MainFrame, "target"); await CaptureAny(page.MainFrame); await CheckSelectors(page.MainFrame);
        });
        await Scenario("modal dialog in top layer", async () =>
        {
            await Html("<dialog><div class='wrap'><input id='target' data-fixture='target'><div id='cover' class='cover'></div></div></dialog><script>document.querySelector('dialog').showModal()</script>");
            await Start(); await Hover(page.MainFrame, "#cover");
            Assert(await page.EvaluateExpressionAsync<bool>(@"(()=>{const el=document.getElementById('_ui_exp_overlay');el.style.setProperty('pointer-events','auto','important');const r=el.getBoundingClientRect();const visible=document.elementFromPoint(r.left+5,r.top+5)===el;el.style.setProperty('pointer-events','none','important');return visible;})()"), "selection overlay appears above modal content");
            await SelectLayer(page.MainFrame, "target"); await CaptureAny(page.MainFrame); await CheckSelectors(page.MainFrame);
        });
        await Scenario("dynamic insertion while indicating", async () =>
        {
            await Html("<div class='wrap' id='wrap'><div id='cover' class='cover'></div></div>");
            await Start(); await page.EvaluateExpressionAsync("document.querySelector('#wrap').insertAdjacentHTML('afterbegin','<input id=target data-fixture=target>')");
            await Hover(page.MainFrame, "#cover"); await SelectLayer(page.MainFrame, "target"); await CaptureAny(page.MainFrame); await CheckSelectors(page.MainFrame);
        });
        await Scenario("replacement after layer list was built", async () =>
        {
            await Html("<div class='wrap'><input id='old'><div id='cover' class='cover'></div></div>");
            await Start(); await Hover(page.MainFrame, "#cover"); await SelectLayer(page.MainFrame, "old");
            await page.EvaluateExpressionAsync("document.querySelector('#old').outerHTML='<input id=target data-fixture=target>'");
            await SelectLayer(page.MainFrame, "target"); await CaptureAny(page.MainFrame); await CheckSelectors(page.MainFrame);
        });
        await Scenario("repeated injection leaves one overlay and one capture", async () =>
        {
            await Html("<input id='target' data-fixture='target'>"); await Start();
            for (int i = 0; i < 5; i++) await CallAsync("SetupPageForSpyingAsync", page);
            Assert(await page.EvaluateExpressionAsync<int>("document.querySelectorAll('#_ui_exp_overlay').length") == 1, "one overlay after reinjection");
            await Hover(page.MainFrame, "#target"); await CaptureAny(page.MainFrame); await CheckSelectors(page.MainFrame);
        });
        await Scenario("cancel restores native click handlers", async () =>
        {
            await Html("<button id='target' onclick='window.clicks=(window.clicks||0)+1'>click</button>");
            await Start(); await Hover(page.MainFrame, "#target"); await page.Keyboard.PressAsync("Escape");
            await Until(() => page.EvaluateExpressionAsync<bool>("!document.getElementById('_ui_exp_overlay')"), "cancel");
            await page.ClickAsync("#target"); Assert(await page.EvaluateExpressionAsync<int>("window.clicks") == 1, "page click restored after cancel");
        });
        await Scenario("25 rapid start and cancel cycles", async () =>
        {
            await Html("<input id='target'>");
            for (int i = 0; i < 25; i++) { await Start(); await CallAsync("StopSpyingAsync", true); }
            Assert(await page.EvaluateExpressionAsync<int>("document.querySelectorAll('#_ui_exp_overlay').length") == 0, "no leaked overlays");
            await Start(); await Hover(page.MainFrame, "#target"); await CaptureAny(page.MainFrame);
        });
        await Scenario("10,000-node page layer scan", async () =>
        {
            await Html("<div class='wrap'><input id='target' data-fixture='target'><div id='cover' class='cover'></div></div><div id='large'></div>");
            await page.EvaluateExpressionAsync("document.querySelector('#large').innerHTML='<span>node</span>'.repeat(10000)");
            await Start(); await Hover(page.MainFrame, "#cover"); var timer = Stopwatch.StartNew();
            await SelectLayer(page.MainFrame, "target"); Assert(timer.ElapsedMilliseconds < 2000, "layer scan remains responsive (" + timer.ElapsedMilliseconds + " ms)");
            await CaptureAny(page.MainFrame); await CheckSelectors(page.MainFrame);
        });

        foreach (var frameType in new[] { "same-origin", "cross-origin", "srcdoc", "sandbox opaque origin", "shadow-hosted iframe" })
            await Scenario("dynamic frame: " + frameType, async () =>
            {
                await Html("<div id='frame-host'></div>"); await Start();
                string url = frameType == "cross-origin" ? server.ChildUrl : server.Url + "nested";
                await page.EvaluateFunctionAsync(@"(type,url) => {
                    const frame=document.createElement('iframe');frame.id='dynamic-frame';frame.style='width:400px;height:180px;border:8px solid black';
                    if(type==='srcdoc')frame.srcdoc='<input id=shared value=srcdoc>';
                    else {frame.src=url;if(type==='sandbox opaque origin')frame.setAttribute('sandbox','allow-scripts');}
                    let host=document.querySelector('#frame-host');if(type==='shadow-hosted iframe')host=host.attachShadow({mode:'open'});host.appendChild(frame);
                }", frameType, url);
                await Until(() => Task.FromResult(page.Frames.Any(f => f != page.MainFrame && f.Url != "about:blank")), "dynamic frame attached");
                IFrame frame = null;
                await Until(async () => {
                    frame = page.Frames.FirstOrDefault(f => f != page.MainFrame && !f.Detached &&
                        (frameType == "srcdoc" ? f.Url == "about:srcdoc" : f.Url == url));
                    return frame != null && await frame.EvaluateExpressionAsync<bool>("!!window._uiExpActive && !!document.querySelector('#shared')");
                }, "frame inspector for " + frameType);
                await Hover(frame, "#shared"); await CaptureAny(frame, true);
                await Action("ValidateButton_Click");
                ((TextBox)window.FindName("TypeInputBox")).Text = "dynamic frame"; await Action("TestTypeButton_Click");
                Assert(await frame.EvaluateExpressionAsync<string>("document.querySelector('#shared').value") == "dynamic frame", "action reaches dynamic frame");
            });

        foreach (string mode in new[] { "slotted input", "hidden input inside shadow", "closed shadow host" })
            await Scenario(mode, async () =>
            {
                await Html("<div id='host'></div>");
                await page.EvaluateFunctionAsync(@"mode=>{
                    const host=document.querySelector('#host');host.style='display:block;position:relative;width:240px;height:44px';
                    const root=host.attachShadow({mode:mode==='closed shadow host'?'closed':'open'});
                    if(mode==='slotted input'){host.innerHTML='<input id=target data-fixture=target slot=field>';root.innerHTML='<slot name=field></slot>';}
                    else root.innerHTML='<input id=target data-fixture=target '+(mode==='hidden input inside shadow'?'type=hidden':'')+'><div id=cover style=""width:240px;height:44px;background:silver""></div>';
                }", mode);
                await Start(); await Hover(page.MainFrame, "#host");
                if (mode == "closed shadow host")
                {
                    await CaptureAny(page.MainFrame); Assert(Last.TagName == "DIV" && Last.Css == "#host", "closed root captures the accessible host");
                }
                else
                {
                    await SelectLayer(page.MainFrame, "target"); await CaptureAny(page.MainFrame);
                    await CheckSelectors(page.MainFrame, mode == "slotted input" ? "document.querySelector('#target')" : "document.querySelector('#host').shadowRoot.querySelector('#target')");
                }
            });

        await Scenario("all actions on a captured text input", async () =>
        {
            await Html("<input id='target' data-fixture='target' value='original'>"); await Start(); await Hover(page.MainFrame, "#target"); await CaptureAny(page.MainFrame);
            foreach (string action in new[] { "ValidateButton_Click", "TestClickButton_Click", "TestHoverButton_Click", "TestScrollButton_Click", "TestGetTextButton_Click" }) await Action(action);
            ((TextBox)window.FindName("TypeInputBox")).Text = "new text"; await Action("TestTypeButton_Click");
            Assert(await page.EvaluateExpressionAsync<string>("document.querySelector('#target').value") == "new text", "typed text verified");
            await Action("TestClearTextButton_Click"); Assert(await page.EvaluateExpressionAsync<string>("document.querySelector('#target').value") == "", "clear verified");
        });
        await Scenario("missing saved element gives an action error", async () =>
        {
            await Html("<input id='target'>"); await Start(); await Hover(page.MainFrame, "#target"); await CaptureAny(page.MainFrame);
            await page.EvaluateExpressionAsync("document.querySelector('#target').remove()");
            Call("ValidateButton_Click", null, null); await Until(() => Task.FromResult(Status.StartsWith("❌")), "missing element error");
            Assert(Status.Contains("0 elements"), "missing selector reports zero matches");
        });

        await LifecycleCases(browser, server);
        string browserLabel = executable.IndexOf("msedge", StringComparison.OrdinalIgnoreCase) >= 0 ? "Edge"
            : executable.IndexOf("chrome-win64", StringComparison.OrdinalIgnoreCase) >= 0 ? "BundledChromium" : "Chrome";
        string reportPath = Path.GetFullPath("tests/" + browserLabel + (useAppFlags ? "-app-flags" : "") + (caseFilter != null ? "-filtered" : "") + "-results.md");
        var report = new StringBuilder("# Inspector browser regression results\n\nBrowser: " + version + "\n\n");
        report.AppendLine("Configuration: " + (useAppFlags ? "application browser flags (web security and site isolation disabled)" : "default browser security and site isolation") + ".\n");
        report.AppendLine(results.Count(r => r.passed) + "/" + results.Count + " extended scenarios passed; " + assertions + " successful assertions including the original smoke suite.\n");
        report.AppendLine("Original suite also covers nested cross-origin frames, document capture, nested shadow roots, detached frames, frame-specific actions with another tab open, navigation, and cancellation.\n");
        report.AppendLine("| Scenario | Result | Details |\n|---|---|---|");
        foreach (var result in results) report.AppendLine("| " + result.name + " | " + (result.passed ? "PASS" : "FAIL") + " | " + result.error.Replace("|", "\\|").Replace("\n", " ").Replace("\r", " ") + " |");
        File.WriteAllText(reportPath, report.ToString());
        Console.WriteLine("REPORT: " + reportPath);
        if (results.Any(r => !r.passed)) throw new Exception("Failed scenarios: " + string.Join(", ", results.Where(r => !r.passed).Select(r => r.name)));
    }
}
