using System;
using System.Linq;
using System.Threading.Tasks;
using PuppeteerSharp;

internal static partial class Program
{
    private static async Task LifecycleCases(IBrowser browser, FixtureServer server)
    {
        foreach (string tag in new[] { "body", "html", "#document" })
            await Scenario("root selection: " + tag, async () =>
            {
                await Html("<input id='target'>"); await Start(); await Hover(page.MainFrame, "#target");
                for (int i = 0; i < 5; i++)
                {
                    if (await page.EvaluateFunctionAsync<bool>("tag=>document.getElementById('_ui_exp_badge').textContent.startsWith(tag)", tag)) break;
                    await page.Keyboard.PressAsync("ArrowUp");
                }
                await CaptureAny(page.MainFrame);
                Assert(Last.TagName.Equals(tag, StringComparison.OrdinalIgnoreCase), "captured requested root node");
                await CheckSelectors(page.MainFrame, tag == "#document" ? "document" : "document.querySelector('" + tag + "')");
                await Action("ValidateButton_Click");
            });
        await Scenario("document Down returns the html child", async () =>
        {
            await Html("<input id=target>"); await Start(); await Hover(page.MainFrame, "#target");
            for (int i = 0; i < 3; i++) await page.Keyboard.PressAsync("ArrowUp");
            await page.Keyboard.PressAsync("ArrowDown"); await CaptureAny(page.MainFrame);
            Assert(Last.TagName == "HTML", "document child is html");
        });
        await Scenario("document without a body", async () =>
        {
            await Html(""); await page.EvaluateExpressionAsync("document.body.remove()"); await Start();
            await page.Mouse.MoveAsync(20, 20); await CaptureAny(page.MainFrame);
            Assert(Last.TagName == "HTML", "bodyless document can be inspected");
        });
        await Scenario("empty document", async () =>
        {
            await Html(""); await Start(); await page.Mouse.MoveAsync(20, 20); await CaptureAny(page.MainFrame);
            await Action("ValidateButton_Click");
        });
        await Scenario("SVG hierarchical selector without identifying attributes", async () =>
        {
            await Html("<svg width=250 height=100><rect width=100 height=80 fill='silver' /><rect data-fixture=target x=120 width=100 height=80 fill='red' /></svg>");
            await Start(); await Hover(page.MainFrame, "[data-fixture=target]"); await CaptureAny(page.MainFrame); await CheckSelectors(page.MainFrame);
        });
        await Scenario("MathML namespace selector", async () =>
        {
            await Html("<math style='font-size:60px'><mi data-fixture=target>x</mi></math>");
            await Start(); await Hover(page.MainFrame, "mi"); await CaptureAny(page.MainFrame); await CheckSelectors(page.MainFrame);
        });
        await Scenario("full-page fixed cover over an input", async () =>
        {
            await Html("<input id=target data-fixture=target><div id=cover style='position:fixed;inset:0;background:silver;z-index:999999'></div>");
            await Start(); await page.Mouse.MoveAsync(20, 20); await SelectLayer(page.MainFrame, "target"); await CaptureAny(page.MainFrame); await CheckSelectors(page.MainFrame);
        });
        await Scenario("body replaced by SPA while indicating", async () =>
        {
            await Html("<input id=old>"); await Start(); await Hover(page.MainFrame, "#old");
            await page.EvaluateExpressionAsync("document.body.innerHTML='<input id=target data-fixture=target>'");
            await page.Mouse.MoveAsync(60, 20);
            Assert(await page.EvaluateExpressionAsync<bool>("!!document.getElementById('_ui_exp_overlay')"), "overlay restored after body replacement");
            await CaptureAny(page.MainFrame); await CheckSelectors(page.MainFrame);
        });
        await Scenario("modal opened after indication starts", async () =>
        {
            await Html("<input id=old><dialog><input id=target data-fixture=target></dialog>"); await Start(); await Hover(page.MainFrame, "#old");
            await page.EvaluateExpressionAsync("document.querySelector('dialog').showModal()"); await Hover(page.MainFrame, "#target");
            Assert(await page.EvaluateExpressionAsync<bool>("document.querySelector('dialog').contains(document.getElementById('_ui_exp_overlay'))"), "overlay follows newly opened modal");
            await CaptureAny(page.MainFrame); await CheckSelectors(page.MainFrame);
        });
        await Scenario("native popover input", async () =>
        {
            await Html("<div popover=manual id=popover><input id=target data-fixture=target></div><script>document.querySelector('#popover').showPopover()</script>");
            await Start(); await Hover(page.MainFrame, "#target"); await CaptureAny(page.MainFrame); await CheckSelectors(page.MainFrame);
        });
        await Scenario("top document navigation while indicating", async () =>
        {
            await Html("<input id=target>"); await Start();
            await page.GoToAsync(server.Url + "nested", new NavigationOptions { ReferrerPolicy = "noReferrer" });
            await Until(() => page.EvaluateExpressionAsync<bool>("!!window._uiExpActive"), "top-document reinjection");
            await Hover(page.MainFrame, "#shared"); await CaptureAny(page.MainFrame);
            Assert(Last.Css == "#shared", "capture works after top navigation");
        });
        await Scenario("frame transitions from same origin to cross origin", async () =>
        {
            await Html("<iframe id=frame src='" + server.Url + "nested'></iframe>");
            await Until(() => Task.FromResult(page.Frames.Any(f => f.Url.EndsWith("/nested"))), "initial frame");
            await Start(); await page.EvaluateFunctionAsync("url=>document.querySelector('#frame').src=url", server.ChildUrl);
            IFrame frame = null;
            await Until(async () => { frame = page.Frames.FirstOrDefault(f => f.Url == server.ChildUrl); return frame != null && await frame.EvaluateExpressionAsync<bool>("!!window._uiExpActive"); }, "frame after origin swap");
            await Hover(frame, "#shared"); await CaptureAny(frame); await Action("ValidateButton_Click");
        });
        await Scenario("captured tab closure reports an error", async () =>
        {
            var original = page; page = await browser.NewPageAsync();
            try
            {
                await Html("<input id=target>"); await Start(); await Hover(page.MainFrame, "#target"); await CaptureAny(page.MainFrame);
                await page.CloseAsync(); Call("ValidateButton_Click", null, null);
                await Until(() => Task.FromResult(Status.StartsWith("❌")), "closed captured tab error");
                Assert(Status.Contains("No active browser tab"), "closed tab cannot redirect actions to another page");
            }
            finally { page = original; }
        });
        await Scenario("rapid cancel then restart cannot capture stale results", async () =>
        {
            await Html("<input id=target>"); int before = window.SavedElements.Count;
            await Start(); await Hover(page.MainFrame, "#target"); await page.Keyboard.PressAsync("Escape");
            await CallAsync("StopSpyingAsync", true); await Start();
            await Hover(page.MainFrame, "#target"); await CaptureAny(page.MainFrame);
            Assert(window.SavedElements.Count == before + 1, "one capture across session restart");
        });
        await Scenario("frame hover activates real CSS hover state", async () =>
        {
            await Html("<iframe src='" + server.ChildUrl + "' style='width:400px;height:180px'></iframe>");
            await Until(() => Task.FromResult(page.Frames.Any(f => f.Url == server.ChildUrl)), "hover test frame");
            var frame = page.Frames.Single(f => f.Url == server.ChildUrl);
            await Start(); await Hover(frame, "#shared"); await CaptureAny(frame);
            // Clear the input's hover within the same frame. Chromium can retain
            // an OOPIF's last hover state when the pointer jumps straight to its parent.
            var inputBox = await (await frame.QuerySelectorAsync("#shared")).BoundingBoxAsync();
            await page.Mouse.MoveAsync(inputBox.X + inputBox.Width + 20, inputBox.Y + inputBox.Height / 2);
            await Until(async () => !await frame.EvaluateExpressionAsync<bool>("document.querySelector('#shared').matches('input:hover')"), "pointer leaves the frame input");
            Assert(!await frame.EvaluateExpressionAsync<bool>("document.querySelector('#shared').matches('input:hover')"), "pointer starts away from frame input");
            await Action("TestHoverButton_Click");
            await Until(() => frame.EvaluateExpressionAsync<bool>("document.querySelector('#shared').matches('input:hover')"), "native frame hover state");
            Assert(true, "hover action moves the real pointer into the captured frame");
        });
        await Scenario("document rejects text-input actions without altering DOM", async () =>
        {
            await Html("<input id=target><p>Keep this page</p>"); await Start(); await Hover(page.MainFrame, "#target");
            for (int i = 0; i < 3; i++) await page.Keyboard.PressAsync("ArrowUp");
            await CaptureAny(page.MainFrame);
            ((System.Windows.Controls.TextBox)window.FindName("TypeInputBox")).Text = "must not replace document";
            Call("TestTypeButton_Click", null, null);
            await Until(() => Task.FromResult(Status.StartsWith("❌") || Status.StartsWith("✅")), "document type result");
            Assert(Status.StartsWith("❌") && await page.EvaluateExpressionAsync<bool>("!!document.querySelector('#target') && document.querySelector('p').textContent==='Keep this page'"), "typing refuses a document node and preserves its content");
            Call("TestClearTextButton_Click", null, null);
            await Until(() => Task.FromResult(Status.StartsWith("❌") || Status.StartsWith("✅")), "document clear result");
            Assert(Status.StartsWith("❌"), "clearing refuses a document node");
        });
    }
}
