// =============================================================================
// GPAL Bot of the Week - CastManager
//
// A GPALForm that manages a saved list of favorites (websites, local files,
// videos, movies) and casts the selected one to a named Cast device. Supports
// tab and desktop casting. Config is persisted as YAML via GPAL.Converter.
// The first entry is preloaded in the browser on startup for instant casting.
// =============================================================================

using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using GenerallyPositive;
using GenerallyPositive.Browser;
using GenerallyPositive.GPALForm;
using static GenerallyPositive.Enums;

// ── Config model (serialized to/from YAML by GPAL.Converter) ─────────────────

class CastEntry
{
    public string Title { get; set; } = string.Empty;
    public string Url   { get; set; } = string.Empty;
}

class CastConfig
{
    public string         DeviceName { get; set; } = "Living Room TV";
    public string         CastMode   { get; set; } = "Tab";
    public string         Engine     { get; set; } = "PuppeteerPort";
    public string         Browser    { get; set; } = "Chrome";
    public System.Collections.Generic.List<CastEntry> Entries { get; set; }
        = new System.Collections.Generic.List<CastEntry>();
}

// ── Program ───────────────────────────────────────────────────────────────────

static class Program
{
    static readonly string ConfigFile = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "cast-urls.yaml");

    static CastConfig _config  = new CastConfig();
    static bool       casting  { get; set; }
    static CastEntry  _selected;
    static dynamic    browser  { get; set; }

    static readonly System.Windows.Forms.Timer _browserWatchdog = new System.Windows.Forms.Timer { Interval = 3000 };

    // ── Controls ──────────────────────────────────────────────────────────────

    static GPALListView urlList = GPAL.ListView
        .WithColumns("Title", "URL / Path")
        .WithColumnWidth(288, -2)
        .WithView(ListViewMode.Details)
        .WithFullRowSelect(true)
        .WithHeight(180)
        .WithName("urlList")
        .WithCallback<EventHandler>(OnEntrySelected)
        .WithCallbackOffUIThread<EventHandler>(OnCast).ForEvent(ControlEventType.DoubleClick)
        .ToGPALObject();

    static GPALInput titleInput = GPAL.Input
        .WithName("titleInput")
        .ToGPALObject();

    static GPALFileSelector urlInput = (GPALFileSelector)GPAL.FileSelector
        .WithFilter("All Files (*.*)|*.*|Video Files|*.mp4;*.mkv;*.avi;*.mov;*.wmv|Web Pages|*.html;*.htm")
        .WithCompact(true)
        .WithPlaceholder("Paste a URL or browse for a local file...")
        .WithName("urlInput")
        .ToGPALObject();

    static GPALButton addBtn = GPAL.Button
        .WithName("add")
        .WithText("➕ Add")
        .WithCallback<EventHandler>(OnAdd)
        .ToGPALObject();

    static GPALButton removeBtn = GPAL.Button
        .WithEnabled(false)
        .WithName("remove")
        .WithText("➖ Remove")
        .WithCallback<EventHandler>(OnRemove)
        .ToGPALObject();

    static GPALInput deviceInput = GPAL.Input
        .WithName("deviceInput")
        .ToGPALObject();

    static GPALComboBox castModeCombo = GPAL.ComboBox
        .WithItems("Cast Tab", "Cast Desktop")
        .WithSelectedIndex(0)
        .WithName("castMode")
        .ToGPALObject();

    // Casting needs Chrome's own Cast commands, which PuppeteerPort and Selenium have and OttoMagic does not.
    static GPALComboBox engineCombo = GPAL.ComboBox
        .WithItems("PuppeteerPort", "Selenium")
        .WithSelectedIndex(0)
        .WithName("engine")
        .ToGPALObject();

    // Chrome and Edge both cast; Firefox has no Cast.
    static GPALComboBox browserCombo = GPAL.ComboBox
        .WithItems("Chrome", "Edge")
        .WithSelectedIndex(0)
        .WithName("browserType")
        .ToGPALObject();

    // the browser runs on a Windows desktop of its own, so it casts without appearing on the screen you work on
    static GPALCheckbox hiddenDesktopCheck = GPAL.CheckboxFor("Hidden desktop", true);

    static GPALButton loadBtn = GPAL.Button
        .WithName("load")
        .WithText("📂 Load")
        .WithCallback<EventHandler>(OnLoad)
        .ToGPALObject();

    static GPALButton saveBtn = GPAL.Button
        .WithName("save")
        .WithText("💾 Save")
        .WithCallback<EventHandler>(OnSave)
        .ToGPALObject();

    static GPALButton castBtn = GPAL.Button
        .WithEnabled(false)
        .WithName("cast")
        .WithText("📺 Cast")
        .WithCallback<EventHandler>((s, e) => castBtn.Enabled = false)
        .WithCallbackOffUIThread<EventHandler>(OnCast)
        .ToGPALObject();

    static GPALButton stopBtn = GPAL.Button
        .WithEnabled(false)
        .WithName("stop")
        .WithText("🛑 Stop Casting")
        .WithCallback<EventHandler>(OnStop)
        .ToGPALObject();

    // shows the hidden desktop for a minute, or until Escape or its Return button, long enough to set the video to full screen
    static GPALButton peekBtn = GPAL.Button
        .WithEnabled(false)
        .WithName("peek")
        .WithText("👁 Peek")
        .WithCallbackOffUIThread<EventHandler>(OnPeek)
        .ToGPALObject();

    static GPALButton exitBtn = GPAL.Button
        .WithName("exit")
        .WithText("🚪 Exit")
        .WithCallback<EventHandler>((s, e) => Application.Exit())
        .ToGPALObject();

    static GPALButton aboutBtn = GPAL.Button
        .WithName("about")
        .WithText("ℹ About")
        .WithCallback<EventHandler>(OnAbout)
        .ToGPALObject();

    // ── About ─────────────────────────────────────────────────────────────────

    static GPALLabel aboutText = (GPALLabel)GPAL.LabelFor(
        "CastManager, a GPAL Bot of the Week.\n\n" +
        "Cast your favorite websites, local files, and videos\n" +
        "to any Google Cast device with one click.\n\n" +
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

    static GPALStatusStrip statusBar = GPAL.StatusStrip
        .WithText("Ready")
        .WithName("status")
        .ToGPALObject();

    // ── Entry point ───────────────────────────────────────────────────────────

    static GPALSplitter favouritesAndCasting = (GPALSplitter)GPAL.Splitter.HSplit(.80)
        .WithFormControl(GPAL.LabelFor("Title:"))
        .WithFormControl(titleInput)
        .WithFormControl(GPAL.LabelFor("URL or File Path:"))
        .WithFormControl(urlInput)
        .WithFormControl(addBtn)
        .WithFormControl(removeBtn)
        .WithFormControl(GPAL.LabelFor("Saved Favorites:"))
        .WithFormControl(urlList)
        .WithFormControl(GPAL.SplitRight)
        .WithFormControl(GPAL.LabelFor("Cast Device Name:"))
        .WithFormControl(deviceInput)
        .WithFormControl(GPAL.LabelFor("Cast Mode:"))
        .WithFormControl(castModeCombo)
        // Hidden: the shipped version casts with Chrome on PuppeteerPort. Restore these four lines to choose.
        //.WithFormControl(GPAL.LabelFor("Engine:"))
        //.WithFormControl(engineCombo)
        //.WithFormControl(GPAL.LabelFor("Browser:"))
        //.WithFormControl(browserCombo)
        .WithFormControl(hiddenDesktopCheck)
        .WithFormControl(GPAL.SpacerFor(18))
        .WithFormControl(loadBtn)
        .WithFormControl(saveBtn)
        .WithFormControl(GPAL.SpacerFor(18))
        .WithFormControl(castBtn)
        .WithFormControl(stopBtn)
        .WithFormControl(peekBtn)
        .WithFormControl(GPAL.SpacerFor(18))
        .WithFormControl(exitBtn)
        .WithFormControl(aboutBtn)
        .ToGPALObject();

    [STAThread]
    static void Main()
    {
        GPAL.WithInformationHandler(OnGPALInfo).WithPublishToConsole();
        _browserWatchdog.Tick += (s, e) => { if (casting && browser != null && !browser.IsAlive) OnStop(s, e); };

        var form = GPAL.Form
            .WithTitle("CastManager  |  A GPAL Bot of the Week")
            .WithWidth(700)
            .WithHeight(550)
            .WithFormControl(favouritesAndCasting)
            .WithFormControl(statusBar);

        LoadConfig();

        form.ShowDialog();

        browser?.Close(true);
    }

    // ── Config load / save ────────────────────────────────────────────────────

    static void LoadConfig(string path = null)
    {
        path = path ?? ConfigFile;

        if (!File.Exists(path)) return;

        GPAL.Converter
            .WithInput((GPALFile)path)
            .SaveTo(ref _config);

        RefreshList();
        deviceInput.Text = _config.DeviceName;

        castModeCombo.SelectedIndex = _config.CastMode == "Desktop" ? 1 : 0;
        // With the combos hidden, a saved list does not change the engine or browser.
        //engineCombo.SelectedIndex   = _config.Engine == "Selenium" ? 1 : 0;
        //browserCombo.SelectedIndex  = _config.Browser == "Edge" ? 1 : 0;

        SetStatus($"Loaded {_config.Entries.Count} item(s).");
    }

    static void SaveConfig(string path = null)
    {
        path = path ?? ConfigFile;

        _config.DeviceName = deviceInput.Text.Trim();
        _config.CastMode   = castModeCombo.SelectedIndex == 1 ? "Desktop" : "Tab";
        _config.Engine     = engineCombo.SelectedIndex == 1 ? "Selenium" : "PuppeteerPort";
        _config.Browser    = browserCombo.SelectedIndex == 1 ? "Edge" : "Chrome";

        GPAL.Converter
            .WithInput(_config)
            .SaveTo((GPALFile)path);

        SetStatus($"Saved to {path}");
    }

    // ── List helpers ──────────────────────────────────────────────────────────

    static void RefreshList()
    {
        if (!urlList.IsReady) return;
        urlList.ClearItems();
        foreach (var entry in _config.Entries)
            urlList.AddItem(entry.Title, entry.Url);
        UpdateCastButton();
    }

    static void UpdateCastButton()
    {
        castBtn.Enabled = _selected != null && !casting;
    }

    // ── List callbacks ────────────────────────────────────────────────────────

    static void OnEntrySelected(object s, EventArgs e)
    {
        int idx = urlList.SelectedIndex;
        if (idx < 0) return;
        _selected         = idx < _config.Entries.Count ? _config.Entries[idx] : null;
        removeBtn.Enabled = _selected != null;
        UpdateCastButton();
        if (_selected == null) return;
        titleInput.Text = _selected.Title;
        urlInput.SetPath(_selected.Url);
    }

    static void OnAdd(object s, EventArgs e)
    {
        string url   = urlInput.SelectedPath.Trim();
        string title = titleInput.Text?.Trim();
        if (string.IsNullOrEmpty(url)) return;
        if (string.IsNullOrEmpty(title))
        {
            MessageBox.Show("Please enter a title before adding.", "Title Required",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            titleInput.Focus();
            return;
        }
        bool dup = false;
        foreach (var en in _config.Entries) if (en.Url == url) { dup = true; break; }
        if (!dup) _config.Entries.Add(new CastEntry { Title = title, Url = url });
        titleInput.Text = string.Empty;
        urlInput.Clear();
        RefreshList();
    }

    static void OnRemove(object s, EventArgs e)
    {
        if (_selected == null) return;
        _config.Entries.Remove(_selected);
        _selected         = null;
        removeBtn.Enabled = false;
        RefreshList();
    }

    static void OnLoad(object s, EventArgs e)
    {
        using (var dlg = new OpenFileDialog())
        {
            dlg.Title            = "Load Cast List";
            dlg.Filter           = "YAML Files (*.yaml)|*.yaml|All Files (*.*)|*.*";
            dlg.InitialDirectory = Path.GetDirectoryName(ConfigFile);
            dlg.FileName         = Path.GetFileName(ConfigFile);
            if (dlg.ShowDialog() != DialogResult.OK) return;
            LoadConfig(dlg.FileName);
        }
    }

    static void OnSave(object s, EventArgs e)
    {
        using (var dlg = new SaveFileDialog())
        {
            dlg.Title            = "Save Cast List";
            dlg.Filter           = "YAML Files (*.yaml)|*.yaml|All Files (*.*)|*.*";
            dlg.InitialDirectory = Path.GetDirectoryName(ConfigFile);
            dlg.FileName         = Path.GetFileName(ConfigFile);
            if (dlg.ShowDialog() != DialogResult.OK) return;
            SaveConfig(dlg.FileName);
        }
    }

    // ── Cast callbacks ────────────────────────────────────────────────────────

    // 1 while a cast starts. A double click on the list and the Cast button start one on separate queues
    static int starting;

    static void OnCast(object s, EventArgs e)
    {
        bool mine = 0 == System.Threading.Interlocked.CompareExchange(ref starting, 1, 0);

        try
        {
            if (false == mine) return;

            if (_selected == null) return;

            string target = ToTarget(_selected.Url);
            bool desktop = castModeCombo.SelectedIndex == 1;

            string device = deviceInput.Text.Trim();
            bool hidden = hiddenDesktopCheck.Checked;
            AutomationEngine engine = engineCombo.SelectedIndex == 1 ? AutomationEngine.Selenium : AutomationEngine.PuppeteerPort;
            BrowserType browserType = browserCombo.SelectedIndex == 1 ? BrowserType.Edge : BrowserType.Chrome;

            if (casting && browser != null)
            {
                SetStatus($"Navigating to [{_selected.Title}]...");
                browser.GoTo(target);
                if (desktop)
                    browser.CastDesktop(device);
                else
                    browser.CastTab(device);
                return;
            }

            if (string.IsNullOrWhiteSpace(device))
            {
                castBtn.OnUiThread(() => MessageBox.Show("Enter a Cast device name.", "Missing Device",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning));
                return;
            }

            casting           = true;
            castBtn.Enabled   = false;
            stopBtn.Enabled   = true;
            peekBtn.Enabled   = hidden;
            removeBtn.Enabled = false;
            // a Windows Forms timer ticks only on the thread that started it, so it starts on the UI thread
            castBtn.OnUiThread(() => _browserWatchdog.Start());
            SetStatus($"Casting \"{_selected.Title}\" to \"{device}\"...");

            browser?.Close(true);
            browser = null;

            browser = GPAL.Browser
                .WithBrowserType(browserType)
                .WithAutomationEngine(engine)
                .WithUseStealth(StealthType.PatchDriver)
                .WithHiddenDesktop(hidden)
                .WithDriverLocation(@"c:\drivers\")
                .GoTo(target);

            if (desktop)
                browser.CastDesktop(device);
            else
                browser.CastTab(device);
        }
        finally
        {
            // the click turned the button off; it comes back as the program's state has it
            if (true == mine)
            {
                System.Threading.Interlocked.Exchange(ref starting, 0);
                UpdateCastButton();
            }
        }
    }

    static void OnStop(object s, EventArgs e)
    {
        _browserWatchdog.Stop();
        browser?.Close();
        browser           = null;
        casting           = false;
        stopBtn.Enabled   = false;
        peekBtn.Enabled   = false;
        UpdateCastButton();
        SetStatus("Casting stopped.");
    }

    // puts the browser's hidden desktop on the screen for a minute, or until Escape or the Return button on it,
    // long enough to set the video to full screen before coming back
    static void OnPeek(object s, EventArgs e)
    {
        IHiddenDesktop desktop = browser?.HiddenDesktop;

        if (null == desktop)
        {
            SetStatus("The browser is not on a hidden desktop.");
            return;
        }

        SetStatus("Showing the hidden desktop. Press Escape or click Return to come back.");

        string failed = desktop.Peek(60_000);

        if (null != failed)
            SetStatus($"Could not show the hidden desktop: {failed}");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static bool IsFilePath(string s) =>
        !string.IsNullOrEmpty(s) &&
        !s.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
        !s.StartsWith("file:", StringComparison.OrdinalIgnoreCase);

    static string ToTarget(string urlOrPath) =>
        IsFilePath(urlOrPath)
            ? "file:///" + urlOrPath.Replace('\\', '/')
            : urlOrPath;

    static void OnAbout(object s, EventArgs e)
    {
        GPAL.Form
            .WithTitle("About CastManager")
            .WithWidth(420)
            .WithFormControl(aboutText)
            .WithFormControl(GPAL.SpacerFor(12))
            .WithFormControl(moreBotsLink)
            .WithFormControl(GPAL.SpacerFor(12))
            .WithFormControl(aboutOkBtn)
            .ShowDialog();
    }

    static void OnGPALInfo(object sender, GPAL.GPALEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Message)) return;
        if (!e.Message.StartsWith("Cast", StringComparison.OrdinalIgnoreCase)) return;
        int colon = e.Message.IndexOf(':');
        SetStatus(colon >= 0 ? e.Message.Substring(colon + 2) : e.Message);
    }

    static void SetStatus(string msg) => statusBar.Text = msg;
}
