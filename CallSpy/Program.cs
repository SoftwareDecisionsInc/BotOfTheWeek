// =============================================================================
// GPAL Bot of the Week - CallSpy
//
// PageSpy's sibling. PageSpy shows what is on a page; CallSpy shows what the page requests. Navigate, and every request
// is recorded with its method, url, resource type and body, so endpoints are read off the site, not guessed.
//
// Filter to XHR and Fetch and you are looking at the site's own API. Pick a call
// and Copy Path gives you the path to paste into SessionRelay, or into a
// GPAL.Request in a workflow.
//
// Runs on any engine. Puppeteer reports requests over CDP, OttoMagic watches with the extension's
// webRequest listener, and Selenium records inside the page and so sees less than either.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using GenerallyPositive;
using GenerallyPositive.Browser;
using GenerallyPositive.GPALForm;
using static GenerallyPositive.Enums;

// What SessionRelay needs to repeat the call, written as a file instead of through the clipboard.
class HandoffTarget
{
    public string PageUrl { get; set; } = string.Empty;
    public string ApiPath { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Headers { get; set; } = string.Empty;
}

class CallSpyHistory
{
    public List<string> RecentUrls { get; set; } = new List<string>();

    // One box per browser, since a profile belongs to one browser. The run uses the one for the browser in the combo.
    public string ChromeProfile { get; set; } = string.Empty;
    public string EdgeProfile { get; set; } = string.Empty;
    public string FirefoxProfile { get; set; } = string.Empty;

    // Host and path of calls to hide for good, such as telemetry beacons. Kept because the noisy ones are the same
    // every day.
    public List<string> Excluded { get; set; } = new List<string>();
}

static class Program
{
    const int MaxHistory = 20;

    static readonly string historyFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "callspy-history.yaml");
    static readonly string handoffPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "callspy-target.yaml");
    static readonly BrowserType[] browserTypes = { BrowserType.Chrome, BrowserType.Edge, BrowserType.FireFox };

    // All four record; they differ in how much they see, which the combo says.
    static readonly AutomationEngine[] engines = { AutomationEngine.PuppeteerPort, AutomationEngine.PuppeteerPipe, AutomationEngine.OttoMagic, AutomationEngine.Selenium };

    // The real profiles the other testers drive. Anti-bot sites deny a page on a fresh temporary profile, and the
    // OttoMagic extension is only installed in these, so the boxes start on them.
    static readonly Dictionary<BrowserType, string> defaultProfiles = new Dictionary<BrowserType, string>
    {
        [BrowserType.Chrome]  = @"C:\profiles\chrome\signedin",
        [BrowserType.Edge]    = @"C:\profiles\edge\signedin",
        [BrowserType.FireFox] = @"C:\profiles\firefox\signedin",
    };

    // The browser's names for each request type. "API only" is the pair to use on a data site.
    static readonly string[] filters = { "API only (XHR, Fetch)", "Everything", "Documents", "Scripts", "Images" };

    static CallSpyHistory history = new CallSpyHistory();
    static IBrowser browser;
    static List<GPALCall> calls = new List<GPALCall>();
    static List<GPALCall> shown = new List<GPALCall>();

    // -- Target ---------------------------------------------------------------

    static GPALInput urlInput = GPAL.Input
        .WithPlaceholder("https://...")
        .WithName("url")
        .ToGPALObject();

    // the same history list PageSpy has, so the two bots are worked the same way
    static GPALListView urlHistoryList = GPAL.ListView
        .WithColumns("Recent", "Why it is here")
        .WithColumnWidth(520, -2)
        .WithView(ListViewMode.Details)
        .WithFullRowSelect(true)
        .WithHeight(350)
        .WithName("urlHistory")
        .WithCallback<EventHandler>(OnHistorySelected)
        .ToGPALObject();

    // The engine entries disabled when Firefox is picked, named once so both places agree.
    const string puppeteerItem = "PuppeteerPort (sees everything)";
    const string puppeteerPipeItem = "PuppeteerPipe (sees everything)";

    static GPALComboBox browserCombo = GPAL.ComboBox
        .WithItems("Chrome", "Edge", "Firefox")
        .WithSelectedIndex(0)
        .WithCallback<EventHandler>(OnBrowserChanged)
        .WithName("browser")
        .ToGPALObject();

    static GPALComboBox engineCombo = GPAL.ComboBox
        .WithItems(puppeteerItem, puppeteerPipeItem, "OttoMagic (sees everything)", "Selenium (XHR and fetch only)")
        .WithSelectedIndex(0)
        .WithName("engine")
        .ToGPALObject();

    static GPALCheckbox headlessCheck = GPAL.CheckboxFor("Hide browser", false);

    static GPALFileSelector chromeProfile = (GPALFileSelector)GPAL.FileSelector
        .WithSelectFolder(true)
        .WithCompact(true)
        .WithTitle("Chrome user data directory")
        .WithPlaceholder("Optional, but an anti-bot site will deny a page loaded without one...")
        .WithName("chromeProfile")
        .WithToolTip("The --user-data-dir Chrome is started on. Type a path or use the button")
        .ToGPALObject();

    static GPALFileSelector edgeProfile = (GPALFileSelector)GPAL.FileSelector
        .WithSelectFolder(true)
        .WithCompact(true)
        .WithTitle("Edge user data directory")
        .WithPlaceholder("Optional, but an anti-bot site will deny a page loaded without one...")
        .WithName("edgeProfile")
        .WithToolTip("The --user-data-dir Edge is started on. Type a path or use the button")
        .ToGPALObject();

    static GPALFileSelector firefoxProfile = (GPALFileSelector)GPAL.FileSelector
        .WithSelectFolder(true)
        .WithCompact(true)
        .WithTitle("FireFox profile directory")
        .WithPlaceholder("Optional, but an anti-bot site will deny a page loaded without one...")
        .WithName("firefoxProfile")
        .WithToolTip("The FireFox profile directory, the one under Profiles. Type a path or use the button")
        .ToGPALObject();

    static GPALButton watchBtn = GPAL.Button
        .WithName("watch")
        .WithText("👁 Watch")
        .WithCallback<EventHandler>((s, e) => watchBtn.Enabled = false)
        .WithCallbackOffUIThread<EventHandler>(OnWatch)
        .ToGPALObject();

    static GPALButton stopBtn = GPAL.Button
        .WithEnabled(false)
        .WithName("stop")
        .WithText("🛑 Stop and Close")
        .WithCallback<EventHandler>((s, e) => stopBtn.Enabled = false)
        .WithCallbackOffUIThread<EventHandler>(OnStop)
        .ToGPALObject();

    static GPALButton refreshBtn = GPAL.Button
        .WithEnabled(false)
        .WithName("refresh")
        .WithText("📥 Collect Calls")
        .WithCallback<EventHandler>((s, e) => refreshBtn.Enabled = false)
        .WithCallbackOffUIThread<EventHandler>(OnCollect)
        .ToGPALObject();

    static GPALButton aboutBtn = GPAL.Button
        .WithName("about")
        .WithText("ℹ About")
        .WithCallback<EventHandler>(OnAbout)
        .ToGPALObject();

    // -- About ----------------------------------------------------------------

    static GPALLabel aboutText = (GPALLabel)GPAL.LabelFor(
        "CallSpy, a GPAL Bot of the Week.\n\n" +
        "Watch a page and record every request it makes,\n" +
        "so a site's own API can be read off it.\n\n" +
        "Built with GPAL - Generally Positive Automation Library.")
        .WithTextAlignment(ContentAlignment.TopCenter);

    static GPALLinkLabel moreBotsLink = (GPALLinkLabel)GPAL.LinkLabel
        .WithUrl("https://gpal.sditx.com/docs/botw")
        .WithText("More Bots of the Week, with their source and walkthroughs")
        .WithTextAlignment(ContentAlignment.MiddleCenter);

    // the default button closes the dialog
    static GPALButton aboutOkBtn = GPAL.Button
        .Default
        .WithName("aboutOk")
        .WithText("OK")
        .WithWidth(50)
        .ToGPALObject();

    // -- Results --------------------------------------------------------------

    static GPALComboBox filterCombo = GPAL.ComboBox
        .WithItems(filters)
        .WithSelectedIndex(0)
        .WithName("filter")
        .WithCallback<EventHandler>(OnFilterChanged)
        .ToGPALObject();

    static GPALListView callList = GPAL.ListView
        .WithColumns("Status", "Method", "Type", "Path", "Host")
        .WithColumnWidth(55, 60, 80, 420, -2)
        .WithView(ListViewMode.Details)
        .WithFullRowSelect(true)
        .WithHeight(240)
        .WithName("calls")
        .WithCallback<EventHandler>(OnCallSelected)
        .ToGPALObject();

    static GPALTextArea detailArea = (GPALTextArea)GPAL.TextArea
        .WithReadOnly(true)
        .WithHeight(130)
        .WithName("detail");

    static GPALButton copyPathBtn = GPAL.Button
        .WithEnabled(false)
        .WithName("copyPath")
        .WithText("📋 Copy Path")
        .WithCallback<EventHandler>(OnCopyPath)
        .ToGPALObject();

    static GPALButton copyUrlBtn = GPAL.Button
        .WithEnabled(false)
        .WithName("copyUrl")
        .WithText("🔗 Copy Full URL")
        .WithCallback<EventHandler>(OnCopyUrl)
        .ToGPALObject();

    // as a GPAL chain, ready to paste into a workflow
    static GPALButton copyHeadersBtn = GPAL.Button
        .WithEnabled(false)
        .WithName("copyHeaders")
        .WithText("🏷 Copy Headers")
        .WithCallback<EventHandler>(OnCopyHeaders)
        .ToGPALObject();

    // Everything known about the call, as the detail pane shows it.
    static GPALButton handoffBtn = GPAL.Button
        .WithEnabled(false)
        .WithName("handoff")
        .WithText("📤 Send to SessionRelay")
        .WithCallback<EventHandler>(OnSendToSessionRelay)
        .ToGPALObject();

    static GPALButton copyAllBtn = GPAL.Button
        .WithEnabled(false)
        .WithName("copyAll")
        .WithText("📄 Copy All")
        .WithCallback<EventHandler>(OnCopyAll)
        .ToGPALObject();

    static GPALButton excludeDomainBtn = GPAL.Button
        .WithEnabled(false)
        .WithName("excludeDomain")
        .WithText("⛔ Exclude Domain")
        .WithCallback<EventHandler>(OnExcludeDomain)
        .ToGPALObject();

    static GPALButton excludeBtn = GPAL.Button
        .WithEnabled(false)
        .WithName("exclude")
        .WithText("🚫 Exclude Similar")
        .WithCallback<EventHandler>(OnExclude)
        .ToGPALObject();

    static GPALButton clearExcludesBtn = GPAL.Button
        .WithName("clearExcludes")
        .WithText("♻ Clear Excludes")
        .WithCallback<EventHandler>(OnClearExcludes)
        .ToGPALObject();

    static GPALButton clearListBtn = GPAL.Button
        .WithName("clearList")
        .WithText("\u2716 Clear List")
        .WithCallback<EventHandler>(OnClearList)
        .ToGPALObject();

    static GPALButton saveBtn = GPAL.Button
        .WithEnabled(false)
        .WithName("save")
        .WithText("💾 Save All")
        .WithCallback<EventHandler>(OnSave)
        .ToGPALObject();

    // -- Tabs -----------------------------------------------------------------

    static GPALTab howToTab = GPAL.Tab.WithName("howToTab").WithText("How To").ToGPALObject();

    // The steps, so the bot explains itself.
    static readonly string[] instructions =
    {
        "CallSpy tells you what a page asks for. PageSpy tells you what is on it.",
        "",
        "1.  URL, and check the profile directory for the browser you picked. The three boxes start",
        "    on the profiles these bots are normally driven on, one per browser, so there is usually",
        "    nothing to type. A fresh profile is a stranger to an anti-bot site and gets the page",
        "    denied; your own profile already has clearance.",
        "",
        "2.  Press Watch. The browser opens, loads the page, and recording starts.",
        "",
        "3.  Use the page. Search, pick dates, press Book. The call you want is usually one the",
        "    page makes while you work, not one it makes on load.",
        "",
        "4.  Press Collect Calls. Nothing updates on its own. This is a snapshot, taken when you",
        "    ask, of everything recorded since Watch. The Calls tab is where it lands.",
        "",
        "5.  The filter starts on API only (XHR, Fetch), which on a data site is the site's own",
        "    API. Status, the first column, is what the page itself got: a 200 is an endpoint",
        "    worth calling, a 400 is one failing for the site too.",
        "",
        "6.  Select a call to see its full url, status, the headers the page sent, and any body.",
        "",
        "7.  Copy Path pastes into SessionRelay's API path. Copy Headers gives you .WithHeader",
        "    lines for a workflow. Copy All gives you the whole thing, method, url, status,",
        "    headers and body, which is the block to paste when you want someone else to see it.",
        "",
        "    Send to SessionRelay writes the call to callspy-target.yaml beside this exe, url,",
        "    path, method, body and headers. In SessionRelay press Load From CallSpy: it asks",
        "    for that file once and remembers it, so after the first time it is one press each",
        "    way and nothing goes through the clipboard.",
        "",
        "8.  Two ways to quieten the list, both remembered between runs because the noisy",
        "    endpoints are the same every day:",
        "",
        "        Exclude Similar   the same host and path, whatever the query string. One",
        "                          beacon endpoint pinged forty times leaves in one press.",
        "",
        "        Exclude Domain    everything from that host. The right one for an analytics",
        "                          or tag-manager domain you never want to see again.",
        "",
        "    Clear Excludes puts them all back.",
        "",
        "9.  Save All writes what is shown, so the filter and the excludes decide what lands",
        "    in the file. YAML, because a call is structured: url, method, type, status, the",
        "    headers the page sent and any body.",
        "",
        "10. Run Fetch and Run REST Handoff resend the selected call in the open session, with",
        "    the headers exactly as the page sent them. Nothing to paste or keep in sync: a",
        "    token issued this session is the live one.",
        "",
        "    Fetch is sent by the page, so it carries the browser's TLS fingerprint, HTTP/2",
        "    framing and header order as well as the cookies. REST handoff is the same call",
        "    without the browser: cookies and headers, none of the transport. Together they",
        "    show which wall a site uses:",
        "",
        "        both answer          the wall was cookies, so the fast browserless route works",
        "        fetch answers only   the wall is the transport, which is what Akamai does",
        "        neither answers      the IP, or the page never really settled",
        "",
        "    Run Fetch first. It can never spoil the session.",
        "",
        "    Run REST Handoff second. If the site refuses it, the session can be spoiled for the",
        "    browser as well, and Fetch will start failing too. Press Watch again: it reopens the",
        "    browser and loads the page, which makes the site hand out new clearance.",
        "",
        "    Try each route twice, a few minutes apart. A handoff often works right after you use",
        "    the page and stops working later, so one try can tell you the wrong thing.",
        "",
        "    A call to another host comes back empty. The page is not allowed to read another",
        "    site's answer. Stick to the calls a site makes to itself.",
        "",
        "",
        "The recent list starts with sites to try. Run the first two back to back: Harrods",
        "answers both routes; Radisson answers Fetch but refuses the REST handoff, which is",
        "Akamai fingerprinting the connection, not checking cookies. The rest are untried",
        "protected retail sites from the antibot list. The Why column says which is which.",
        "",
        "To change the call instead of repeating it, use SessionRelay: edit the body, vary a",
        "parameter, try an endpoint the page never called, or run it on Firefox, Selenium or",
        "OttoMagic. This bot resends what the page actually sent.",
        "",
        "",
        "What each engine can see",
        "",
        "    Puppeteer     everything: documents, scripts, images, XHR and fetch, with request",
        "                  headers and response status. The default, and the one to use when",
        "                  you do not yet know what you are looking for.",
        "",
        "    Selenium      XHR and fetch only. With no CDP event stream the page records for",
        "                  itself, so the browser's own requests are never seen. Chrome and Edge",
        "                  record from the first line of every document; Firefox only after load,",
        "                  so its first burst is missed.",
        "",
        "    OttoMagic     everything, the same breadth as Puppeteer. The extension watches with",
        "                  webRequest, from outside the page, so it keeps recording across a",
        "                  navigation on its own and puts nothing in the page to be noticed."
    };

    static GPALTextArea instructionArea = (GPALTextArea)GPAL.TextArea
        .WithReadOnly(true)
        .WithHeight(500)
        .WithName("instructions");

    static GPALTab watchTab = GPAL.Tab.WithName("watchTab").WithText("Watch").ToGPALObject();
    static GPALTab callsTab = GPAL.Tab.WithName("callsTab").WithText("Calls").ToGPALObject();
    static GPALTab logTab = GPAL.Tab.WithName("logTab").WithText("GPAL Log").ToGPALObject();

    // The Log tab shows every event GPAL publishes.
    static GPALTextArea logArea = (GPALTextArea)GPAL.TextArea
        .WithReadOnly(true)
        .WithHeight(480)
        .WithName("log");

    // Building an event costs a stack capture, and an attached handler gets everything unless it says otherwise, so
    // DEBUG is requested, not filtered on arrival. DEEPDEBUG is never requested: thousands of events a run belong in a
    // log file, not a watched tab.
    static GPALCheckbox debugCheck = (GPALCheckbox)GPAL.Checkbox
        .WithChecked(true)
        .WithName("debug")
        .WithText("Show DEBUG")
        .WithToolTip("Off: the log a person wants to read. On: adds GPAL's DEBUG tracing, for when a call does something you cannot explain.")
        .WithCallback<EventHandler>(OnDebugChanged)
        .ToGPALObject();

    static GPALButton clearLogBtn = GPAL.Button
        .WithName("clearLog")
        .WithText("🧹 Clear Log")
        .WithCallback<EventHandler>((s, e) =>
        {
            logArea.Text = string.Empty;

            lock (fullLog)
                fullLog.Clear();
        })
        .ToGPALObject();

    // every line since the last Clear Log, for Save Log; the box keeps only the last 2000
    static List<string> fullLog = new List<string>();

    // writes every line since the last Clear Log, not only the 2000 the box is showing
    static GPALButton saveLogBtn = GPAL.Button
        .WithName("saveLog")
        .WithText("💾 Save Log")
        .WithCallback<EventHandler>(OnSaveLog)
        .ToGPALObject();

    // -- Trying the call, both ways -------------------------------------------

    static GPALButton fetchBtn = GPAL.Button
        .WithEnabled(false)
        .WithName("runFetch")
        .WithText("⚡ Run Fetch")
        .WithCallback<EventHandler>((s, e) => fetchBtn.Enabled = false)
        .WithCallbackOffUIThread<EventHandler>(OnRunFetch)
        .ToGPALObject();

    static GPALButton restBtn = GPAL.Button
        .WithEnabled(false)
        .WithName("runRest")
        .WithText("🤝 Run REST Handoff")
        .WithCallback<EventHandler>((s, e) => restBtn.Enabled = false)
        .WithCallbackOffUIThread<EventHandler>(OnRunRest)
        .ToGPALObject();

    static GPALTextArea resultArea = (GPALTextArea)GPAL.TextArea
        .WithReadOnly(true)
        .WithHeight(120)
        .WithName("result");

    // Above the list, in two halves: using a call on the left, tidying the list on the right.
    static GPALSplitter callActions = (GPALSplitter)GPAL.Splitter.HSplit(0.5)
        .WithFormControl(GPAL.LabelFor("Take the selected call:"))
        .WithFormControl(copyPathBtn)
        .WithFormControl(copyUrlBtn)
        .WithFormControl(copyHeadersBtn)
        .WithFormControl(copyAllBtn)
        .WithFormControl(handoffBtn)
        .WithFormControl(GPAL.SplitRight)
        .WithFormControl(GPAL.LabelFor("Tidy the list:"))
        .WithFormControl(excludeBtn)
        .WithFormControl(excludeDomainBtn)
        .WithFormControl(clearExcludesBtn)
        .WithFormControl(clearListBtn)
        .WithFormControl(saveBtn)
        //.WithHeight(165)
        .ToGPALObject();

    static GPALStatusStrip statusBar = GPAL.StatusStrip
        .WithText("Give it a url and press Watch.")
        .WithName("status")
        .ToGPALObject();

    // -- Entry point ----------------------------------------------------------

    [STAThread]
    static void Main()
    {
        GPAL
            .WithPublishToConsole(GPALEventType.USERALL)
            .WithHandlerEvents(GPALEventType.All & ~GPALEventType.DEEPDEBUG)
            .WithInformationHandler(OnGPALEvent)
            .WithExceptionHandler(OnGPALEvent)
            .WithDriverLocation(@"c:\drivers");

        var form = GPAL.Form
            .WithTitle("CallSpy  |  A GPAL Bot of the Week")
            .WithWidth(900)
            .WithHeight(900)
            .WithTop(10)

            .WithFormControl(watchTab)
            .WithFormControl(GPAL.LabelFor("URL to watch:"))
            .WithFormControl(urlInput)
            .WithFormControl(urlHistoryList)
            .WithFormControl(browserCombo)
            .WithFormControl(engineCombo)
            .WithFormControl(headlessCheck)
            .WithFormControl(GPAL.LabelFor("Profile directories, one per browser. The one for the browser above is the one this run uses:"))
            .WithFormControl(chromeProfile)
            .WithFormControl(edgeProfile)
            .WithFormControl(firefoxProfile)
            .WithFormControl(watchBtn)
            .WithFormControl(refreshBtn)
            .WithFormControl(stopBtn)
            .WithFormControl(aboutBtn)

            .WithFormControl(callsTab)
            .WithFormControl(GPAL.LabelFor("Show:"))
            .WithFormControl(filterCombo)

            .WithFormControl(callActions)

            .WithFormControl(callList)
            .WithFormControl(detailArea)

            .WithFormControl(GPAL.LabelFor("Try the selected call against the session that is already open:"))
            .WithFormControl(fetchBtn)
            .WithFormControl(restBtn)
            .WithFormControl(resultArea)

            .WithFormControl(logTab)
            .WithFormControl(debugCheck)
            .WithFormControl(clearLogBtn)
            .WithFormControl(saveLogBtn)
            .WithFormControl(logArea)

            .WithFormControl(howToTab)
            .WithFormControl(instructionArea)

            .WithFormControl(statusBar);

        LoadHistory();

        instructionArea.Text = string.Join(Environment.NewLine, instructions);

        form.ShowDialog();

        browser?.Close();
    }

    // The verdict cookies of Akamai, Cloudflare, DataDome and PerimeterX. A real profile keeps them, so one visit
    // judged a bot marks the profile for every later visit, whatever the engine. AntibotTester explains it in full.
    static readonly string[] VerdictCookieNames =
    {
        "_abck", "bm_sz", "bm_sv", "ak_bmsc", "bm_mi", "bm_so", "bm_lso", "bm_s",
        "cf_clearance", "__cf_bm",
        "datadome",
        "_px3", "_pxhd", "_pxvid"
    };

    // The site's verdict cookies, declared on its url so the deletes touch only those cookies on this site. A domain
    // without www covers every subdomain.
    static GPALUrl WithVerdictCookies(string url)
    {
        string host = new Uri(url).Host;
        string siteDomain = host.StartsWith("www.") ? host.Substring(4) : host;

        GPALUrl retVal = url;

        foreach (string name in VerdictCookieNames)
            retVal.WithStorageType(WebsiteStorageType.cookie).WithStorageDomain(siteDomain).WithStorageKey(name).WithUserDefined("verdict");

        return retVal;
    }

    // Deletes only the site's verdict cookies, so logins and consent choices survive. For points each delete at the
    // site's url and lasts one action, so it starts each delete.
    static void ClearVerdictCookies(IBrowser browser, GPALUrl siteUrl, bool onTheSite)
    {
        foreach (string name in VerdictCookieNames)
            browser
                .For(siteUrl)
                    .WithUserDefinedFilter("verdict")
                    .WithStorageKey(name)
                    .RunDelete(WebsiteStorageType.cookie);

        GPAL.PublishSimpleEvent(GPALEventType.NOTICE, $"Cleared the verdict cookies for [{siteUrl.Url}] {(true == onTheSite ? "on arrival" : "before the visit")}");
    }

    // Goes to the page with the site's verdict cookies deleted first, so a verdict an earlier visit left in the profile
    // does not carry in. A browser not started yet holds the deletes and runs them as it starts, before the page loads.
    // Selenium on Firefox lists only the current site's cookies, so there the page loads, the cookies go, and it loads
    // again; the refreshed load is the one judged.
    static void VisitWithoutVerdicts(IBrowser browser, BrowserType browserType, string url, bool headless)
    {
        GPALUrl siteUrl = WithVerdictCookies(url);
        bool onArrival = BrowserType.FireFox == browserType && true == browser.UseSelenium;

        if (false == onArrival)
            ClearVerdictCookies(browser, siteUrl, false);

        // Get opens the page headless, GoTo in a window
        if (true == headless)
            browser.Get(siteUrl);
        else
            browser.GoTo(siteUrl);

        if (true == onArrival)
        {
            ClearVerdictCookies(browser, siteUrl, true);

            _ = browser.Refresh;
        }
    }

    // -- Watching -------------------------------------------------------------

    static void OnWatch(object s, EventArgs e)
    {
        try
        {
            // Attached off the UI thread, so the form keeps painting and the Log tab fills in as the browser works.
            try
            {
                string url = urlInput.Text?.Trim();

                if (string.IsNullOrEmpty(url))
                {
                    SetStatus("Give it a url first.");
                    return;
                }

                browser?.Close();

                SetStatus("Starting the browser...");

                BrowserType browserType = browserTypes[Math.Max(0, browserCombo.SelectedIndex)];

                // Puppeteer reports its requests over CDP. Selenium has no such stream, so the page records for itself
                // and sees only what its own script requested.
                browser = GPAL.Browser
                    .WithBrowserType(browserType)
                    .WithUseStealth(StealthType.PatchDriver)
                    .WithAutomationEngine(engines[Math.Max(0, engineCombo.SelectedIndex)])
                    .WithWaitOnDocumentReady(30_000)
                    .ToGPALObject();

                string profile = ProfileFor(browserType);

                if (false == string.IsNullOrEmpty(profile))
                    browser.WithProfileDataDirectory(profile);

                VisitWithoutVerdicts(browser, browserType, url, true == headlessCheck.Checked);

                refreshBtn.Enabled = true;
                stopBtn.Enabled = true;

                RememberUrl(url);
                OnCollect(s, e);

                // recording starts before the navigation, so the page's own first requests are in the list
                _ = browser.Refresh;

                SetStatus($"Watching [{url}]. Click around in the browser, then Collect Calls again.");
            }
            catch (Exception ex)
            {
                GPAL.PublishSimpleEvent(GPALEventType.EXCEPTION, $"[OnWatch] did not finish", null, GPALObjectType.GPALForm, ex);
                SetStatus($"[{ex.GetType().Name}] {ex.Message}");
            }
        }
        finally
        {
            // the click turned the button off; it comes back as the program's state has it
            watchBtn.Enabled = true;
        }
    }

    // the browser keeps recording while you use it, so collecting again picks up whatever it did since
    static void OnCollect(object s, EventArgs e)
    {
        try
        {
            // Attached off the UI thread, so the form keeps painting and the Log tab fills in as the browser works.
            try
            {
                if (null == browser) return;

                // Turned on again so tabs opened since the last collect record too; it returns everything since the
                // browser started. Turning it on twice costs nothing.
                browser.CaptureCalls(out calls);

                Render();

                SetStatus($"[{calls.Count}] calls so far, [{shown.Count}] shown.");
            }
            catch (Exception ex)
            {
                GPAL.PublishSimpleEvent(GPALEventType.EXCEPTION, $"[OnCollect] did not finish", null, GPALObjectType.GPALForm, ex);
                SetStatus($"[{ex.GetType().Name}] {ex.Message}");
            }
        }
        finally
        {
            // the click turned the button off; it comes back as the program's state has it
            refreshBtn.Enabled = null != browser;
        }
    }

    static void OnStop(object s, EventArgs e)
    {
        try
        {
            // Attached off the UI thread, so the form keeps painting and the Log tab fills in as the browser works.
            try
            {
                OnCollect(s, e);

                browser?.Close();
                browser = null;

                refreshBtn.Enabled = false;
                stopBtn.Enabled = false;

                SetStatus($"Stopped. [{calls.Count}] calls kept.");
            }
            catch (Exception ex)
            {
                GPAL.PublishSimpleEvent(GPALEventType.EXCEPTION, $"[OnStop] did not finish", null, GPALObjectType.GPALForm, ex);
                SetStatus($"[{ex.GetType().Name}] {ex.Message}");
            }
        }
        finally
        {
            // the click turned the button off; it comes back as the program's state has it
            stopBtn.Enabled = null != browser;
        }
    }

    // -- Showing --------------------------------------------------------------

    // Puppeteer has no Firefox, so its entry is disabled rather than Firefox left off the list. Firefox still records
    // through OttoMagic, and through Selenium with less detail.
    static void OnBrowserChanged(object s, EventArgs e)
    {
        bool firefox = BrowserType.FireFox == browserTypes[Math.Max(0, browserCombo.SelectedIndex)];

        if (true == firefox)
        {
            engineCombo.DisableItem(puppeteerItem);
            engineCombo.DisableItem(puppeteerPipeItem);

            AutomationEngine picked = engines[Math.Max(0, engineCombo.SelectedIndex)];

            if (AutomationEngine.PuppeteerPort == picked || AutomationEngine.PuppeteerPipe == picked)
                SetStatus("Firefox records through OttoMagic, or through Selenium, which sees only what page script requested.");
        }
        else
        {
            engineCombo.EnableItem(puppeteerItem);
            engineCombo.EnableItem(puppeteerPipeItem);
        }
    }

    // Clears the list and the browser's recorder. Clearing only the list lasts until the next Collect, which gets the
    // whole history back.
    static void OnClearList(object s, EventArgs e)
    {
        // Asked when something would be lost: calls on the list, or uncollected calls in the browser, which the clear
        // also discards.
        if ((0 < calls.Count || null != browser) && DialogResult.Yes != MessageBox.Show(
                $"Clear the {calls.Count} captured calls?",
                "Clear List", MessageBoxButtons.YesNo, MessageBoxIcon.Warning))
            return;

        browser?.ClearCapturedCalls();

        calls = new List<GPALCall>();

        Render();
        detailArea.Text = string.Empty;
        SetStatus("Cleared. What shows next is what the page does next.");
    }

    static void OnFilterChanged(object s, EventArgs e) => Render();

    static void Render()
    {
        shown = new List<GPALCall>();

        foreach (GPALCall call in calls)
            if (true == Wanted(call))
                shown.Add(call);

        callList.ClearItems();

        // The status the page itself got. Without it, an endpoint failing for the site looks like one worth calling.
        foreach (GPALCall call in shown)
            callList.AddItem(0 == call.Status ? "" : call.Status.ToString(), call.Method ?? "", call.ResourceType ?? "", PathOf(call.Url), HostOf(call.Url));

        detailArea.Text = string.Empty;
        copyPathBtn.Enabled = false;
        copyUrlBtn.Enabled = false;
        saveBtn.Enabled = 0 < shown.Count;
    }

    // Host and path without the query: two calls with the same one are the same endpoint.
    static string SignatureOf(string url)
    {
        string retVal = url ?? "";

        if (true == Uri.TryCreate(url, UriKind.Absolute, out Uri parsed))
            retVal = parsed.Host + parsed.AbsolutePath;

        return retVal;
    }

    static bool Wanted(GPALCall call)
    {
        string type = call.ResourceType ?? "";
        bool retVal;

        // Two kinds of exclude in one list: a host alone drops everything from it; a host with a path drops that
        // endpoint whatever its query string.
        if (true == history.Excluded.Contains(SignatureOf(call.Url)) || true == history.Excluded.Contains(HostOf(call.Url)))
            return false;

        switch (filterCombo.SelectedIndex)
        {
            case 1: retVal = true; break;
            case 2: retVal = type.Equals("Document", StringComparison.OrdinalIgnoreCase); break;
            case 3: retVal = type.Equals("Script", StringComparison.OrdinalIgnoreCase); break;
            case 4: retVal = type.Equals("Image", StringComparison.OrdinalIgnoreCase); break;
            default:
                retVal = type.Equals("XHR", StringComparison.OrdinalIgnoreCase)
                      || type.Equals("Fetch", StringComparison.OrdinalIgnoreCase);
                break;
        }

        return retVal;
    }

    static void OnCallSelected(object s, EventArgs e)
    {
        int index = callList.SelectedIndex;

        if (0 > index || index >= shown.Count) return;

        GPALCall call = shown[index];

        List<string> lines = new List<string>
        {
            $"{call.Method} {call.Url}",
            $"status    {(0 == call.Status ? "no response recorded" : call.Status.ToString())}",
            $"type      {call.ResourceType}",
            $"initiator {call.Initiator}",
            ""
        };

        // The headers the page sent. The ones its own code added are what the API expects and a hand-written request
        // leaves out, so these are the ones to copy into .WithHeader.
        foreach (KeyValuePair<string, string> header in call.Headers)
            lines.Add($"{header.Key}: {header.Value}");

        lines.Add("");
        lines.Add(true == string.IsNullOrEmpty(call.PostData) ? "no body" : call.PostData);

        detailArea.Text = string.Join(Environment.NewLine, lines);

        copyPathBtn.Enabled = true;
        copyUrlBtn.Enabled = true;
        copyHeadersBtn.Enabled = 0 < call.Headers.Count;
        copyAllBtn.Enabled = true;
        handoffBtn.Enabled = true;
        excludeBtn.Enabled = true;
        excludeDomainBtn.Enabled = true;
        fetchBtn.Enabled = null != browser;
        restBtn.Enabled = null != browser;
    }

    // The path, since SessionRelay and GPAL.Request both resolve it against the page's origin.
    static void OnCopyPath(object s, EventArgs e)
    {
        int index = callList.SelectedIndex;

        if (0 > index || index >= shown.Count) return;

        Clipboard.SetText(PathOf(shown[index].Url));

        SetStatus($"Copied [{PathOf(shown[index].Url)}]");
    }

    static void OnCopyUrl(object s, EventArgs e)
    {
        int index = callList.SelectedIndex;

        if (0 > index || index >= shown.Count) return;

        Clipboard.SetText(shown[index].Url);

        SetStatus("Copied the full url.");
    }

    // Written as .WithHeader calls for a GPALRequest, ready to paste.
    static void OnCopyHeaders(object s, EventArgs e)
    {
        int index = callList.SelectedIndex;

        if (0 > index || index >= shown.Count) return;

        List<string> chain = new List<string>();

        foreach (KeyValuePair<string, string> header in shown[index].Headers)
            chain.Add($"    .WithHeader(\"{header.Key}\", \"{header.Value}\")");

        Clipboard.SetText(string.Join(Environment.NewLine, chain));

        SetStatus($"Copied [{chain.Count}] headers as .WithHeader calls.");
    }

    // Drops the call and every other call to the same endpoint, so a beacon pinged forty times with forty query strings
    // leaves the list at once.
    static void OnExclude(object s, EventArgs e)
    {
        int index = callList.SelectedIndex;

        if (0 > index || index >= shown.Count) return;

        string signature = SignatureOf(shown[index].Url);

        if (false == history.Excluded.Contains(signature))
            history.Excluded.Add(signature);

        SaveHistory();
        Render();

        SetStatus($"Excluded [{signature}]. [{history.Excluded.Count}] excluded, [{shown.Count}] shown.");
    }

    // The bigger hammer: a whole analytics or tag-manager domain instead of one endpoint.
    static void OnExcludeDomain(object s, EventArgs e)
    {
        int index = callList.SelectedIndex;

        if (0 > index || index >= shown.Count) return;

        string host = HostOf(shown[index].Url);

        if (true == string.IsNullOrEmpty(host)) return;

        if (false == history.Excluded.Contains(host))
            history.Excluded.Add(host);

        SaveHistory();
        Render();

        SetStatus($"Excluded everything from [{host}]. [{history.Excluded.Count}] excluded, [{shown.Count}] shown.");
    }

    // -- Trying the call, both ways -------------------------------------------

    // The browser sets these itself and fetch will not override them. The ones that matter, the app's own, are not in
    // this list.
    static readonly string[] browserOwned =
    {
        "referer", "user-agent", "host", "origin", "cookie", "content-length",
        "connection", "accept-encoding", "sec-ch-ua", "sec-ch-ua-mobile", "sec-ch-ua-platform",
        "sec-fetch-dest", "sec-fetch-mode", "sec-fetch-site"
    };

    static GPALCall Selected()
    {
        int index = callList.SelectedIndex;

        return 0 > index || index >= shown.Count ? null : shown[index];
    }

    // Sent by the page, so it carries the browser's TLS fingerprint, framing and header order as well as the cookies.
    // The headers come from the captured call, so a token issued this session is the live one.
    static void OnRunFetch(object s, EventArgs e)
    {
        try
        {
            GPALCall call = Selected();

            if (null == call || null == browser) return;

            try
            {
                // The whole url, not the path. A page calls its own origin and several others, and a bare path would
                // resolve against whichever page is loaded.
                GPALRequest request = (GPALRequest)GPAL.Request
                    .WithPath(call.Url)
                    .WithHttpMethod("POST".Equals(call.Method, StringComparison.OrdinalIgnoreCase) ? HttpVerb.Post : HttpVerb.Get)
                    .WithBody(call.PostData)
                    .WithName("callspy");

                foreach (KeyValuePair<string, string> header in call.Headers)
                    if (false == Array.Exists(browserOwned, owned => owned.Equals(header.Key, StringComparison.OrdinalIgnoreCase)))
                        request.WithHeader($"{header.Key}: {header.Value}");

                string json = null;

                browser
                    .Fetch(request)
                    .SaveTo(ref json);

                // Set by the fetch from the response envelope, so success is stated, not inferred from a body that
                // looks right.
                // A page can call another origin because it never reads the answer. Reading it is a different request
                // to the browser, and without CORS headers it is blocked before it leaves. Useful endpoints are nearly
                // always same origin.
                bool crossOrigin = false == HostOf(call.Url).Equals(HostOf(urlInput.Text), StringComparison.OrdinalIgnoreCase);
                string note = true == crossOrigin && 0 == browser.ServerResponseCode
                    ? $"  (nothing came back, and [{HostOf(call.Url)}] is not the page's origin, so CORS refused it)"
                    : "";

                Show($"Fetch, status [{browser.ServerResponseCode}]{note}", json);
            }
            catch (Exception ex)
            {
                GPAL.PublishSimpleEvent(GPALEventType.EXCEPTION, $"[Run Fetch] did not finish", null, GPALObjectType.GPALForm, ex);

                Show("Fetch threw", $"{ex.GetType().Name}{Environment.NewLine}{ex.Message}");
            }
        }
        finally
        {
            // the click turned the button off; it comes back as the program's state has it
            fetchBtn.Enabled = null != browser && null != Selected();
        }
    }

    // The session leaves the browser: cookies, live user agent and accept-language, and none of the transport.
    static void OnRunRest(object s, EventArgs e)
    {
        try
        {
            GPALCall call = Selected();

            if (null == call || null == browser) return;

            try
            {
                browser.ContinueAsRESTClient(out IRESTClient client);

                // A site that fingerprints the transport accepts the connection and never answers, so the default
                // minute would be a minute waiting on a call already lost.
                client.WithTimeout(TimeSpan.FromSeconds(15));

                foreach (KeyValuePair<string, string> header in call.Headers)
                    if (false == Array.Exists(browserOwned, owned => owned.Equals(header.Key, StringComparison.OrdinalIgnoreCase)))
                        client.WithHeader(header.Key, header.Value);

                string body = "POST".Equals(call.Method, StringComparison.OrdinalIgnoreCase)
                    ? client.WithEndpoint(call.Url).WithParameters(call.PostData).WithHttpMethod("POST").Execute(false)
                    : client.WithEndpoint(call.Url).WithHttpMethod("GET").Execute(false);

                Show($"REST handoff, status [{((RESTClient)client).StatusCode}]", body);
            }
            catch (Exception ex)
            {
                GPAL.PublishSimpleEvent(GPALEventType.EXCEPTION, $"[Run REST Handoff] did not finish", null, GPALObjectType.GPALForm, ex);

                // Into the result, not just the status bar. A run that threw looks like one that returned nothing if
                // its only trace scrolls past in a caption.
                Show("REST handoff threw", $"{ex.GetType().Name}{Environment.NewLine}{ex.Message}");
            }
        }
        finally
        {
            // the click turned the button off; it comes back as the program's state has it
            restBtn.Enabled = null != browser && null != Selected();
        }
    }

    static void Show(string route, string body)
    {
        resultArea.Text = $"{route}, [{body?.Length ?? 0}] characters{Environment.NewLine}{Environment.NewLine}{Shortened(body)}";

        SetStatus($"[{route}] returned [{body?.Length ?? 0}] characters.");
    }

    // A rate response runs to hundreds of kilobytes, which no text area wants and nobody reads.
    static string Shortened(string body)
    {
        string retVal = body ?? string.Empty;

        if (4_000 < retVal.Length)
            retVal = retVal.Substring(0, 4_000) + $"{Environment.NewLine}...[{retVal.Length} characters in all]";

        return retVal;
    }

    // The captured call, written where SessionRelay picks it up. Headers included, minus the ones the browser owns, so
    // everything written can be sent.
    static void OnSendToSessionRelay(object s, EventArgs e)
    {
        GPALCall call = Selected();

        if (null == call) return;

        List<string> headers = new List<string>();

        foreach (KeyValuePair<string, string> header in call.Headers)
            if (false == Array.Exists(browserOwned, owned => owned.Equals(header.Key, StringComparison.OrdinalIgnoreCase)))
                headers.Add($"{header.Key}: {header.Value}");

        HandoffTarget target = new HandoffTarget
        {
            PageUrl = urlInput.Text?.Trim() ?? string.Empty,
            ApiPath = call.Url,
            Method = "POST".Equals(call.Method, StringComparison.OrdinalIgnoreCase) ? "POST" : "GET",
            ContentType = "Json",
            Body = call.PostData ?? string.Empty,
            Headers = string.Join(Environment.NewLine, headers)
        };

        GPALFile handoffFile = handoffPath;

        GPAL.Converter
            .WithInput(target)
            .SaveTo(handoffFile);

        SetStatus($"Written to [{handoffFile.Filename}]. Press Load From CallSpy in SessionRelay.");
    }

    static void OnCopyAll(object s, EventArgs e)
    {
        if (true == string.IsNullOrEmpty(detailArea.Text)) return;

        Clipboard.SetText(detailArea.Text);

        SetStatus("Copied the call, its headers and its body.");
    }

    static void OnClearExcludes(object s, EventArgs e)
    {
        int had = history.Excluded.Count;

        // Ask first: the button is easy to hit by accident, and the excludes are built by hand.
        if (0 < had && DialogResult.Yes != MessageBox.Show(
                $"Clear all {had} excluded domains?",
                "Clear Excludes", MessageBoxButtons.YesNo, MessageBoxIcon.Warning))
            return;

        history.Excluded.Clear();

        SaveHistory();
        Render();

        SetStatus($"Cleared [{had}] excludes. [{shown.Count}] shown.");
    }

    // What is shown, not what was captured, so the excludes and the filter decide what goes in the file.
    static void OnSave(object s, EventArgs e)
    {
        using (SaveFileDialog dialog = new SaveFileDialog())
        {
            dialog.Title = "Save the calls shown";
            dialog.Filter = "YAML Files (*.yaml)|*.yaml|All Files (*.*)|*.*";
            dialog.InitialDirectory = AppDomain.CurrentDomain.BaseDirectory;
            dialog.FileName = "calls.yaml";

            if (DialogResult.OK != dialog.ShowDialog()) return;

            GPALFile saveFile = dialog.FileName;

            GPAL.Converter
                .WithInput(shown)
                .SaveTo(saveFile);

            SetStatus($"Wrote [{shown.Count}] calls to [{saveFile.Filename}].");
        }
    }

    // -- Url parts ------------------------------------------------------------

    static string PathOf(string url)
    {
        string retVal = url ?? "";

        if (true == Uri.TryCreate(url, UriKind.Absolute, out Uri parsed))
            retVal = parsed.PathAndQuery;

        return retVal;
    }

    static string HostOf(string url)
    {
        string retVal = "";

        if (true == Uri.TryCreate(url, UriKind.Absolute, out Uri parsed))
            retVal = parsed.Host;

        return retVal;
    }

    // -- History --------------------------------------------------------------

    // Somewhere to start on a first run. Run the first two back to back: one answers both routes, the other only the
    // in-page one. The rest are untried protected retail sites from the antibot list.
    static readonly string[,] knownTargets =
    {
        { "https://www.harrods.com/",                                                                  "Akamai. rest handoff answers only while _abck is freshly validated" },
        { "https://www.radissonhotels.com/en-us/hotels/radisson-individuals-munich-airport/rooms",      "Akamai. fetch answers, rest handoff denied" },
        { "https://www.ssense.com/en-us/men/designers/acne-studios",                                    "Cloudflare. 403 to plain curl" },
        { "https://www.gucci.com/us/en/pr/women/shoes-for-women/sandals-for-women/flat-sandals-for-women/womens-slide-sandal-with-horsebit-p-834427AAE046273", "drops the TLS handshake. plain curl never gets a response" },
        { "https://www.canadagoose.com/it/it/shop/donna/capispalla/giacche-imbottite/shop-womens-puffers", "Kasada. 429 to plain curl" },
        { "https://www.depop.com/",                                                                     "Cloudflare. 403 to plain curl" },
        { "https://loccitane.com/en-us/olio-jeunesse-divine-30ml-27DH030I22.html",                      "drops the TLS handshake. plain curl never gets a response" },
        { "https://www.plus.nl/",                                                                       "Imperva. 403 to plain curl" },
        { "https://leroymerlin.fr",                                                                     "DataDome. both routes answer its suggest api. its own CDN 503s at times" }
    };

    // Why a seeded url is in the list. Not saved, because it belongs to the seed, not the history.
    static readonly Dictionary<string, string> targetNotes = new Dictionary<string, string>();

    static void LoadHistory()
    {
        for (int target = 0; target < knownTargets.GetLength(0); target++)
            targetNotes[knownTargets[target, 0]] = knownTargets[target, 1];

        if (true == File.Exists(historyFile))
            GPAL.Converter
                .WithInput((GPALFile)historyFile)
                .SaveTo(ref history);

        // The known sites are always added, since a history saved before they existed would never show them. They go at
        // the end, so your last url stays on top.
        for (int target = 0; target < knownTargets.GetLength(0); target++)
            if (false == history.RecentUrls.Contains(knownTargets[target, 0]))
                history.RecentUrls.Add(knownTargets[target, 0]);

        RefreshHistoryList();

        if (0 < history.RecentUrls.Count)
            urlInput.Text = history.RecentUrls[0];

        // The compact selector builds its box empty, so the paths go in here: last run's, or on first open, the profile
        // that browser normally uses.
        chromeProfile.SetPath(false == string.IsNullOrEmpty(history.ChromeProfile) ? history.ChromeProfile : defaultProfiles[BrowserType.Chrome]);
        edgeProfile.SetPath(false == string.IsNullOrEmpty(history.EdgeProfile) ? history.EdgeProfile : defaultProfiles[BrowserType.Edge]);
        firefoxProfile.SetPath(false == string.IsNullOrEmpty(history.FirefoxProfile) ? history.FirefoxProfile : defaultProfiles[BrowserType.FireFox]);
    }

    // The box for the browser about to start. A profile belongs to one browser; Chrome's user data directory given to
    // Firefox is a browser that will not open.
    static string ProfileFor(BrowserType browserType)
    {
        string profile = BrowserType.Edge == browserType ? edgeProfile.SelectedPath
                       : BrowserType.FireFox == browserType ? firefoxProfile.SelectedPath
                       : chromeProfile.SelectedPath;

        return profile?.Trim() ?? string.Empty;
    }

    static void RefreshHistoryList()
    {
        urlHistoryList.ClearItems();

        foreach (string url in history.RecentUrls)
            urlHistoryList.AddItem(url, true == targetNotes.ContainsKey(url) ? targetNotes[url] : "");
    }

    static void OnHistorySelected(object s, EventArgs e)
    {
        int index = urlHistoryList.SelectedIndex;

        if (0 > index || index >= history.RecentUrls.Count) return;

        urlInput.Text = history.RecentUrls[index];
    }

    static void RememberUrl(string url)
    {
        history.RecentUrls.Remove(url);
        history.RecentUrls.Insert(0, url);

        while (MaxHistory < history.RecentUrls.Count)
            history.RecentUrls.RemoveAt(history.RecentUrls.Count - 1);

        history.ChromeProfile = chromeProfile.SelectedPath?.Trim() ?? string.Empty;
        history.EdgeProfile = edgeProfile.SelectedPath?.Trim() ?? string.Empty;
        history.FirefoxProfile = firefoxProfile.SelectedPath?.Trim() ?? string.Empty;

        SaveHistory();
        RefreshHistoryList();
    }

    static void SaveHistory()
    {
        GPAL.Converter
            .WithInput(history)
            .SaveTo((GPALFile)historyFile);
    }

    static void OnAbout(object s, EventArgs e)
    {
        GPAL.Form
            .WithTitle("About CallSpy")
            .WithWidth(420)
            .WithFormControl(aboutText)
            .WithFormControl(GPAL.SpacerFor(12))
            .WithFormControl(moreBotsLink)
            .WithFormControl(GPAL.SpacerFor(12))
            .WithFormControl(aboutOkBtn)
            .ShowDialog();
    }

    // Both GPAL channels land here. AppendLine marshals to the UI thread and keeps the last 2000 lines.
    static void OnGPALEvent(object sender, GPAL.GPALEventArgs e)
    {
        if (true == string.IsNullOrEmpty(e.Message) || false == logArea.IsReady) return;

        logArea.AppendLine($"[{e.GPALEventType}] {e.Message}", 2_000);

        lock (fullLog)
            fullLog.Add($"[{e.GPALEventType}] {e.Message}");

        if (null != e.ExceptionRaised)
        {
            logArea.AppendLine($"    {e.ExceptionRaised.Message}", 2_000);

            lock (fullLog)
                fullLog.Add($"    {e.ExceptionRaised.Message}");
        }
    }

    // DEBUG is requested or not at the source, so an unticked box costs nothing instead of a discarded stack capture
    // per event. DEEPDEBUG is in neither mask.
    static void OnDebugChanged(object s, EventArgs e)
    {
        GPAL.WithHandlerEvents(true == debugCheck.Checked
            ? GPALEventType.All & ~GPALEventType.DEEPDEBUG
            : GPALEventType.All & ~(GPALEventType.DEBUG | GPALEventType.DEEPDEBUG));

        SetStatus($"DEBUG logging is {(true == debugCheck.Checked ? "on" : "off")}.");
    }

    // Asks where to save, then writes every line since the last Clear Log, up to the moment of the click.
    static void OnSaveLog(object s, EventArgs e)
    {
        using (System.Windows.Forms.SaveFileDialog dialog = new System.Windows.Forms.SaveFileDialog
        {
            Title = "Save the log",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"{System.Diagnostics.Process.GetCurrentProcess().ProcessName}_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        })
        {
            if (System.Windows.Forms.DialogResult.OK == dialog.ShowDialog())
            {
                string[] lines;

                lock (fullLog)
                    lines = fullLog.ToArray();

                System.IO.File.WriteAllLines(dialog.FileName, lines);

                SetStatus($"Saved [{lines.Length}] log lines to [{dialog.FileName}]");
            }
        }
    }

    static void SetStatus(string message) => statusBar.Text = message;
}
