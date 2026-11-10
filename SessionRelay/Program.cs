// =============================================================================
// GPAL Bot of the Week - SessionRelay
//
// The hybrid pattern from The Web Scraping Club: earn a session in a browser,
// then call the site's own API with it. GPAL can do that two ways, and this bot
// runs both against the same session so you can see which wall you are at.
//
//   Fetch          the page issues the request, so it carries the browser's TLS
//                  fingerprint, HTTP/2 framing and header order as well as the
//                  cookies. Nothing outside the browser can copy that.
//   REST handoff   .ContinueAsRESTClient hands the session to a browserless
//                  client: cookies, live user agent and accept-language. Fast,
//                  and it keeps working after the browser is closed.
//
// One press of Start Session, then Fetch and REST Handoff as many times as you
// like against it, with the status and the body of every run kept in the list.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using GenerallyPositive;
using GenerallyPositive.Browser;
using GenerallyPositive.GPALForm;
using static GenerallyPositive.Enums;

// -- Settings kept between runs --

// The same shape CallSpy writes, so a target moves between them as a file.
class HandoffTarget
{
    public string PageUrl { get; set; } = string.Empty;
    public string ApiPath { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Headers { get; set; } = string.Empty;
}

class SessionRelaySettings
{
    // One box per browser; a profile works only with its own browser. The session uses the one for the browser in the
    // combo.
    public string ChromeProfile { get; set; } = string.Empty;
    public string EdgeProfile { get; set; } = string.Empty;
    public string FirefoxProfile { get; set; } = string.Empty;

    // Where CallSpy left its last target. Asked once, then remembered.
    public string HandoffFile { get; set; } = string.Empty;
    public string PageUrl { get; set; } = string.Empty;
    public string ApiPath { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Headers { get; set; } = string.Empty;
}

// -- A target: the page that earns the session, and the API call to make with it

class Target
{
    public string Name { get; set; }
    public string PageUrl { get; set; }
    public string ApiPath { get; set; }
    public string Method { get; set; }
    public string ContentType { get; set; }
    public string Body { get; set; }
    public string Headers { get; set; }
}

// -- One run as it was made, for Show Code --

class RunRecord
{
    public string Route { get; set; }
    public string PageUrl { get; set; }
    public string ApiPath { get; set; }
    public bool Post { get; set; }
    public bool Form { get; set; }
    public string Body { get; set; }
    public List<string> Headers { get; set; }
    public BrowserType BrowserType { get; set; }
    public AutomationEngine Engine { get; set; }
    public string Profile { get; set; }
    public bool Headless { get; set; }
}

static class Program
{
    // Harrods is the known-good one: its search API is same-origin and answers both routes, so it shows the pattern
    // working before you try a protected site. Radisson is the article's target: the Munich airport hotel's room page,
    // whose rates come from an API behind Akamai Bot Manager. This is the real call the page makes, captured with
    // CallSpy: Fetch returns the nightly price, the REST handoff gets an Akamai deny page. Change the date to sweep.
    // Take the headers from the page, not from here; Transactionid is minted per session and this one is dead. The date
    // is worked out at startup so it is always one the API accepts; a hardcoded date goes stale and the answer becomes
    // about the date, not the block.
    static string CheckDate => DateTime.Now.AddDays(30).ToString("yyyy-MM-dd");

    static readonly List<Target> targets = new List<Target>
    {
        new Target
        {
            Name = "Harrods search (Algolia, same origin)",
            PageUrl = "https://www.harrods.com/",
            ApiPath = "/api/search/1/indexes/prod_harrods.com_query_suggestions/query",
            Method = "POST",
            ContentType = "Form",
            Body = @"{""query"":""mens shorts"",""hitsPerPage"":5,""page"":0,""facets"":[""*""]}",
            Headers = ""
        },
        new Target
        {
            Name = "Radisson price from (Akamai Bot Manager)",
            PageUrl = "https://www.radissonhotels.com/en-us/hotels/radisson-individuals-munich-airport/rooms",
            ApiPath = "/availability-api/multiple-price-from?hotelCodes=DEHALAAA&memberOnlyRatesHotelCodes=DEHALAAA"
                    + "&taxIncludedRegion=false&origin=price.we&searchType=lowest&adults=2&sourceOfBusiness=desktop"
                    + $"&startCheckDate={CheckDate}&rooms=1&memberType=",
            Method = "GET",
            ContentType = "Json",
            Body = "",
            Headers = string.Join(Environment.NewLine, new[]
            {
                "X-Currency: USD",
                "X-SOURCE-OF-BUSINESS: desktop",
                "Content-Type: application/json"
            })
        }
    };

    static readonly BrowserType[] browserTypes = { BrowserType.Chrome, BrowserType.Edge, BrowserType.FireFox };
    static readonly AutomationEngine[] engines = { AutomationEngine.Selenium, AutomationEngine.PuppeteerPort, AutomationEngine.PuppeteerPipe, AutomationEngine.OttoMagic };

    // The real profiles the other testers drive. Anti-bot sites deny a fresh temp profile, and only these have the
    // OttoMagic extension installed.
    static readonly Dictionary<BrowserType, string> defaultProfiles = new Dictionary<BrowserType, string>
    {
        [BrowserType.Chrome]  = @"C:\profiles\chrome\signedin",
        [BrowserType.Edge]    = @"C:\profiles\edge\signedin",
        [BrowserType.FireFox] = @"C:\profiles\firefox\signedin",
    };

    static readonly string settingsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sessionrelay-settings.yaml");

    static SessionRelaySettings settings = new SessionRelaySettings();
    static IBrowser browser;
    static IRESTClient client;

    // -- Target ---------------------------------------------------------------

    static GPALComboBox presetCombo = GPAL.ComboBox
        .WithItems(targets[0].Name, targets[1].Name)
        .WithSelectedIndex(1)
        .WithName("preset")
        .WithCallback<EventHandler>(OnPresetChosen)
        .ToGPALObject();

    static GPALInput pageUrlInput = GPAL.Input.WithName("pageUrl").ToGPALObject();
    static GPALInput apiPathInput = GPAL.Input.WithName("apiPath").ToGPALObject();

    static GPALComboBox methodCombo = GPAL.ComboBox
        .WithItems("GET", "POST")
        .WithSelectedIndex(0)
        .WithName("method")
        .ToGPALObject();

    static GPALComboBox contentTypeCombo = GPAL.ComboBox
        .WithItems("Json", "Form")
        .WithSelectedIndex(0)
        .WithName("contentType")
        .ToGPALObject();

    static GPALTextArea bodyArea = (GPALTextArea)GPAL.TextArea
        .WithHeight(60)
        .WithName("body");

    // one per line, written the way CallSpy prints them and the way .WithHeader takes them
    static GPALTextArea headerArea = (GPALTextArea)GPAL.TextArea
        .WithPlaceholder("accept: application/json")
        .WithHeight(60)
        .WithName("headers");

    // -- Browser --------------------------------------------------------------

    static GPALComboBox browserCombo = GPAL.ComboBox
        .WithItems("Chrome", "Edge", "FireFox")
        .WithSelectedIndex(0)
        .WithName("browser")
        .ToGPALObject();

    static GPALComboBox engineCombo = GPAL.ComboBox
        .WithItems("Selenium", "PuppeteerPort", "PuppeteerPipe", "OttoMagic")
        .WithSelectedIndex(0)
        .WithName("engine")
        .ToGPALObject();

    static GPALCheckbox headlessCheck = GPAL.CheckboxFor("Headless", false);

    // With nothing set, GPAL makes a temp profile and deletes it on close; it has no history, so anti-bot sites deny
    // the page. Your own browsing profile starts with the clearance real visits earned. One box per browser; the run
    // uses the one for the browser in the combo.
    static GPALFileSelector chromeProfile = (GPALFileSelector)GPAL.FileSelector
        .WithSelectFolder(true)
        .WithCompact(true)
        .WithTitle("Chrome user data directory")
        .WithPlaceholder("Empty means a temp profile, which anti-bot sites deny. Set your own profile for a clean run...")
        .WithName("chromeProfile")
        .WithToolTip("The --user-data-dir Chrome is started on. Type a path or use the button")
        .ToGPALObject();

    static GPALFileSelector edgeProfile = (GPALFileSelector)GPAL.FileSelector
        .WithSelectFolder(true)
        .WithCompact(true)
        .WithTitle("Edge user data directory")
        .WithPlaceholder("Empty means a temp profile, which anti-bot sites deny. Set your own profile for a clean run...")
        .WithName("edgeProfile")
        .WithToolTip("The --user-data-dir Edge is started on. Type a path or use the button")
        .ToGPALObject();

    static GPALFileSelector firefoxProfile = (GPALFileSelector)GPAL.FileSelector
        .WithSelectFolder(true)
        .WithCompact(true)
        .WithTitle("FireFox profile directory")
        .WithPlaceholder("Empty means a temp profile, which anti-bot sites deny. Set your own profile for a clean run...")
        .WithName("firefoxProfile")
        .WithToolTip("The FireFox profile directory, the one under Profiles. Type a path or use the button")
        .ToGPALObject();

    // -- Tabs -----------------------------------------------------------------

    static GPALTab howToTab = GPAL.Tab.WithName("howToTab").WithText("How To").ToGPALObject();

    // The steps, so the bot explains itself.
    static readonly string[] instructions =
    {
        "SessionRelay earns a session in a browser, then calls the site's own API with it two",
        "ways, so you can see which check you are hitting.",
        "",
        "1.  Pick a target, or fill in the page url and API path yourself. CallSpy is where the",
        "    API path comes from: Copy Path there, paste here.",
        "",
        "2.  Check the profile directory for the browser you picked. The three boxes start on the",
        "    profiles these bots are normally driven on, one per browser, so there is usually nothing",
        "    to type. Without one an anti-bot site denies the page.",
        "",
        "3.  Engine: PuppeteerPort or PuppeteerPipe if you want Take Headers From Page. Fetch and REST",
        "    Handoff work on every engine; capture does not.",
        "",
        "4.  Press Start Session. The browser loads the page and earns whatever the site issues.",
        "",
        "5.  Press Take Headers From Page BEFORE Run Fetch. This matters on Radisson and on any",
        "    site like it: the API wants a Transactionid header that the page's own JavaScript",
        "    mints per session. It is different on every load, it is not a cookie, and the one",
        "    saved in the preset is already dead. This button reads the live one out of the run.",
        "",
        "6.  Run Fetch issues the request from inside the page, so it carries the browser's TLS",
        "    fingerprint, HTTP/2 framing and header order as well as the cookies.",
        "",
        "7.  Run REST Handoff makes the same call with no browser in the path, carrying cookies,",
        "    the live user agent and accept-language.",
        "",
        "8.  Compare them on the Runs tab. Both through means the wall was cookies and the fast",
        "    route is available. Fetch through and REST blocked means the wall is the transport,",
        "    which is what Akamai does. Both blocked means it is the IP or the page never settled.",
        "",
        "9.  Close Browser leaves the REST client alive, which is the point of the handoff.",
        "",
        "10. Run Fetch first. It can never spoil the session. Run REST Handoff second: if the",
        "    site refuses it, the session can be spoiled for the browser as well and Fetch will",
        "    start failing too. Press Start Session again: it reopens the browser and loads the",
        "    page, which makes the site hand out new clearance.",
        "",
        "11. Try each route twice, a few minutes apart. A handoff often works right after you",
        "    use the page and stops working later, so one try can tell you the wrong thing.",
        "",
        "",
        "This bot is the bench: it sends a request you can edit. Change the body, vary a",
        "parameter, sweep a date, try an endpoint the page never called, or run the same",
        "call across Chrome, Edge and Firefox on Selenium, Puppeteer or OttoMagic.",
        "",
        "To find out what a site calls in the first place, and to reissue a call exactly as the",
        "page sent it with its live headers, use CallSpy. Copy Path there, paste here, and this",
        "is where you start changing it.",
        "",
        "The GPAL Log tab carries everything the library publishes while a run happens."
    };

    static GPALTextArea instructionArea = (GPALTextArea)GPAL.TextArea
        .WithReadOnly(true)
        //.WithHeight(500)
        .WithName("instructions");

    static GPALTab sessionTab = GPAL.Tab.WithName("sessionTab").WithText("Session").ToGPALObject();
    static GPALTab runsTab = GPAL.Tab.WithName("runsTab").WithText("Runs").ToGPALObject();
    static GPALTab logTab = GPAL.Tab.WithName("logTab").WithText("GPAL Log").ToGPALObject();

    // Every event GPAL publishes.
    static GPALTextArea logArea = (GPALTextArea)GPAL.TextArea
        .WithReadOnly(true)
        //.WithHeight(460)
        .WithName("log");

    // Building an event captures a stack, and a handler gets everything unless told otherwise, so DEBUG is requested at
    // the source, not filtered on arrival. DEEPDEBUG is never requested: thousands of events a run belong in a log
    // file, not a watched tab.
    static GPALCheckbox debugCheck = (GPALCheckbox)GPAL.Checkbox
        .WithChecked(true)
        .WithName("debug")
        .WithText("Show DEBUG")
        .WithToolTip("Off: the readable log. On: adds GPAL's DEBUG tracing, for when a call does something you cannot explain.")
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

    // -- Actions --------------------------------------------------------------

    static GPALButton loadHandoffBtn = GPAL.Button
        .WithName("loadHandoff")
        .WithText("📥 Load From CallSpy")
        .WithCallback<EventHandler>(OnLoadFromCallSpy)
        .ToGPALObject();

    static GPALButton startBtn = GPAL.Button
        .WithName("start")
        .WithText("👁 Start Session")
        .WithCallback<EventHandler>((s, e) => startBtn.Enabled = false)
        .WithCallbackOffUIThread<EventHandler>(OnStartSession)
        .ToGPALObject();

    static GPALButton fetchBtn = GPAL.Button
        .WithEnabled(false)
        .WithName("fetch")
        .WithText("⚡ Run Fetch")
        .WithCallback<EventHandler>((s, e) => fetchBtn.Enabled = false)
        .WithCallbackOffUIThread<EventHandler>(OnFetch)
        .ToGPALObject();

    static GPALButton restBtn = GPAL.Button
        .WithEnabled(false)
        .WithName("rest")
        .WithText("🤝 Run REST Handoff")
        .WithCallback<EventHandler>((s, e) => restBtn.Enabled = false)
        .WithCallbackOffUIThread<EventHandler>(OnRestHandoff)
        .ToGPALObject();

    static GPALButton takeHeadersBtn = GPAL.Button
        .WithEnabled(false)
        .WithName("takeHeaders")
        .WithText("🏷 Take Headers From Page")
        .WithCallback<EventHandler>((s, e) => takeHeadersBtn.Enabled = false)
        .WithCallbackOffUIThread<EventHandler>(OnTakeHeaders)
        .ToGPALObject();

    static GPALButton closeBtn = GPAL.Button
        .WithEnabled(false)
        .WithName("close")
        .WithText("🛑 Close Browser")
        .WithCallback<EventHandler>((s, e) => closeBtn.Enabled = false)
        .WithCallbackOffUIThread<EventHandler>(OnCloseBrowser)
        .ToGPALObject();

    static GPALButton aboutBtn = GPAL.Button
        .WithName("about")
        .WithText("ℹ About")
        .WithCallback<EventHandler>(OnAbout)
        .ToGPALObject();

    static GPALButton showCodeBtn = GPAL.Button
        .WithName("showCode")
        .WithText("💻 Show Code")
        .WithToolTip("The GPAL workflow that makes the selected run's call, or the last run's, to use in a program of your own")
        .WithCallback<EventHandler>(OnShowCode)
        .ToGPALObject();

    // -- Show Code ------------------------------------------------------------

    static readonly string codeFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "code");

    static GPALLabel codeHelp = GPAL.LabelFor(
        "A console program that earns the session and makes this run's call the same way. Add the GPAL package\n" +
        "to it; the answer is written to answer.txt in the folder it runs in.");

    static GPALTextArea codeArea = (GPALTextArea)GPAL.TextArea
        .WithReadOnly(true)
        .WithHeight(470)
        .WithName("code");

    static GPALButton copyCodeBtn = GPAL.Button
        .WithName("copyCode")
        .WithText("📋 Copy")
        .WithCallback<EventHandler>(OnCopyCode)
        .ToGPALObject();

    static GPALButton saveCodeBtn = GPAL.Button
        .WithName("saveCode")
        .WithText("💾 Save")
        .WithToolTip("Writes the code to a .cs file in the code folder beside SessionRelay")
        .WithCallback<EventHandler>(OnSaveCode)
        .ToGPALObject();

    // the default button closes the dialog
    static GPALButton closeCodeBtn = GPAL.Button
        .Default
        .WithName("closeCode")
        .WithText("Close")
        .ToGPALObject();

    // the browser the session was started with, for the runs made against it
    static BrowserType sessionBrowserType;
    static AutomationEngine sessionEngine;
    static string sessionProfile = string.Empty;
    static bool sessionHeadless;
    static string sessionPageUrl = string.Empty;

    // every run as it was made, in step with the rows of runList
    static List<RunRecord> runRecords = new List<RunRecord>();

    // the route of the code in the dialog, for the file name Save gives it
    static string codeRoute = string.Empty;

    // -- About ----------------------------------------------------------------

    static GPALLabel aboutText = (GPALLabel)GPAL.LabelFor(
        "SessionRelay, a GPAL Bot of the Week.\n\n" +
        "Earn a session in a browser, then call the site's\n" +
        "own API with it, by Fetch and by REST handoff.\n\n" +
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

    static GPALListView runList = GPAL.ListView
        .WithColumns("Route", "Method", "Path", "Status", "Bytes", "Seconds")
        .WithColumnWidth(100, 60, 300, 70, 70, -2)
        .WithView(ListViewMode.Details)
        .WithFullRowSelect(true)
        .WithHeight(120)
        .WithName("runs")
        .WithCallback<EventHandler>(OnRunSelected)
        .ToGPALObject();

    static GPALTextArea responseArea = (GPALTextArea)GPAL.TextArea
        .WithReadOnly(true)
        //.WithHeight(260)
        .WithName("response");

    static GPALStatusStrip statusBar = GPAL.StatusStrip
        .WithText("Pick a target, then Start Session.")
        .WithName("status")
        .ToGPALObject();

    // every run's body, in step with the rows of runList
    static List<string> bodies = new List<string>();

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
            .WithTitle("SessionRelay  |  A GPAL Bot of the Week")
            .WithTop(200)
            .WithWidth(860)
            .WithHeight(785)

            .WithFormControl(sessionTab)
            .WithFormControl(GPAL.LabelFor("Target:"))
            .WithFormControl(presetCombo)
            .WithFormControl(GPAL.LabelFor("Page that earns the session:"))
            .WithFormControl(pageUrlInput)
            .WithFormControl(GPAL.LabelFor("API path, relative to that page's origin:"))
            .WithFormControl(apiPathInput)
            .WithFormControl(GPAL.LabelFor("Method and content type:"))
            .WithFormControl(methodCombo)
            .WithFormControl(contentTypeCombo)
            .WithFormControl(GPAL.LabelFor("Body, for a POST:"))
            .WithFormControl(bodyArea)
            .WithFormControl(GPAL.LabelFor("Headers the page sends, one per line, from CallSpy:"))
            .WithFormControl(headerArea)
            .WithFormControl(GPAL.LabelFor("Browser and engine:"))
            .WithFormControl(browserCombo)
            .WithFormControl(engineCombo)
            .WithFormControl(headlessCheck)
            .WithFormControl(GPAL.LabelFor("Profile folders, one per browser. The session runs on the one for the browser above; empty means a temp profile with no cookies:"))
            .WithFormControl(chromeProfile)
            .WithFormControl(edgeProfile)
            .WithFormControl(firefoxProfile)
            .WithFormControl(loadHandoffBtn)
            .WithFormControl(startBtn)
            .WithFormControl(takeHeadersBtn)
            .WithFormControl(closeBtn)
            .WithFormControl(aboutBtn)

            .WithFormControl(runsTab)
            .WithFormControl(fetchBtn)
            .WithFormControl(restBtn)
            .WithFormControl(showCodeBtn)
            .WithFormControl(GPAL.LabelFor("Runs against this session:"))
            .WithFormControl(runList)
            .WithFormControl(GPAL.LabelFor("Response:"))
            .WithFormControl(responseArea)

            .WithFormControl(logTab)
            .WithFormControl(debugCheck)
            .WithFormControl(clearLogBtn)
            .WithFormControl(saveLogBtn)
            .WithFormControl(logArea)

            .WithFormControl(howToTab)
            .WithFormControl(instructionArea)

            .WithFormControl(statusBar);

        FillFrom(targets[1]);
        LoadSettings();

        // The compact selector starts empty, so paths are set after the form is built: last run's, or on first open
        // that browser's usual profile.
        chromeProfile.SetPath(false == string.IsNullOrEmpty(settings.ChromeProfile) ? settings.ChromeProfile : defaultProfiles[BrowserType.Chrome]);
        edgeProfile.SetPath(false == string.IsNullOrEmpty(settings.EdgeProfile) ? settings.EdgeProfile : defaultProfiles[BrowserType.Edge]);
        firefoxProfile.SetPath(false == string.IsNullOrEmpty(settings.FirefoxProfile) ? settings.FirefoxProfile : defaultProfiles[BrowserType.FireFox]);

        instructionArea.Text = string.Join(Environment.NewLine, instructions);

        form.ShowDialog();

        browser?.Close();
    }

    // -- Target ---------------------------------------------------------------

    static void OnPresetChosen(object s, EventArgs e)
    {
        int index = presetCombo.SelectedIndex;

        if (0 > index || index >= targets.Count) return;

        FillFrom(targets[index]);
    }

    static void FillFrom(Target target)
    {
        pageUrlInput.Text = target.PageUrl;
        apiPathInput.Text = target.ApiPath;
        methodCombo.SelectedIndex = "POST".Equals(target.Method) ? 1 : 0;
        contentTypeCombo.SelectedIndex = "Form".Equals(target.ContentType) ? 1 : 0;
        bodyArea.Text = target.Body;
        headerArea.Text = AsLines(target.Headers);

        SetStatus($"Target set to [{target.Name}].");
    }

    // -- What carries over between runs ---------------------------------------

    static void LoadSettings()
    {
        if (false == File.Exists(settingsFile)) return;

        GPAL.Converter
            .WithInput((GPALFile)settingsFile)
            .SaveTo(ref settings);

        // A CallSpy endpoint took effort to find, so it overrides the preset loaded above. The preset name is what you
        // last picked; the boxes are what you last ran.
        if (false == string.IsNullOrEmpty(settings.PageUrl))
        {
            pageUrlInput.Text = settings.PageUrl;
            apiPathInput.Text = settings.ApiPath;
            methodCombo.SelectedIndex = "POST".Equals(settings.Method) ? 1 : 0;
            contentTypeCombo.SelectedIndex = "Form".Equals(settings.ContentType) ? 1 : 0;
            // YAML keeps line breaks as bare newlines, which a textbox shows as one line, so they are converted for the
            // control.
            bodyArea.Text = AsLines(settings.Body);
            headerArea.Text = AsLines(settings.Headers);

            SetStatus("Target restored from the last run. Pick a preset to start over.");
        }
    }

    // The box for the browser about to start. Chrome's user data folder given to Firefox means Firefox will not open.
    static string ProfileFor(BrowserType browserType)
    {
        string profile = BrowserType.Edge == browserType ? edgeProfile.SelectedPath
                       : BrowserType.FireFox == browserType ? firefoxProfile.SelectedPath
                       : chromeProfile.SelectedPath;

        return profile?.Trim() ?? string.Empty;
    }

    static string AsLines(string text)
    {
        return string.Join(Environment.NewLine, (text ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None));
    }

    static void SaveSettings()
    {
        settings.ChromeProfile = chromeProfile.SelectedPath?.Trim() ?? string.Empty;
        settings.EdgeProfile = edgeProfile.SelectedPath?.Trim() ?? string.Empty;
        settings.FirefoxProfile = firefoxProfile.SelectedPath?.Trim() ?? string.Empty;
        settings.PageUrl = pageUrlInput.Text?.Trim() ?? string.Empty;
        settings.ApiPath = apiPathInput.Text?.Trim() ?? string.Empty;
        settings.Method = 1 == methodCombo.SelectedIndex ? "POST" : "GET";
        settings.ContentType = 1 == contentTypeCombo.SelectedIndex ? "Form" : "Json";
        settings.Body = bodyArea.Text ?? string.Empty;
        settings.Headers = headerArea.Text ?? string.Empty;

        GPAL.Converter
            .WithInput(settings)
            .SaveTo((GPALFile)settingsFile);
    }

    // CallSpy writes callspy-target.yaml beside its exe. The first press asks where; later presses reuse it, so a call
    // found there is one button from running here.
    static void OnLoadFromCallSpy(object s, EventArgs e)
    {
        string path = settings.HandoffFile;

        if (true == string.IsNullOrEmpty(path) || false == File.Exists(path))
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "Where does CallSpy write its target?";
                dialog.Filter = "CallSpy target (callspy-target.yaml)|callspy-target.yaml|YAML Files (*.yaml)|*.yaml";
                dialog.FileName = "callspy-target.yaml";

                if (DialogResult.OK != dialog.ShowDialog()) return;

                path = dialog.FileName;
            }
        }

        HandoffTarget target = new HandoffTarget();

        GPAL.Converter
            .WithInput((GPALFile)path)
            .SaveTo(ref target);

        if (true == string.IsNullOrEmpty(target.ApiPath))
        {
            SetStatus($"Nothing to load from [{path}]. Press Send to SessionRelay in CallSpy first.");
            return;
        }

        pageUrlInput.Text = target.PageUrl;
        apiPathInput.Text = target.ApiPath;
        methodCombo.SelectedIndex = "POST".Equals(target.Method) ? 1 : 0;
        contentTypeCombo.SelectedIndex = "Form".Equals(target.ContentType) ? 1 : 0;
        bodyArea.Text = AsLines(target.Body);
        headerArea.Text = AsLines(target.Headers);

        settings.HandoffFile = path;
        SaveSettings();

        SetStatus($"Loaded [{target.Method}] [{target.ApiPath}] from CallSpy.");
    }

    // -- Session --------------------------------------------------------------

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

    static void OnStartSession(object s, EventArgs e)
    {
        try
        {
            // Attached off the UI thread, so the form keeps painting and the Log tab fills in while the browser works.
            try
            {
                if (string.IsNullOrWhiteSpace(pageUrlInput.Text))
                {
                    SetStatus("Give it a page to load first.");
                    return;
                }

                browser?.Close();

                SaveSettings();

                BrowserType browserType = browserTypes[Math.Max(0, browserCombo.SelectedIndex)];
                AutomationEngine engine = engines[Math.Max(0, engineCombo.SelectedIndex)];

                SetStatus($"Starting [{browserType}] on [{engine}]...");

                browser = GPAL.Browser
                    .WithBrowserType(browserType)
                    .WithAutomationEngine(engine)
                    .WithUseStealth(StealthType.PatchDriver)
                    .WithWaitOnDocumentReady(30_000)      // the session is only earned once the page settles
                    .ToGPALObject();

                string profile = ProfileFor(browserType);

                if (false == string.IsNullOrEmpty(profile))
                    browser.WithProfileDataDirectory(profile);

                sessionBrowserType = browserType;
                sessionEngine = engine;
                sessionProfile = profile;
                sessionHeadless = true == headlessCheck.Checked;
                sessionPageUrl = pageUrlInput.Text.Trim();

                // Said before the run, not after it finds nothing. Selenium records inside the page, so it has the
                // calls but not the headers the browser adds on the way out.
                if (AutomationEngine.PuppeteerPort != engine && AutomationEngine.PuppeteerPipe != engine && AutomationEngine.OttoMagic != engine)
                    GPAL.PublishSimpleEvent(GPALEventType.WARNING, $"Take Headers From Page needs Puppeteer or OttoMagic, and this session is on [{engine}]", null, GPALObjectType.Browser);

                // Headless uses .Get, the same navigation without a window.
                VisitWithoutVerdicts(browser, browserType, pageUrlInput.Text.Trim(), true == headlessCheck.Checked);

                // Record only once the browser is up and has navigated. The page keeps calling its API, and Take
                // Headers reads the latest call.
                browser.CaptureCalls();

                fetchBtn.Enabled = true;
                restBtn.Enabled = true;
                closeBtn.Enabled = true;
                takeHeadersBtn.Enabled = true == browser.UsePuppeteer || true == browser.UseOttoMagic;

                // A deny page is a result, not a dead end. Anti-bots judge the navigation hardest and judge the page's
                // own requests separately, so run both routes and read the status codes, not the page.
                SetStatus($"Loaded [{pageUrlInput.Text.Trim()}]. If it shows Access Denied, run the routes anyway and see what they say.");
            }
            catch (Exception ex)
            {
                GPAL.PublishSimpleEvent(GPALEventType.EXCEPTION, $"[OnStartSession] did not finish", null, GPALObjectType.GPALForm, ex);
                SetStatus($"[{ex.GetType().Name}] {ex.Message}");
            }
        }
        finally
        {
            // the click turned the button off; it comes back as the program's state has it
            startBtn.Enabled = true;
        }
    }

    static void OnCloseBrowser(object s, EventArgs e)
    {
        try
        {
            // Attached off the UI thread, so the form keeps painting and the Log tab fills in while the browser works.
            try
            {
                browser?.Close();
                browser = null;

                fetchBtn.Enabled = false;
                closeBtn.Enabled = false;

                // The handed-off client outlives the browser; that is its point.
                restBtn.Enabled = null != client;

                SetStatus(null != client
                    ? "Browser closed. The REST client still has the session."
                    : "Browser closed.");
            }
            catch (Exception ex)
            {
                GPAL.PublishSimpleEvent(GPALEventType.EXCEPTION, $"[OnCloseBrowser] did not finish", null, GPALObjectType.GPALForm, ex);
                SetStatus($"[{ex.GetType().Name}] {ex.Message}");
            }
        }
        finally
        {
            // the click turned the button off; it comes back as the program's state has it
            closeBtn.Enabled = null != browser;
        }
    }

    // -- The headers the page itself used -------------------------------------

    // The browser sets these itself and fetch refuses overrides, so copying them only clutters the box.
    static readonly string[] browserOwned =
    {
        "referer", "user-agent", "host", "origin", "cookie", "content-length",
        "connection", "accept-encoding", "sec-ch-ua", "sec-ch-ua-mobile", "sec-ch-ua-platform",
        "sec-fetch-dest", "sec-fetch-mode", "sec-fetch-site"
    };

    static void OnTakeHeaders(object s, EventArgs e)
    {
        try
        {
            // Attached off the UI thread, so the form keeps painting and the Log tab fills in while the browser works.
            try
            {
                if (null == browser) return;

                browser.CaptureCalls(out List<GPALCall> calls);

                string wanted = apiPathInput.Text?.Trim() ?? string.Empty;
                int query = wanted.IndexOf('?');

                if (0 < query)
                    wanted = wanted.Substring(0, query);

                GPALCall found = null;

                // The last one wins; a page calling an endpoint repeatedly has settled by the last.
                foreach (GPALCall call in calls)
                    if (false == string.IsNullOrEmpty(wanted) && true == call.Url?.Contains(wanted))
                        found = call;

                if (null == found)
                {
                    // selenium records inside the page, so a call the page did not issue itself is not there
                    if (true == browser.UseSelenium)
                        SetStatus($"Captured [{calls.Count}] calls and none was to [{wanted}]. Selenium only sees what page script issued, so try Puppeteer or OttoMagic.");
                    else
                        SetStatus($"Captured [{calls.Count}] calls and none was to [{wanted}]. Check the path, or drive the page until it makes that call.");

                    return;
                }

                List<string> lines = new List<string>();

                foreach (KeyValuePair<string, string> header in found.Headers)
                    if (false == Array.Exists(browserOwned, owned => owned.Equals(header.Key, StringComparison.OrdinalIgnoreCase)))
                        lines.Add($"{header.Key}: {header.Value}");

                headerArea.Text = string.Join(Environment.NewLine, lines);

                SaveSettings();

                SetStatus($"Took [{lines.Count}] headers from the page's own [{found.Status}] call. Minted this session, not pasted.");
            }
            catch (Exception ex)
            {
                GPAL.PublishSimpleEvent(GPALEventType.EXCEPTION, $"[OnTakeHeaders] did not finish", null, GPALObjectType.GPALForm, ex);
                SetStatus($"[{ex.GetType().Name}] {ex.Message}");
            }
        }
        finally
        {
            // the click turned the button off; it comes back as the program's state has it
            IBrowser open = browser;
            takeHeadersBtn.Enabled = null != open && (true == open.UsePuppeteer || true == open.UseOttoMagic);
        }
    }

    // -- Route one: the page makes the call -----------------------------------

    static void OnFetch(object s, EventArgs e)
    {
        try
        {
            // Attached off the UI thread, so the form keeps painting and the Log tab fills in while the browser works.
            try
            {
                if (null == browser)
                {
                    SetStatus("No browser. Start a session first.");
                    return;
                }

                GPALRequest request = (GPALRequest)GPAL.Request
                    .WithPath(apiPathInput.Text.Trim())
                    .WithHttpMethod(1 == methodCombo.SelectedIndex ? HttpVerb.Post : HttpVerb.Get)
                    .WithContentType(1 == contentTypeCombo.SelectedIndex ? ContentType.Form : ContentType.Json)
                    .WithBody(bodyArea.Text)
                    .WithName("session relay");

                // The browser sends its own headers regardless. These go on top, which is where headers the page's code
                // adds must come from.
                foreach (string header in HeaderLines())
                    request.WithHeader(header);

                string json = null;
                Stopwatch stopwatch = Stopwatch.StartNew();

                browser
                    .Fetch(request)
                    .SaveTo(ref json);

                stopwatch.Stop();

                // Fetch hands back a json array of bodies, one per page requested, so a single call is a one-item array
                AddRun("Fetch", json, "in page", stopwatch.Elapsed);

                // What was actually run, usually pasted in after the session started.
                SaveSettings();
            }
            catch (Exception ex)
            {
                GPAL.PublishSimpleEvent(GPALEventType.EXCEPTION, $"[OnFetch] did not finish", null, GPALObjectType.GPALForm, ex);
                SetStatus($"[{ex.GetType().Name}] {ex.Message}");
            }
        }
        finally
        {
            // the click turned the button off; it comes back as the program's state has it
            fetchBtn.Enabled = null != browser;
        }
    }

    // -- Route two: the session leaves the browser ----------------------------

    static void OnRestHandoff(object s, EventArgs e)
    {
        try
        {
            // Attached off the UI thread, so the form keeps painting and the Log tab fills in while the browser works.
            try
            {
                Stopwatch stopwatch = Stopwatch.StartNew();

                // Taken once from a browser that has navigated. The client then carries the cookies, live user agent
                // and accept-language the page was sent with.
                if (null != browser)
                    browser.ContinueAsRESTClient(out client);

                if (null == client)
                {
                    SetStatus("No session to carry. Start a session first.");
                    return;
                }

                // A site that fingerprints the transport accepts the connection and never answers, so the default
                // minute is a minute wasted.
                client.WithTimeout(TimeSpan.FromSeconds(15));

                foreach (string header in HeaderLines())
                {
                    int colon = header.IndexOf(':');

                    if (0 < colon)
                        client.WithHeader(header.Substring(0, colon).Trim(), header.Substring(colon + 1).Trim());
                }

                string body = 1 == methodCombo.SelectedIndex
                    ? client.WithEndpoint(apiPathInput.Text.Trim()).WithParameters(bodyArea.Text).WithHttpMethod("POST").Execute(false)
                    : client.WithEndpoint(apiPathInput.Text.Trim()).WithHttpMethod("GET").Execute(false);

                stopwatch.Stop();

                // the status code is the interesting part on an anti-bot target: 200 is through, 403 is the wall
                AddRun("REST handoff", body, ((RESTClient)client).StatusCode.ToString(), stopwatch.Elapsed);

                SaveSettings();
            }
            catch (Exception ex)
            {
                GPAL.PublishSimpleEvent(GPALEventType.EXCEPTION, $"[OnRestHandoff] did not finish", null, GPALObjectType.GPALForm, ex);
                SetStatus($"[{ex.GetType().Name}] {ex.Message}");
            }
        }
        finally
        {
            // the click turned the button off; it comes back as the program's state has it
            restBtn.Enabled = null != browser || null != client;
        }
    }

    // Blank lines and lines without a colon are skipped, so a pasted block can be trimmed by deleting lines.
    static List<string> HeaderLines()
    {
        List<string> retVal = new List<string>();

        foreach (string line in (headerArea.Text ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            if (false == string.IsNullOrWhiteSpace(line) && 0 < line.IndexOf(':'))
                retVal.Add(line.Trim());

        return retVal;
    }

    // -- Results --------------------------------------------------------------

    static void AddRun(string route, string body, string status, TimeSpan elapsed)
    {
        // The method and path as sent: a wrong one looks exactly like a block, and this list tells them apart.
        string method = 1 == methodCombo.SelectedIndex ? "POST" : "GET";

        bodies.Add(body ?? string.Empty);

        runRecords.Add(new RunRecord
        {
            Route = route,
            PageUrl = sessionPageUrl,
            ApiPath = apiPathInput.Text?.Trim() ?? string.Empty,
            Post = 1 == methodCombo.SelectedIndex,
            Form = 1 == contentTypeCombo.SelectedIndex,
            Body = bodyArea.Text ?? string.Empty,
            Headers = HeaderLines(),
            BrowserType = sessionBrowserType,
            Engine = sessionEngine,
            Profile = sessionProfile,
            Headless = sessionHeadless
        });

        runList.AddItem(route, method, apiPathInput.Text?.Trim(), status, (body?.Length ?? 0).ToString(), elapsed.TotalSeconds.ToString("0.00"));

        responseArea.Text = Shortened(body);

        SetStatus($"[{route}] returned [{body?.Length ?? 0}] characters in [{elapsed.TotalSeconds:0.00}] seconds.");
    }

    static void OnRunSelected(object s, EventArgs e)
    {
        int index = runList.SelectedIndex;

        if (0 > index || index >= bodies.Count) return;

        responseArea.Text = Shortened(bodies[index]);
    }

    // A rates response runs to hundreds of kilobytes; nobody reads that in a textarea.
    static string Shortened(string body)
    {
        string retVal = body ?? string.Empty;

        if (4000 < retVal.Length)
            retVal = retVal.Substring(0, 4000) + $"{Environment.NewLine}...[{retVal.Length} characters in all]";

        return retVal;
    }

    // -- Show Code ------------------------------------------------------------

    // The selected run, or the last one, as a console program that earns the session as Start Session does and makes
    // the call the way that run's route made it, shown in a dialog to copy or save
    static void OnShowCode(object s, EventArgs e)
    {
        int index = 0 <= runList.SelectedIndex && runList.SelectedIndex < runRecords.Count ? runList.SelectedIndex : runRecords.Count - 1;

        if (0 <= index)
        {
            RunRecord run = runRecords[index];
            bool fetch = "Fetch" == run.Route;
            System.Text.StringBuilder code = new System.Text.StringBuilder();
            string indent = "        ";

            // a character that ends a line in C#, or any other control character, which neither a string nor a
            // comment may hold as it is
            bool Breaks(char c) => ' ' > c || (char)0x85 == c || (char)0x2028 == c || (char)0x2029 == c;

            // A value as a C# string. Verbatim when it holds a backslash, a quote or line breaks, so a path or a json body
            // of several lines reads as typed; a verbatim string may hold line breaks and tabs. Escaped character by
            // character when it holds any other character that would end a line or cannot be seen
            string Quoted(string value) => true == value.Any(c => true == Breaks(c) && '\r' != c && '\n' != c && '\t' != c)
                ? "\"" + string.Concat(value.Select(c => true == Breaks(c) || '\\' == c || '"' == c ? $"\\u{(int)c:X4}" : c.ToString())) + "\""
                : 0 <= value.IndexOfAny(new[] { '\\', '"', '\r', '\n', '\t' })
                    ? "@\"" + value.Replace("\"", "\"\"") + "\""
                    : "\"" + value + "\"";

            // a value inside a // comment, with any character that would end the comment made a space
            string Commented(string value) => string.Concat(value.Select(c => true == Breaks(c) ? " " : c.ToString()));

            void Line(string text) => code.AppendLine(0 == text.Length ? string.Empty : indent + text);

            code.AppendLine($"// {(run.Post ? "POST" : "GET")} {Commented(run.ApiPath)}");
            code.AppendLine($"// on a session earned at {Commented(run.PageUrl)}, by the {(fetch ? "in-page Fetch" : "REST handoff")} route");
            code.AppendLine("// written by SessionRelay, a GPAL Bot of the Week: https://gpal.sditx.com/docs/botw/botw-sessionrelay");
            code.AppendLine();
            code.AppendLine("using System;");
            code.AppendLine("using System.IO;");
            code.AppendLine("using GenerallyPositive;");
            code.AppendLine("using GenerallyPositive.Browser;");
            code.AppendLine("using static GenerallyPositive.Enums;");
            code.AppendLine();
            code.AppendLine("static class Program");
            code.AppendLine("{");
            code.AppendLine("    static void Main()");
            code.AppendLine("    {");
            Line("IBrowser browser = GPAL.Browser");
            Line($"    .WithBrowserType(BrowserType.{run.BrowserType})");
            Line($"    .WithAutomationEngine(AutomationEngine.{run.Engine})");
            Line("    .WithUseStealth(StealthType.PatchDriver)");
            Line("    .WithWaitOnDocumentReady(30_000)      // the session is only earned once the page settles");
            Line("    .ToGPALObject();");

            if (0 < run.Profile.Length)
            {
                Line(string.Empty);
                Line("// a used profile loads like a returning visitor; a fresh one is what anti-bot systems look for");
                Line($"browser.WithProfileDataDirectory({Quoted(run.Profile)});");
            }

            Line(string.Empty);
            Line(true == run.Headless ? "// the page that earns the session, opened with no window" : "// the page that earns the session");
            Line($"browser.{(true == run.Headless ? "Get" : "GoTo")}({Quoted(run.PageUrl)});");
            Line(string.Empty);

            if (true == fetch)
            {
                Line("// the page makes the call, over the browser's own connection, so it carries the cookies, the TLS");
                Line("// fingerprint and the header order");
                Line("GPALRequest request = (GPALRequest)GPAL.Request");
                Line($"    .WithPath({Quoted(run.ApiPath)})");
                Line($"    .WithHttpMethod(HttpVerb.{(run.Post ? "Post" : "Get")})");
                Line($"    .WithContentType(ContentType.{(run.Form ? "Form" : "Json")}){(0 == run.Body.Length ? ";" : string.Empty)}");
                if (0 < run.Body.Length)
                    Line($"    .WithBody({Quoted(run.Body)});");

                if (0 < run.Headers.Count)
                {
                    Line(string.Empty);
                    Line("// the headers the page sends; the browser adds its own beneath them");
                    foreach (string header in run.Headers)
                        Line($"request.WithHeader({Quoted(header)});");
                }

                Line(string.Empty);
                Line("string json = null;");
                Line(string.Empty);
                Line("browser");
                Line("    .Fetch(request)");
                Line("    .SaveTo(ref json);");
                Line(string.Empty);
                Line("// Fetch hands back a json array of bodies, one per page requested");
                Line("File.WriteAllText(\"answer.txt\", json ?? string.Empty);");
            }
            else
            {
                Line("// the session leaves the browser: its cookies, user agent and accept-language go to a REST client");
                Line("browser.ContinueAsRESTClient(out IRESTClient client);");
                Line(string.Empty);
                Line("// a site that fingerprints the transport takes the connection and never answers");
                Line("client.WithTimeout(TimeSpan.FromSeconds(15));");

                if (0 < run.Headers.Count)
                {
                    Line(string.Empty);
                    Line("// the headers the page sends");
                    foreach (string header in run.Headers)
                    {
                        int colon = header.IndexOf(':');
                        Line($"client.WithHeader({Quoted(header.Substring(0, colon).Trim())}, {Quoted(header.Substring(colon + 1).Trim())});");
                    }
                }

                Line(string.Empty);
                Line("string body = client");
                Line($"    .WithEndpoint({Quoted(run.ApiPath)})");
                if (true == run.Post)
                    Line($"    .WithParameters({Quoted(run.Body)})");
                Line($"    .WithHttpMethod(\"{(run.Post ? "POST" : "GET")}\")");
                Line("    .Execute(false);");
                Line(string.Empty);
                Line("// the status code first: on a protected site 200 is through and 403 is the wall");
                Line("File.WriteAllText(\"answer.txt\", ((RESTClient)client).StatusCode + Environment.NewLine + body);");
            }

            Line(string.Empty);
            Line("browser.Close();");
            code.AppendLine("    }");
            code.AppendLine("}");

            codeArea.Text = code.ToString();
            codeRoute = fetch ? "fetch" : "rest-handoff";

            GPAL.Form
                .WithTitle($"Show Code: {run.Route}, {(run.Post ? "POST" : "GET")} {run.ApiPath}")
                .WithWidth(760)
                .WithFormControl(codeHelp)
                .WithFormControl(codeArea)
                .WithFormControl(copyCodeBtn)
                .WithFormControl(saveCodeBtn)
                .WithFormControl(closeCodeBtn)
                .ShowDialog();
        }
        else
            SetStatus("Run Fetch or the REST handoff first; Show Code writes the call a run made.");
    }

    static void OnCopyCode(object s, EventArgs e)
    {
        GPAL.Clipboard
            .WithInput(codeArea.Text)
            .SetClipboard();

        SetStatus("The code is on the clipboard.");
    }

    static void OnSaveCode(object s, EventArgs e)
    {
        string path = Path.Combine(codeFolder, $"sessionrelay-{codeRoute}.cs");

        Directory.CreateDirectory(codeFolder);
        File.WriteAllText(path, codeArea.Text);

        SetStatus($"The code is saved to [{path}].");
    }

    static void OnAbout(object s, EventArgs e)
    {
        GPAL.Form
            .WithTitle("About SessionRelay")
            .WithWidth(420)
            .WithFormControl(aboutText)
            .WithFormControl(GPAL.SpacerFor(12))
            .WithFormControl(moreBotsLink)
            .WithFormControl(GPAL.SpacerFor(12))
            .WithFormControl(aboutOkBtn)
            .ShowDialog();
    }

    // both GPAL channels land here. AppendLine marshals to the ui thread itself and keeps the last 2000 lines,
    // so a long run does not grow without end
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

    // DEBUG is requested at the source, so an unchecked box costs nothing instead of a stack capture per event thrown
    // away. DEEPDEBUG is in neither mask.
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
