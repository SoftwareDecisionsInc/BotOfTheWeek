// =============================================================================
// GPAL Bot of the Week #3 - PageSpy
//
// Navigate to any URL and scan for elements. Results are grouped by element
// type in a TreeView. Each child node shows: [text] [placeholder] [value] [href/src/attr] [css]
// Filter combobox re-renders the tree in real-time from stored results.
// URL history auto-saved to pagespy-history.txt next to the exe (max 20).
// "Hide Browser" uses Get (headless) instead of GoTo; checked by default.
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

class PageSpyHistory
{
    public List<string> RecentUrls { get; set; } = new List<string>();
}

static class Program
{
    static IBrowser _browser;

    const int MaxHistory = 20;
    static readonly string _historyFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pagespy-history.yaml");
    static PageSpyHistory _history = new PageSpyHistory();

    static readonly (string tag, string label, string attr)[] _scanTypes =
    {
        ("a",      "Links",   null),
        ("img",    "Images",  null),
        ("input",  "Inputs",  "name"),
        ("button", "Buttons", null),
        ("form",   "Forms",   "action"),
    };

    static Dictionary<string, List<string>> _results = new Dictionary<string, List<string>>();

    // ── Controls ──────────────────────────────────────────────────────────────

    static GPALInput urlInput = GPAL.Input
        .WithPlaceholder("https://...")
        .WithName("url")
        .ToGPALObject();

    static GPALComboBox filterCombo = (GPALComboBox)GPAL.ComboBox
        .WithItems("All", "Links", "Images", "Inputs", "Buttons", "Forms")
        .WithSelectedIndex(0)
        .WithCallback<EventHandler>(OnFilterChanged)
        .WithName("filter")
        .ToGPALObject();

    static GPALButton scanBtn = GPAL.Button
        .WithText("🔍 Scan")
        .WithCallback<EventHandler>((s, e) => scanBtn.Enabled = false)
        .WithCallbackOffUIThread<EventHandler>(OnScan)
        .WithName("scanBtn")
        .ToGPALObject();

    static GPALLabel statusLabel = GPAL.Label
        .WithText("Enter a URL and click Scan.")
        .WithName("status")
        .ToGPALObject();

    static GPALCheckbox hideBrowserCheck = (GPALCheckbox)GPAL.Checkbox
        .WithChecked(true)
        .WithText("Hide Browser")
        .WithName("hideBrowser")
        .ToGPALObject();

    static GPALButton exitBtn = GPAL.Button
        .WithText("🚪 Exit")
        .WithCallback<EventHandler>((s, e) => System.Windows.Forms.Application.Exit())
        .WithName("exitBtn")
        .ToGPALObject();

    static GPALButton aboutBtn = GPAL.Button
        .WithName("about")
        .WithText("ℹ About")
        .WithCallback<EventHandler>(OnAbout)
        .ToGPALObject();

    static GPALListView urlHistoryList = (GPALListView)GPAL.ListView
        .WithColumns("URL")
        .WithCallback<EventHandler>(OnUrlSelected)
        .WithCallbackOffUIThread<EventHandler>(OnUrlDoubleClick).ForEvent(ControlEventType.DoubleClick)
        .WithName("urlHistory")
        .ToGPALObject();

    static GPALButton deleteUrlBtn = GPAL.Button
        .WithText("🗑 Delete")
        .WithCallback<EventHandler>(OnDeleteUrl)
        .WithName("deleteUrl")
        .ToGPALObject();

    static GPALButton clearHistoryBtn = GPAL.Button
        .WithText("♻ Clear All")
        .WithCallback<EventHandler>(OnClearHistory)
        .WithName("clearHistory")
        .ToGPALObject();

    static GPALTreeView resultsTree = (GPALTreeView)GPAL.TreeView
        .WithShowLines(true)
        .WithShowPlusMinus(true)
        .WithName("results")
        .ToGPALObject();

    // ── About ─────────────────────────────────────────────────────────────────

    static GPALLabel aboutText = (GPALLabel)GPAL.LabelFor(
        "PageSpy, a GPAL Bot of the Week.\n\n" +
        "Scan any page for its links, images, inputs,\n" +
        "buttons and forms, grouped by type in a tree.\n\n" +
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

    // ── Entry point ───────────────────────────────────────────────────────────

    static GPALSplitter controlsAndResults = (GPALSplitter)GPAL.Splitter.HSplit(0.25)
        .WithFormControl(GPAL.LabelFor("URL:"))
        .WithFormControl(urlInput)
        .WithFormControl(urlHistoryList)
        .WithFormControl(deleteUrlBtn)
        .WithFormControl(clearHistoryBtn)
        .WithFormControl(GPAL.LabelFor("Filter:"))
        .WithFormControl(filterCombo)
        .WithFormControl(GPAL.SpacerFor(18))
        .WithFormControl(scanBtn)
        .WithFormControl(statusLabel)
        .WithFormControl(GPAL.SpacerFor(18))
        .WithFormControl(hideBrowserCheck)
        .WithFormControl(GPAL.SpacerFor(18))
        .WithFormControl(exitBtn)
        .WithFormControl(aboutBtn)
        .WithFormControl(GPAL.SplitRight)
        .WithFormControl(resultsTree)
        .ToGPALObject();

    [STAThread]
    static void Main()
    {
        GPAL.WithInformationHandler(OnGPALEvent);
        AppDomain.CurrentDomain.ProcessExit += (s, e) => SaveHistory();

        var form = GPAL.Form
            .WithTitle("PageSpy")
            .WithWidth(1000)
            .WithHeight(466)
            .WithFormControl(controlsAndResults)
            .Show();

        LoadHistory();

        form.ShowDialog();
    }

    // ── History ───────────────────────────────────────────────────────────────

    static void LoadHistory()
    {
        if (!File.Exists(_historyFile)) return;
        GPAL.Converter.WithInput((GPALFile)_historyFile).SaveTo(ref _history);
        RefreshHistoryList();
    }

    static void SaveHistory()
    {
        try { GPAL.Converter.WithInput(_history).SaveTo((GPALFile)_historyFile); }
        catch { }
    }

    static void AddToHistory(string url)
    {
        _history.RecentUrls.Remove(url);
        _history.RecentUrls.Insert(0, url);
        if (_history.RecentUrls.Count > MaxHistory)
            _history.RecentUrls.RemoveRange(MaxHistory, _history.RecentUrls.Count - MaxHistory);
        SaveHistory();
        urlHistoryList.OnUiThread(RefreshHistoryList);
    }

    static void RefreshHistoryList()
    {
        urlHistoryList.ClearItems();
        foreach (var url in _history.RecentUrls)
            urlHistoryList.AddItem(url);
    }

    static void OnUrlSelected(object sender, EventArgs e)
    {
        var url = urlHistoryList.SelectedText;
        if (!string.IsNullOrEmpty(url))
            urlInput.Text = url;
    }

    static void OnUrlDoubleClick(object sender, EventArgs e) => OnScan(null, null);

    static void OnDeleteUrl(object sender, EventArgs e)
    {
        var url = urlHistoryList.SelectedText;
        if (string.IsNullOrEmpty(url)) return;
        _history.RecentUrls.Remove(url);
        SaveHistory();
        RefreshHistoryList();
    }

    static void OnClearHistory(object sender, EventArgs e)
    {
        _history.RecentUrls.Clear();
        SaveHistory();
        RefreshHistoryList();
    }

    // ── Scan ──────────────────────────────────────────────────────────────────

    // 1 while a scan runs. A double click on the history and the Scan button start scans on separate queues
    static int scanning;

    static void OnScan(object sender, EventArgs e)
    {
        bool mine = 0 == System.Threading.Interlocked.CompareExchange(ref scanning, 1, 0);

        try
        {
            if (false == mine) return;

            scanBtn.Enabled = false;

            var url = urlInput.Text?.Trim();
            if (string.IsNullOrEmpty(url)) return;

            AddToHistory(url);
            _results.Clear();
            ClearTree();
            Status("Loading page...");

            if (_browser == null)
                _browser = GPAL.Browser
                    .WithBrowserType(BrowserType.Chrome)
                    .WithUseStealth(StealthType.PatchDriver)
                    .WithAutomationEngine(AutomationEngine.PuppeteerPort)
                    .WithWaitOnDocumentReady(15_000)
                    .ToGPALObject();

            if (hideBrowserCheck.Checked)
                _browser.Get(url);
            else
                _browser.GoTo(url).WaitFor(2_000);

            Status("Scanning...");

            foreach (var (tag, label, attr) in _scanTypes)
                _results[label] = ScanLines(tag, attr);

            statusLabel.OnUiThread(ApplyFilter);
        }
        finally
        {
            // the click turned the button off; it comes back as the program's state has it
            if (true == mine)
            {
                System.Threading.Interlocked.Exchange(ref scanning, 0);
                scanBtn.Enabled = true;
            }
        }
    }

    static void OnFilterChanged(object sender, EventArgs e) => ApplyFilter();

    static void ApplyFilter()
    {
        var filter = filterCombo.SelectedValue ?? "All";
        ClearTree();
        int total = 0;

        foreach (var (_, label, _) in _scanTypes)
        {
            if (filter != "All" && filter != label) continue;
            if (!_results.TryGetValue(label, out var lines) || lines.Count == 0) continue;

            var parent = resultsTree.AddNode($"{label} ({lines.Count})");
            foreach (var line in lines)
                parent.AddChild(line);
            parent.Expand();
            total += lines.Count;
        }

        if (total > 0)
            Status($"Showing [{total}] elements.");
    }

    static List<string> ScanLines(string tag, string attr)
    {
        Selector sel = attr != null
            ? GPAL.Selector.WithCSS(tag).UseAttribute(attr).ToGPALObject()
            : GPAL.Selector.WithCSS(tag).ToGPALObject();

        List<GPALElement> elements;
        _browser
            .WithSelector(sel)
            .GetElements(out elements);

        var lines = new List<string>();
        if (elements == null) return lines;

        foreach (var el in elements)
        {
            string text        = el.Text?.Trim()        ?? "";
            string placeholder = el.Placeholder?.Trim() ?? "";
            string value       = el.Value?.Trim()       ?? "";
            string keyVal      = tag == "a"   ? (el.Href ?? "")
                               : tag == "img" ? (el.Src  ?? "")
                               : attr != null ? (el.GetAttribute(attr) ?? "")
                               :                "";
            string css         = el.Css ?? "";

            var tokens = new List<string>();
            if (!string.IsNullOrEmpty(text))        tokens.Add($"[{text}]");
            if (!string.IsNullOrEmpty(placeholder)) tokens.Add($"[{placeholder}]");
            if (!string.IsNullOrEmpty(value))       tokens.Add($"[{value}]");
            if (!string.IsNullOrEmpty(keyVal))      tokens.Add($"[{keyVal}]");
            if (!string.IsNullOrEmpty(css))         tokens.Add($"[{css}]");

            if (tokens.Count > 0)
                lines.Add(string.Join(" ", tokens));
        }

        lines.Sort(NaturalCompare);
        return lines;
    }

    static void ClearTree() => resultsTree.ClearNodes();

    static void Status(string msg) => statusLabel.OnUiThread(() => statusLabel.Text = msg);

    static void OnAbout(object s, EventArgs e)
    {
        GPAL.Form
            .WithTitle("About PageSpy")
            .WithWidth(420)
            .WithFormControl(aboutText)
            .WithFormControl(GPAL.SpacerFor(12))
            .WithFormControl(moreBotsLink)
            .WithFormControl(GPAL.SpacerFor(12))
            .WithFormControl(aboutOkBtn)
            .ShowDialog();
    }

    static void OnGPALEvent(object sender, GPAL.GPALEventArgs e) { }

    static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                long.TryParse(a.Substring(si, i - si), out long na);
                long.TryParse(b.Substring(sj, j - sj), out long nb);
                int cmp = na.CompareTo(nb);
                if (cmp != 0) return cmp;
            }
            else
            {
                int cmp = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                if (cmp != 0) return cmp;
                i++; j++;
            }
        }
        return a.Length - b.Length;
    }
}
