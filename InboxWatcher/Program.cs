// =============================================================================
// GPAL Bot of the Week - InboxWatcher
//
// Watches a mailbox and does the work its mail asks for. Each rule names the
// mail it wants by sender, subject and attachment, and says what to do with
// it: save the attachments, take files out of an archive, convert them, and
// post to Slack, Teams or any webhook when it is done. A message is marked
// read only after its rule has run. The rules and settings are kept in
// inboxwatcher.yaml through GPAL.Converter, the servers in GPALMail.yaml by
// the mail object's SaveSettings, and "Get Test Account" fetches a throwaway
// Ethereal account so the bot runs with nothing set up. Send Test Message
// mails it a zip of two csv files, which the sample rule turns into json and html.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using GenerallyPositive;
using GenerallyPositive.GPALForm;
using static GenerallyPositive.Enums;

// -- What a rule matches, and what it does ----------------------------------

class Rule
{
    public string Name { get; set; } = string.Empty;
    // empty matches every sender, or every subject
    public string FromContains { get; set; } = string.Empty;
    public string SubjectContains { get; set; } = string.Empty;
    // the attachments to work on, such as *.zip; empty takes every message the sender and subject match
    public string Attachments { get; set; } = "*.zip";
    // what to take out of each attachment when it is an archive, such as *.csv; empty keeps the attachment as it is
    public string InsideArchive { get; set; } = "*.csv";
    // the formats each file is written in, such as json or json, html; empty leaves the files as they came
    public string ConvertTo { get; set; } = "json, html";
    // the first line of each file names the columns, so each row converts to an object with those names
    public bool FirstLineIsColumnNames { get; set; } = true;
    public string SaveFolder { get; set; } = string.Empty;
}

// -- Everything kept between runs, apart from the servers --------------------

class WatcherSettings
{
    public int Minutes { get; set; } = 5;
    // the environment variable that holds the webhook, never the webhook itself
    public string WebhookVariable { get; set; } = string.Empty;
    public List<Rule> Rules { get; set; } = new List<Rule>();
}

// -- A kept test account -----------------------------------------------------
// The Ethereal account is saved for the next run. Ethereal retires unused accounts; a login refused on send means that,
// and a new account is fetched and used. This is the only file InboxWatcher writes a password to, and only for an
// ethereal.email address, whose password protects nothing.

class TestAccount
{
    public string User { get; set; } = string.Empty;
    public string Pass { get; set; } = string.Empty;
}

// -- Ethereal's JSON, with matching names --

class EtherealAccount
{
    public string user { get; set; }
    public string pass { get; set; }
    public string web { get; set; }
    public EtherealServer smtp { get; set; }
    public EtherealServer imap { get; set; }
}

class EtherealServer
{
    public string host { get; set; }
    public int port { get; set; }
}

static class Program
{
    static readonly string settingsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "inboxwatcher.yaml");
    static readonly string testAccountFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ethereal-account.yaml");
    static readonly string inboxFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "inbox");
    static readonly string sampleFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sample");
    static readonly string codeFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "code");

    // what Send Test Message sends and the sample rule looks for
    const string testSubject = "Daily export";

    // the formats the converter writes, in the order the Convert list shows them
    static readonly string[] convertFormats = { "json", "csv", "yaml", "xml", "xlsx", "html" };

    static IGPALMail mail;

    // one mail call at a time, whether it comes from the watch, Check Now or Send Test Message
    static readonly object mailLock = new object();

    // set by Stop, and waited on between checks so Stop ends the wait at once
    static readonly ManualResetEventSlim stopSignal = new ManualResetEventSlim(false);

    static WatcherSettings settings { get; set; } = new WatcherSettings();

    // the folder each history row saved into, in the same order as the rows
    static List<string> historyFolders { get; set; } = new List<string>();

    // Messages that failed this session, by message id. One that fails is left unread for a person to look at, and
    // skipped until the program is started again, so a message that always fails does not fail every check
    static HashSet<string> failedMessages { get; set; } = new HashSet<string>();

    // -- Tabs -----------------------------------------------------------------

    static GPALTab watchTab = GPAL.Tab.WithName("watchTab").WithText("Watch").ToGPALObject();
    static GPALTab rulesTab = GPAL.Tab.WithName("rulesTab").WithText("Rules").ToGPALObject();
    static GPALTab accountTab = GPAL.Tab.WithName("accountTab").WithText("Account").ToGPALObject();
    static GPALTab howToTab = GPAL.Tab.WithName("howToTab").WithText("How To").ToGPALObject();

    // The steps, so the bot explains itself.
    static readonly string[] instructions =
    {
        "InboxWatcher reads a mailbox and does the work its mail asks for: it saves the attachments,",
        "takes files out of an archive, converts them, and posts to a webhook when it is done.",
        "",
        "1.  Account tab. The IMAP server and port it reads from, the SMTP server and port Send",
        "    Test Message sends through, your username and your password. Save Account writes",
        "    the servers to GPALMail.yaml in the folder InboxWatcher starts in, normally beside",
        "    this exe. The password is never written there,",
        "    so type it in each time InboxWatcher starts.",
        "",
        "    No account to try it on? Get Test Account asks Ethereal for a throwaway mailbox and",
        "    fills in the tab. It is kept in ethereal-account.yaml beside this exe for next time.",
        "    Ethereal delivers nothing outside itself, so its password protects nothing.",
        "",
        "2.  Rules tab. A rule names the mail it wants and says what to do with it:",
        "",
        "        From contains      part of the sender's address; empty is any sender",
        "        Subject contains   part of the subject; empty is any subject",
        "        Attachments        such as *.zip or report*.csv; empty takes every message the",
        "                           sender and subject match",
        "        Take out of each   such as *.csv, for an attachment that is an archive; empty",
        "        archive            keeps each attachment as it is",
        "        Convert to         each format ticked is written beside each file; none ticked",
        "                           leaves the files as they came",
        "        Save into          a folder for each message goes under it, named for when the",
        "                           message arrived and its subject, holding every attachment",
        "",
        "    Tick The first line of each file names the columns when it does, so each row",
        "    becomes an object with those names.",
        "",
        "    Add makes a new rule from the fields. Pick a rule in the list to load it into the",
        "    fields; Update changes it to what the fields hold, and Remove takes it out. Save",
        "    Rules writes the rules to inboxwatcher.yaml beside this exe, and so does Start",
        "    Watching; until one of them is pressed a change lasts only this run.",
        "",
        "    The first rule that matches an unread message handles it. Mail no rule matches",
        "    stays unread.",
        "",
        "    Show Code writes the rule in the fields as a console program of its own, to copy,",
        "    or to save as a .cs file in the code folder beside this exe.",
        "",
        "3.  Watch tab. Check Now reads the newest fifty unread messages once. Start Watching",
        "    checks, waits the minutes set, and checks again, until Stop.",
        "",
        "    A message is marked read only after its rule has run. One that fails is left",
        "    unread for you to look at, and skipped until InboxWatcher is started again.",
        "",
        "    Every message a rule took lands in the list, a failed one marked so. Double-click a",
        "    row to open its folder.",
        "",
        "4.  To hear when a rule has run, put a Slack, Teams, Discord or other webhook in a user",
        "    environment variable and type the variable's name on the Watch tab. The webhook",
        "    itself is never saved or shown.",
        "",
        "",
        "To try it with nothing set up: Get Test Account, then Send Test Message, then Check Now.",
        "The test message carries a zip of two csv files. On a first run the sample rule, Daily",
        "export, is already there, and turns them into json and html in the inbox folder beside",
        "this exe."
    };

    // the steps, filled in when the form is built
    static GPALTextArea instructionArea = (GPALTextArea)GPAL.TextArea
        .WithReadOnly(true)
        .WithHeight(740)
        .WithName("instructions");

    // -- Watch ----------------------------------------------------------------

    static GPALNumericUpDown minutesUpDown = (GPALNumericUpDown)GPAL.NumericUpDown
        .WithMinimum(1)
        .WithMaximum(1440)
        .WithValue(5)
        .WithIncrement(1)
        .WithDecimalPlaces(0)
        .WithName("minutes")
        .WithToolTip("How long to wait between one check and the next")
        .ToGPALObject();

    static GPALInput webhookVariableInput = GPAL.Input
        .WithPlaceholder("GPAL_SLACK_WEBHOOK")
        .WithName("webhookVariable")
        .WithToolTip("The name of a user environment variable holding a Slack, Teams, Discord or other webhook. Empty posts nowhere.")
        .ToGPALObject();

    static GPALButton startBtn = GPAL.Button
        .WithName("start")
        .WithText("▶ Start Watching")
        .WithCallback<EventHandler>((s, e) => startBtn.Enabled = false)
        .WithCallbackOffUIThread<EventHandler>(OnStart)
        .ToGPALObject();

    static GPALButton stopBtn = GPAL.Button
        .WithEnabled(false)
        .WithName("stop")
        .WithText("⏹ Stop")
        .WithCallback<EventHandler>(OnStop)
        .ToGPALObject();

    static GPALButton checkNowBtn = GPAL.Button
        .WithName("checkNow")
        .WithText("📥 Check Now")
        .WithCallbackOffUIThread<EventHandler>(OnCheckNow)
        .ToGPALObject();

    static GPALButton sendTestBtn = GPAL.Button
        .WithName("sendTest")
        .WithText("🧪 Send Test Message")
        .WithCallbackOffUIThread<EventHandler>(OnSendTest)
        .ToGPALObject();

    static GPALButton aboutBtn = GPAL.Button
        .WithName("about")
        .WithText("ℹ About")
        .WithCallback<EventHandler>(OnAbout)
        .ToGPALObject();

    // every message a rule handled, newest last; double-click a row to open the folder it saved into
    static GPALListView historyList = (GPALListView)GPAL.ListView
        .WithColumns("Time", "Rule", "From", "Subject", "Files")
        .WithColumnWidth(70, 110, 170, 190, -2)
        .WithView(ListViewMode.Details)
        .WithFullRowSelect(true)
        .WithHeight(330)
        .WithName("history")
        .WithCallbackOffUIThread<EventHandler>(OnHistoryDoubleClick).ForEvent(ControlEventType.DoubleClick);

    // -- Rules ----------------------------------------------------------------

    static GPALListView rulesList = (GPALListView)GPAL.ListView
        .WithColumns("Name", "From", "Subject", "Attachments", "Inside", "Convert")
        .WithColumnWidth(120, 120, 120, 90, 70, -2)
        .WithView(ListViewMode.Details)
        .WithFullRowSelect(true)
        .WithHeight(150)
        .WithName("rules")
        .WithCallback<EventHandler>(OnRuleSelected);

    static GPALInput ruleNameInput = GPAL.Input.WithName("ruleName").ToGPALObject();

    static GPALInput fromInput = GPAL.Input
        .WithPlaceholder("any sender")
        .WithName("fromContains")
        .ToGPALObject();

    static GPALInput subjectInput = GPAL.Input
        .WithPlaceholder("any subject")
        .WithName("subjectContains")
        .ToGPALObject();

    static GPALInput attachmentsInput = GPAL.Input
        .WithPlaceholder("any message, with or without attachments")
        .WithName("attachments")
        .ToGPALObject();

    static GPALInput insideInput = GPAL.Input
        .WithPlaceholder("keep each attachment as it is")
        .WithName("insideArchive")
        .ToGPALObject();

    // every format ticked is written; none ticked leaves the files as they came
    static GPALListView convertList = (GPALListView)GPAL.ListView
        .WithColumns("Format")
        .WithView(ListViewMode.Details)
        .WithCheckBoxes()
        .WithHeight(150)
        .WithName("convertTo")
        .ToGPALObject();

    static GPALCheckbox columnNamesCheck = (GPALCheckbox)GPAL.Checkbox
        .WithChecked(true)
        .WithText("The first line of each file names the columns")
        .WithName("columnNames")
        .ToGPALObject();

    static GPALFileSelector saveFolderSelector = (GPALFileSelector)GPAL.FileSelector
        .WithSelectFolder(true)
        .WithCompact(true)
        .WithTitle("Where should this rule save what it finds?")
        .WithName("saveFolder")
        .ToGPALObject();

    static GPALButton addRuleBtn = GPAL.Button
        .WithName("addRule")
        .WithText("➕ Add")
        .WithToolTip("Adds the fields as a new rule, under a name no other rule has")
        .WithCallback<EventHandler>(OnAddRule)
        .ToGPALObject();

    static GPALButton updateRuleBtn = GPAL.Button
        .WithName("updateRule")
        .WithText("✏ Update")
        .WithToolTip("Changes the selected rule to what the fields hold")
        .WithCallback<EventHandler>(OnUpdateRule)
        .ToGPALObject();

    static GPALButton removeRuleBtn = GPAL.Button
        .WithName("removeRule")
        .WithText("🗑 Remove")
        .WithCallback<EventHandler>(OnRemoveRule)
        .ToGPALObject();

    static GPALButton saveRulesBtn = GPAL.Button
        .WithName("saveRules")
        .WithText("💾 Save Rules")
        .WithCallback<EventHandler>(OnSaveRules)
        .ToGPALObject();

    static GPALButton showCodeBtn = GPAL.Button
        .WithName("showCode")
        .WithText("💻 Show Code")
        .WithToolTip("The GPAL workflow that does what the rule in the fields does, to use in a program of your own")
        .WithCallback<EventHandler>(OnShowCode)
        .ToGPALObject();

    // -- Show Code ------------------------------------------------------------

    static GPALLabel codeHelp = GPAL.LabelFor(
        "A console program that does what this rule does. Add the GPAL package to it, copy GPALMail.yaml\n" +
        "from beside InboxWatcher to the folder it runs in, and set the password variable it names.");

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
        .WithToolTip("Writes the code to a .cs file in the code folder beside InboxWatcher")
        .WithCallback<EventHandler>(OnSaveCode)
        .ToGPALObject();

    // the default button closes the dialog
    static GPALButton closeCodeBtn = GPAL.Button
        .Default
        .WithName("closeCode")
        .WithText("Close")
        .ToGPALObject();

    // -- Account --------------------------------------------------------------

    // Sending only, which here is Send Test Message. Read only on the SMTP connect; it never affects the reading server.
    static GPALCheckbox anyCertBox = (GPALCheckbox)GPAL.Checkbox
        .WithChecked(false)
        .WithText("Send even if the server cannot prove its certificate")
        .WithName("anyCert")
        .ToGPALObject();

    static GPALInput smtpHostInput = GPAL.Input.WithName("smtpHost").ToGPALObject();
    static GPALInput smtpPortInput = GPAL.Input.WithName("smtpPort").ToGPALObject();
    static GPALInput imapHostInput = GPAL.Input.WithName("imapHost").ToGPALObject();
    static GPALInput imapPortInput = GPAL.Input.WithName("imapPort").ToGPALObject();
    static GPALInput userInput = GPAL.Input.WithName("user").ToGPALObject();

    static GPALInput passwordInput = (GPALInput)GPAL.Input
        .IsPassword
        .WithName("password");

    static GPALButton showPasswordBtn = GPAL.Button
        .WithName("showPassword")
        .WithText("👁 Show")
        .WithCallback<EventHandler>(OnShowPassword)
        .ToGPALObject();

    static GPALButton testAccountBtn = GPAL.Button
        .WithName("testAccount")
        .WithText("🧪 Get Test Account")
        .WithCallbackOffUIThread<EventHandler>(OnTestAccount)
        .ToGPALObject();

    static GPALButton saveAccountBtn = GPAL.Button
        .WithName("saveAccount")
        .WithText("💾 Save Account")
        .WithCallbackOffUIThread<EventHandler>(OnSaveAccount)
        .ToGPALObject();

    static GPALStatusStrip statusBar = GPAL.StatusStrip
        .WithText("Ready")
        .WithName("status")
        .ToGPALObject();

    // -- About ----------------------------------------------------------------

    static GPALLabel aboutText = (GPALLabel)GPAL.LabelFor(
        "InboxWatcher, a GPAL Bot of the Week.\n\n" +
        "Watches a mailbox, saves the attachments it is told to,\n" +
        "opens archives, converts what is inside, and posts\n" +
        "to a webhook when it is done.\n\n" +
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

    // -- Entry point ----------------------------------------------------------

    [STAThread]
    static void Main()
    {
        // one mail object for the life of the program, told what the account tab says before each use
        mail = GPAL.Mail.ToGPALObject();

        // what goes wrong shows on the status bar, since the watch runs with nobody looking at it
        GPAL.WithExceptionHandler((s, e) => SetStatus($"{e.Message} {e.ExceptionRaised?.Message}"));

        var form = GPAL.Form
            .WithTitle("InboxWatcher  |  A GPAL Bot of the Week")
            .WithTop(10)
            .WithWidth(720)
            .WithHeight(880)

            .WithFormControl(watchTab)
            .WithFormControl(GPAL.LabelFor("Check every (minutes):"))
            .WithFormControl(minutesUpDown)
            .WithFormControl(GPAL.LabelFor("When a rule has run, post to the webhook in this environment variable:"))
            .WithFormControl(webhookVariableInput)
            .WithFormControl(startBtn)
            .WithFormControl(stopBtn)
            .WithFormControl(checkNowBtn)
            .WithFormControl(sendTestBtn)
            .WithFormControl(aboutBtn)
            .WithFormControl(GPAL.LabelFor("Handled (double-click a row to open its folder):"))
            .WithFormControl(historyList)

            .WithFormControl(rulesTab)
            .WithFormControl(GPAL.LabelFor("The first rule that matches an unread message handles it. Messages no rule matches stay unread."))
            .WithFormControl(rulesList)
            .WithFormControl(GPAL.LabelFor("Name:"))
            .WithFormControl(ruleNameInput)
            .WithFormControl(GPAL.LabelFor("From contains:"))
            .WithFormControl(fromInput)
            .WithFormControl(GPAL.LabelFor("Subject contains:"))
            .WithFormControl(subjectInput)
            .WithFormControl(GPAL.LabelFor("Attachments, such as *.zip or report*.csv:"))
            .WithFormControl(attachmentsInput)
            .WithFormControl(GPAL.LabelFor("Take out of each archive, such as *.csv or data\\*.csv:"))
            .WithFormControl(insideInput)
            .WithFormControl(GPAL.LabelFor("Convert each file to, as many as are ticked (none keeps them as they came):"))
            .WithFormControl(convertList)
            .WithFormControl(columnNamesCheck)
            .WithFormControl(GPAL.LabelFor("Save into (a folder per message goes under it):"))
            .WithFormControl(saveFolderSelector)
            .WithFormControl(addRuleBtn)
            .WithFormControl(updateRuleBtn)
            .WithFormControl(removeRuleBtn)
            .WithFormControl(saveRulesBtn)
            .WithFormControl(showCodeBtn)

            .WithFormControl(accountTab)
            .WithFormControl(GPAL.LabelFor("IMAP server and port, for reading:"))
            .WithFormControl(imapHostInput)
            .WithFormControl(imapPortInput)
            .WithFormControl(GPAL.LabelFor("SMTP server and port, for Send Test Message:"))
            .WithFormControl(smtpHostInput)
            .WithFormControl(smtpPortInput)
            .WithFormControl(anyCertBox)
            .WithFormControl(GPAL.LabelFor("Username:"))
            .WithFormControl(userInput)
            .WithFormControl(GPAL.LabelFor("Password (never written to the settings file):"))
            .WithFormControl(passwordInput)
            .WithFormControl(showPasswordBtn)
            .WithFormControl(saveAccountBtn)
            .WithFormControl(GPAL.LabelFor("No account? Take a throwaway one from Ethereal, kept for next time:"))
            .WithFormControl(testAccountBtn)

            .WithFormControl(howToTab)
            .WithFormControl(instructionArea)

            .WithFormControl(statusBar);

        LoadSettings();
        LoadAccount();

        instructionArea.Text = string.Join(Environment.NewLine, instructions);

        form.ShowDialog();

        // a watch still waiting ends with the form
        stopSignal.Set();
    }

    // -- Settings load and save -----------------------------------------------

    static void LoadSettings()
    {
        // inboxwatcher.yaml, or the sample rule the first time, so Send Test Message has something to match
        if (true == File.Exists(settingsFile))
        {
            WatcherSettings loaded = new WatcherSettings();

            GPAL.Converter
                .WithInput((GPALFile)settingsFile)
                .SaveTo(ref loaded);

            // a key left blank in a hand-edited file loads as null, which means empty here
            loaded.WebhookVariable = loaded.WebhookVariable ?? string.Empty;
            loaded.Rules = loaded.Rules ?? new List<Rule>();
            foreach (Rule rule in loaded.Rules)
            {
                rule.Name = rule.Name ?? string.Empty;
                rule.FromContains = rule.FromContains ?? string.Empty;
                rule.SubjectContains = rule.SubjectContains ?? string.Empty;
                rule.Attachments = rule.Attachments ?? string.Empty;
                rule.InsideArchive = rule.InsideArchive ?? string.Empty;
                rule.ConvertTo = rule.ConvertTo ?? string.Empty;
                rule.SaveFolder = rule.SaveFolder ?? string.Empty;
            }

            settings = loaded;
        }
        else
            settings.Rules.Add(new Rule
            {
                Name = "Daily export",
                SubjectContains = testSubject,
                Attachments = "*.zip",
                InsideArchive = "*.csv",
                ConvertTo = "json, html",
                SaveFolder = inboxFolder
            });

        minutesUpDown.Value = Math.Max(1, settings.Minutes);
        webhookVariableInput.Text = settings.WebhookVariable;
        saveFolderSelector.SetPath(inboxFolder);

        foreach (string format in convertFormats)
            convertList.AddItem(format.ToUpperInvariant());
        convertList.CheckItem(Array.IndexOf(convertFormats, "json"));
        convertList.CheckItem(Array.IndexOf(convertFormats, "html"));

        // nothing to change, only to show
        ChangeRules(() => { });
    }

    static void SaveSettings()
    {
        settings.Minutes = (int)minutesUpDown.Value;
        settings.WebhookVariable = TextOf(webhookVariableInput);

        // the rules are edited on the form's thread and saved from the watch's
        lock (settings.Rules)
            GPAL.Converter
                .WithInput(settings)
                .SaveTo((GPALFile)settingsFile);
    }

    // An input's text, trimmed. An input nobody has typed in or set holds no text at all, which is empty here
    static string TextOf(GPALInput input) => (input.Text ?? string.Empty).Trim();

    // -- Watching -------------------------------------------------------------

    static void OnStart(object s, EventArgs e)
    {
        stopSignal.Reset();
        stopBtn.Enabled = true;

        // The buttons come back however the watch ends; GPAL publishes anything that ends it, which the status bar
        // shows. Check, then wait the minutes set, until Stop; the wait ends the moment Stop is pressed
        try
        {
            SaveSettings();

            while (false == stopSignal.IsSet)
            {
                string result = CheckMail();

                SetStatus($"Watching. {result} Next check at [{DateTime.Now.AddMinutes((double)minutesUpDown.Value):HH:mm}].");

                stopSignal.Wait(TimeSpan.FromMinutes((double)minutesUpDown.Value));
            }

            SetStatus("Stopped watching.");
        }
        finally
        {
            stopBtn.Enabled = false;
            startBtn.Enabled = true;
        }
    }

    static void OnStop(object s, EventArgs e)
    {
        stopSignal.Set();
        stopBtn.Enabled = false;
    }

    static void OnCheckNow(object s, EventArgs e)
    {
        SetStatus(CheckMail());
    }

    // Reads the unread mail and gives each message to the first rule that wants it. Returns what happened, for the
    // status bar
    static string CheckMail()
    {
        string result;

        lock (mailLock)
        {
            if (true == AccountIsReady())
            {
                int handled = 0;
                int failed = 0;

                ApplyAccount();

                // Unread only, so a handled message, marked read, is not read again; newest fifty a check. Clear first,
                // since the mail object keeps what every Receive brought back until it is cleared
                mail.Clear()
                    .WithUnreadOnly(true)
                    .WithMaxMessages(50)
                    .Receive(out List<ReceivedEmail> messages);

                // the rules as they are now, since the form can change them while this check runs
                List<Rule> rules;
                lock (settings.Rules)
                    rules = settings.Rules.ToList();

                foreach (ReceivedEmail email in messages.Where(message => false == failedMessages.Contains(KeyOf(message))))
                {
                    Rule rule = rules.FirstOrDefault(candidate => true == Matches(candidate, email));

                    if (null != rule)
                    {
                        if (true == Handle(rule, email))
                            handled++;
                        else
                            failed++;
                    }
                }

                result = null != mail.LastError
                    ? $"Could not read the mail at [{DateTime.Now:HH:mm}]. [{mail.LastError.Failure}]: {mail.LastError.Message}"
                    : $"Checked at [{DateTime.Now:HH:mm}]: [{handled}] handled" + (0 < failed ? $", [{failed}] failed and left unread." : ".");
            }
            else
                result = "Fill in the Account tab first, or press Get Test Account.";
        }

        return result;
    }

    // a rule's sender and subject are contained in the message's, and one of its attachments has a name the rule names
    static bool Matches(Rule rule, ReceivedEmail email)
    {
        bool fromMatches = 0 == rule.FromContains.Length || 0 <= (email.From ?? string.Empty).IndexOf(rule.FromContains, StringComparison.OrdinalIgnoreCase);
        bool subjectMatches = 0 == rule.SubjectContains.Length || 0 <= (email.Subject ?? string.Empty).IndexOf(rule.SubjectContains, StringComparison.OrdinalIgnoreCase);
        bool attachmentMatches = 0 == rule.Attachments.Length || true == Named(email).Any(name => true == NameMatches(name, rule.Attachments));

        return fromMatches && subjectMatches && attachmentMatches;
    }

    // The attachments' own names, without any folder the sender put in front of them. A part with no name, such as a
    // forwarded message, has none to match or save by
    static IEnumerable<string> Named(ReceivedEmail email) =>
        email.Attachments.Where(name => false == string.IsNullOrEmpty(name)).Select(name => Path.GetFileName(name));

    // a wildcard, as a folder takes one: * any run of characters, ? any one
    static bool NameMatches(string name, string pattern)
    {
        string regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";

        return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase);
    }

    // One message, one rule: its attachments go into a folder of their own, archives are opened, what is inside is
    // converted, and only then is the message marked read and the webhook told. A message that fails is left unread
    // and skipped for the rest of the session. Returns whether it was handled
    static bool Handle(Rule rule, ReceivedEmail email)
    {
        bool handled = false;
        string root = 0 == rule.SaveFolder.Length ? inboxFolder : rule.SaveFolder;
        string subject = SafeName(email.Subject);
        string folder = Path.Combine(root, $"{email.Received:yyyy-MM-dd_HHmmss} {(0 == subject.Length ? "no subject" : subject)}");

        try
        {
            // every attachment is written, the ones the rule works on and the rest
            List<GPALFile> saved = email.SaveTo((GPALFile)folder);
            int converted = 0;
            List<string> formats = Formats(rule);

            foreach (string name in Named(email).Where(attachment => 0 == rule.Attachments.Length || true == NameMatches(attachment, rule.Attachments)))
            {
                string attachment = Path.Combine(folder, name);
                List<string> sources = new List<string> { attachment };

                // An archive is a folder to GPALFile, so the files wanted are named inside it. They come out into the
                // message's folder beside the archive, replacing what an earlier try at this message left. An attachment
                // that is not an archive, or holds nothing that matches, is converted as it is
                if (0 < rule.InsideArchive.Length)
                {
                    GPALFile inside = (GPALFile)GPAL.File
                        .WithFileName(Path.Combine(attachment, rule.InsideArchive))
                        .WithOverwriteFile(true);

                    if (0 < inside.Filenames.Count)
                    {
                        inside.CopyTo(folder);
                        sources = inside.Filenames.Select(file => Path.Combine(folder, Path.GetFileName(file))).ToList();
                    }
                }

                // the converter reads each by its extension and writes every format the rule names, one SaveTo each
                // in one chain; only a file written counts
                if (0 < formats.Count)
                    foreach (string source in sources)
                    {
                        IAllowConverterActions converter = GPAL.Converter
                            .WithInput((GPALFile)source)
                            .WithFirstLineHasColumnNames(rule.FirstLineIsColumnNames);

                        // a format the file is already in is not written over the file itself
                        List<string> outputs = formats
                            .Select(format => Path.ChangeExtension(source, format))
                            .Where(output => false == string.Equals(output, source, StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        foreach (string output in outputs)
                            converter = converter.SaveTo((GPALFile)output);

                        converted += outputs.Count(output => true == File.Exists(output));
                    }
            }

            // after the work, so a run that failed partway finds the message still unread
            email.MarkAsRead();
            handled = true;

            string files = $"{saved.Count} attachment(s) saved" + (0 < converted ? $", {converted} file(s) written as {string.Join(", ", formats)}" : string.Empty);

            AddHistory(rule.Name, email, files, folder);

            string webhookVariable = TextOf(webhookVariableInput);

            // the webhook is read from the environment each time, so it is never in the settings file or on the screen
            if (0 < webhookVariable.Length)
                GPAL.Notify
                    .WithWebhook(GPAL.CredentialsFor(CredentialServiceType.StaticKey).WithKeyFromEnv(webhookVariable).ToGPALObject())
                    .Send($"InboxWatcher, {rule.Name}: \"{email.Subject}\" from {email.From}. {files}, in {folder}");
        }
        catch (Exception ex)
        {
            failedMessages.Add(KeyOf(email));
            AddHistory(rule.Name, email, "failed, left unread: " + ex.Message, folder);
            GPAL.PublishSimpleEvent(GPALEventType.EXCEPTION, $"[{rule.Name}] could not handle [{email.Subject}] from [{email.From}], so it is left unread.", ex: ex);
        }

        return handled;
    }

    // a message's own id, or for one sent without an id, when it was sent, by whom and about what
    static string KeyOf(ReceivedEmail email) => email.MessageId ?? $"{email.Received:o}|{email.From}|{email.Subject}";

    static void AddHistory(string rule, ReceivedEmail email, string files, string folder)
    {
        historyFolders.Add(folder);
        historyList.AddItem(DateTime.Now.ToString("HH:mm:ss"), rule, email.From, email.Subject, files);
    }

    // a subject as a folder name: the characters Windows refuses become a space, and it is kept short
    static string SafeName(string subject)
    {
        string name = string.Join(" ", (subject ?? string.Empty).Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();

        return 40 < name.Length ? name.Substring(0, 40).Trim() : name;
    }

    static void OnHistoryDoubleClick(object s, EventArgs e)
    {
        int index = historyList.SelectedIndex;

        if (0 <= index && index < historyFolders.Count && true == Directory.Exists(historyFolders[index]))
            GPAL.Launcher
                .WithExecutable("explorer.exe")
                .WithArgument(historyFolders[index])
                .Launch();
        else
            SetStatus("That message saved nothing, so there is no folder to open.");
    }

    // -- Send Test Message ----------------------------------------------------

    // Two csv files zipped, mailed to this account, so the sample rule has something to do on the next check
    static void OnSendTest(object s, EventArgs e)
    {
        if (true == AccountIsReady())
        {
            Directory.CreateDirectory(sampleFolder);
            File.WriteAllText(Path.Combine(sampleFolder, "orders.csv"), "OrderNumber,Total\r\n1001,19.99\r\n1002,5.25\r\n1003,42.00\r\n");
            File.WriteAllText(Path.Combine(sampleFolder, "customers.csv"), "Name,City\r\nAda,London\r\nGrace,Arlington\r\n");

            string zip = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "daily-export.zip");

            // the csv files zipped, the zip replaced each time
            ((GPALFile)GPAL.File.WithFileName(Path.Combine(sampleFolder, "*.csv")).WithOverwriteFile(true))
                .SaveTo(zip);

            lock (mailLock)
            {
                ApplyAccount();

                // Clear empties the envelope and keeps the server, so each send starts on a blank message
                mail.Clear()
                    .WithToEmailAddress(TextOf(userInput))
                    .WithSubject(testSubject)
                    .WithBody("Today's export is attached.")
                    .WithAttachment(zip);

                mail.Send();

                // A refused login on an ethereal address means a retired account: get a new one and send with it. The
                // new account is applied as it arrives, and Clear starts the message again for it
                if (MailFailure.Authentication == mail.LastError?.Failure && true == TextOf(userInput).EndsWith("@ethereal.email", StringComparison.OrdinalIgnoreCase))
                {
                    SetStatus($"[{TextOf(userInput)}] was refused, asking Ethereal for a new account.");

                    EtherealAccount replacement = AskEtherealForAnAccount();

                    if (null != replacement)
                    {
                        mail.Clear()
                            .WithToEmailAddress(replacement.user)
                            .WithSubject(testSubject)
                            .WithBody("Today's export is attached.")
                            .WithAttachment(zip);

                        mail.Send();
                    }
                }

                // read before the lock is let go, so a check starting next cannot change what it says
                SetStatus(null == mail.Sent
                    ? $"Nothing sent. [{mail.LastError?.Failure}]: {mail.LastError?.Message}"
                    : $"Sent [{testSubject}] to [{TextOf(userInput)}] at [{mail.Sent:HH:mm:ss}]. Check Now picks it up.");
            }
        }
    }

    // -- Rules ----------------------------------------------------------------

    // Shows the rules after a change. Every change goes through here under the rules' lock, so a check running at the
    // same moment takes them before or after it, never during
    static void ChangeRules(Action change)
    {
        lock (settings.Rules)
        {
            change();

            rulesList.ClearItems();

            foreach (Rule rule in settings.Rules)
                rulesList.AddItem(rule.Name, rule.FromContains, rule.SubjectContains, rule.Attachments, rule.InsideArchive, rule.ConvertTo);
        }
    }

    static void OnRuleSelected(object s, EventArgs e)
    {
        int index = rulesList.SelectedIndex;

        if (0 <= index && index < settings.Rules.Count)
        {
            Rule rule = settings.Rules[index];

            ruleNameInput.Text = rule.Name;
            fromInput.Text = rule.FromContains;
            subjectInput.Text = rule.SubjectContains;
            attachmentsInput.Text = rule.Attachments;
            insideInput.Text = rule.InsideArchive;
            List<string> formats = Formats(rule);
            for (int format = 0; format < convertFormats.Length; format++)
                if (true == formats.Contains(convertFormats[format]))
                    convertList.CheckItem(format);
                else
                    convertList.UncheckItem(format);
            columnNamesCheck.Checked = rule.FirstLineIsColumnNames;
            saveFolderSelector.SetPath(0 == rule.SaveFolder.Length ? inboxFolder : rule.SaveFolder);
        }
    }

    // the formats a rule names, in its own order, each once; a name the converter does not write is left out
    static List<string> Formats(Rule rule)
    {
        return (rule.ConvertTo ?? string.Empty)
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(format => format.Trim().ToLowerInvariant())
            .Where(format => true == convertFormats.Contains(format))
            .Distinct()
            .ToList();
    }

    // the rule the fields describe
    static Rule RuleFromFields()
    {
        return new Rule
        {
            Name = 0 == TextOf(ruleNameInput).Length ? "Rule " + (settings.Rules.Count + 1) : TextOf(ruleNameInput),
            FromContains = TextOf(fromInput),
            SubjectContains = TextOf(subjectInput),
            Attachments = TextOf(attachmentsInput),
            InsideArchive = TextOf(insideInput),
            ConvertTo = string.Join(", ", convertFormats.Where((format, index) => index < convertList.CheckedItems.Count && true == convertList.CheckedItems[index])),
            FirstLineIsColumnNames = columnNamesCheck.Checked,
            SaveFolder = saveFolderSelector.SelectedPath?.Trim() ?? string.Empty
        };
    }

    static void OnAddRule(object s, EventArgs e)
    {
        Rule rule = RuleFromFields();

        // a second rule of the same name would never run, since the first rule that matches takes the message
        if (true == settings.Rules.Any(existing => true == string.Equals(existing.Name, rule.Name, StringComparison.OrdinalIgnoreCase)))
            SetStatus($"A rule named [{rule.Name}] already exists. Update changes it; give a new name to add another.");
        else
        {
            ChangeRules(() => settings.Rules.Add(rule));

            SetStatus($"Added [{rule.Name}]. Save Rules keeps it for next time.");
        }
    }

    static void OnUpdateRule(object s, EventArgs e)
    {
        int index = rulesList.SelectedIndex;

        if (0 <= index && index < settings.Rules.Count)
        {
            Rule rule = RuleFromFields();

            // renaming a rule to another rule's name would leave one of the two never running
            if (true == settings.Rules.Where((existing, at) => at != index).Any(existing => true == string.Equals(existing.Name, rule.Name, StringComparison.OrdinalIgnoreCase)))
                SetStatus($"Another rule is named [{rule.Name}]. Give this one a name of its own.");
            else
            {
                ChangeRules(() => settings.Rules[index] = rule);

                SetStatus($"Updated [{rule.Name}]. Save Rules keeps it for next time.");
            }
        }
        else
            SetStatus("Pick the rule to update first.");
    }

    static void OnRemoveRule(object s, EventArgs e)
    {
        int index = rulesList.SelectedIndex;

        if (0 <= index && index < settings.Rules.Count)
        {
            string name = settings.Rules[index].Name;

            ChangeRules(() => settings.Rules.RemoveAt(index));

            SetStatus($"Removed [{name}]. Save Rules keeps the change.");
        }
        else
            SetStatus("Pick the rule to remove first.");
    }

    static void OnSaveRules(object s, EventArgs e)
    {
        SaveSettings();

        SetStatus($"[{settings.Rules.Count}] rules saved to [{settingsFile}].");
    }

    // -- Show Code ------------------------------------------------------------

    static void OnCopyCode(object s, EventArgs e)
    {
        GPAL.Clipboard
            .WithInput(codeArea.Text)
            .SetClipboard();

        SetStatus("The code is on the clipboard.");
    }

    static void OnSaveCode(object s, EventArgs e)
    {
        string name = SafeName(RuleFromFields().Name);
        string path = Path.Combine(codeFolder, (0 == name.Length ? "rule" : name) + ".cs");

        Directory.CreateDirectory(codeFolder);
        File.WriteAllText(path, codeArea.Text);

        SetStatus($"The code is saved to [{path}].");
    }

    // The rule in the fields as a console program that does what it does, the way Handle does it, shown in a dialog to
    // copy or save. Only the steps the rule uses are written: no sender, subject or attachment test the rule leaves
    // empty, no archive step without InsideArchive, no converter without ConvertTo, and the webhook post only when a
    // variable is named. The password is read from an environment variable, so it is never written into the code
    static void OnShowCode(object s, EventArgs e)
    {
        Rule rule = RuleFromFields();
        string user = TextOf(userInput);
        string webhookVariable = TextOf(webhookVariableInput);
        System.Text.StringBuilder code = new System.Text.StringBuilder();
        List<string> formats = Formats(rule);
        string indent = "        ";

        // a character that ends a line in C#, or any other control character, which neither a string nor a comment
        // may hold as it is
        bool Breaks(char c) => ' ' > c || (char)0x85 == c || (char)0x2028 == c || (char)0x2029 == c;

        // A value as a C# string: verbatim when it holds a backslash or a quote, so a path reads as typed, and escaped
        // character by character when it holds a character that would end the line
        string Quoted(string value) => true == value.Any(Breaks)
            ? "\"" + string.Concat(value.Select(c => true == Breaks(c) || '\\' == c || '"' == c ? $"\\u{(int)c:X4}" : c.ToString())) + "\""
            : 0 <= value.IndexOfAny(new[] { '\\', '"' })
                ? "@\"" + value.Replace("\"", "\"\"") + "\""
                : "\"" + value + "\"";

        // a value inside a // comment, with any character that would end the comment made a space
        string Commented(string value) => string.Concat(value.Select(c => true == Breaks(c) ? " " : c.ToString()));

        // a wildcard as the same regular expression NameMatches uses
        string Pattern(string wildcard) => Quoted("^" + Regex.Escape(wildcard).Replace(@"\*", ".*").Replace(@"\?", ".") + "$");

        void Line(string text) => code.AppendLine(0 == text.Length ? string.Empty : indent + text);

        // the attachments' own names, as Named gives them: no unnamed part, no folder in front
        string named = "email.Attachments.Where(name => false == string.IsNullOrEmpty(name)).Select(name => Path.GetFileName(name))";

        List<string> tests = new List<string>();
        if (0 < rule.FromContains.Length)
            tests.Add($"0 <= (email.From ?? string.Empty).IndexOf({Quoted(rule.FromContains)}, StringComparison.OrdinalIgnoreCase)");
        if (0 < rule.SubjectContains.Length)
            tests.Add($"0 <= (email.Subject ?? string.Empty).IndexOf({Quoted(rule.SubjectContains)}, StringComparison.OrdinalIgnoreCase)");
        if (0 < rule.Attachments.Length)
            tests.Add($"true == {named}.Any(name => true == Regex.IsMatch(name, {Pattern(rule.Attachments)}, RegexOptions.IgnoreCase))");

        string folder = 0 == rule.SaveFolder.Length
            ? "Path.Combine(AppDomain.CurrentDomain.BaseDirectory, \"inbox\")"
            : Quoted(rule.SaveFolder);

        code.AppendLine($"// {Commented(rule.Name)}, written by InboxWatcher, a GPAL Bot of the Week");
        code.AppendLine("// https://gpal.sditx.com/docs/botw/botw-inboxwatcher");
        code.AppendLine("//");
        code.AppendLine("// Reads the unread mail and does what the rule does. A message is marked read once its work is done.");
        code.AppendLine();
        code.AppendLine("using System;");
        code.AppendLine("using System.Collections.Generic;");
        code.AppendLine("using System.IO;");
        code.AppendLine("using System.Linq;");
        code.AppendLine("using System.Text.RegularExpressions;");
        code.AppendLine("using GenerallyPositive;");
        code.AppendLine("using static GenerallyPositive.Enums;");
        code.AppendLine();
        code.AppendLine("static class Program");
        code.AppendLine("{");
        code.AppendLine("    static void Main()");
        code.AppendLine("    {");
        Line("// The servers from GPALMail.yaml, the file InboxWatcher's Save Account writes. It is read from the folder the");
        Line("// program runs in, which under dotnet run is the project folder, so copy it there");
        Line("IGPALMail mail = GPAL.Mail.ToGPALObject();");
        Line("mail.LoadSettings();");
        Line(string.Empty);
        Line("// The password from an environment variable, so it is never in the code. Set it once with");
        Line("// setx INBOXWATCHER_PASSWORD yourpassword, and a console opened after that sees it");
        Line("mail.WithCredentials((Credentials)GPAL.CredentialsFor(CredentialServiceType.None)");
        Line($"    .WithUsername({Quoted(user)})");
        Line("    .WithPassword(Environment.GetEnvironmentVariable(\"INBOXWATCHER_PASSWORD\") ?? string.Empty));");
        Line(string.Empty);
        Line("// the newest fifty unread messages a run, as the bot reads them");
        Line("mail.WithUnreadOnly(true)");
        Line("    .WithMaxMessages(50)");
        Line("    .Receive(out List<ReceivedEmail> messages);");
        Line(string.Empty);
        Line("// LastError says why when no mail could be read, such as no servers in GPALMail.yaml or a refused login");
        Line("if (null != mail.LastError)");
        Line("    GPAL.PublishSimpleEvent(GPALEventType.WARNING, $\"The mail could not be read: [{mail.LastError.Failure}]\");");
        Line(string.Empty);
        Line("foreach (ReceivedEmail email in messages)");
        Line("{");
        indent += "    ";

        if (0 < tests.Count)
        {
            Line($"// {Commented(string.Join(", ", new[] { 0 < rule.FromContains.Length ? "from " + rule.FromContains : null, 0 < rule.SubjectContains.Length ? "subject " + rule.SubjectContains : null, 0 < rule.Attachments.Length ? "an attachment named " + rule.Attachments : null }.Where(part => null != part)))}");
            Line("if (" + string.Join(Environment.NewLine + indent + "    && ", tests) + ")");
            Line("{");
            indent += "    ";
        }

        Line("// a folder for each message, named for when it arrived and its subject, the characters Windows refuses made a");
        Line("// space and the subject kept short, holding every attachment");
        Line("string subject = string.Join(\" \", (email.Subject ?? string.Empty).Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();");
        Line("subject = 40 < subject.Length ? subject.Substring(0, 40).Trim() : subject;");
        Line($"string folder = Path.Combine({folder}, $\"{{email.Received:yyyy-MM-dd_HHmmss}} {{(0 == subject.Length ? \"no subject\" : subject)}}\");");
        Line("email.SaveTo((GPALFile)folder);");

        if (0 < rule.InsideArchive.Length || 0 < formats.Count)
        {
            string attachments = 0 < rule.Attachments.Length
                ? $"{named}.Where(name => true == Regex.IsMatch(name, {Pattern(rule.Attachments)}, RegexOptions.IgnoreCase))"
                : named;

            Line(string.Empty);
            Line($"foreach (string name in {attachments})");
            Line("{");
            indent += "    ";
            Line("string attachment = Path.Combine(folder, name);");
            Line("List<string> files = new List<string> { attachment };");

            if (0 < rule.InsideArchive.Length)
            {
                Line(string.Empty);
                Line($"// an archive is a folder to GPALFile: the {Commented(rule.InsideArchive)} files in it come out beside it, into the message's folder");
                Line($"GPALFile inside = (GPALFile)GPAL.File.WithFileName(Path.Combine(attachment, {Quoted(rule.InsideArchive)})).WithOverwriteFile(true);");
                Line("if (0 < inside.Filenames.Count)");
                Line("{");
                Line("    inside.CopyTo(folder);");
                Line("    files = inside.Filenames.Select(file => Path.Combine(folder, Path.GetFileName(file))).ToList();");
                Line("}");
            }

            if (0 < formats.Count)
            {
                Line(string.Empty);
                Line($"// each file, into {string.Join(", ", formats)}, one SaveTo each; the converter reads the format from the extension");
                Line("foreach (string file in files)");
                Line("    GPAL.Converter");
                Line("        .WithInput((GPALFile)file)");
                Line($"        .WithFirstLineHasColumnNames({(true == rule.FirstLineIsColumnNames ? "true" : "false")})");
                for (int format = 0; format < formats.Count; format++)
                    Line($"        .SaveTo((GPALFile)Path.ChangeExtension(file, \"{formats[format]}\")){(formats.Count - 1 == format ? ";" : string.Empty)}");
            }

            indent = indent.Substring(4);
            Line("}");
        }

        Line(string.Empty);
        Line("// after the work, so a run that fails partway leaves the message unread");
        Line("email.MarkAsRead();");

        if (0 < webhookVariable.Length)
        {
            Line(string.Empty);
            Line($"// the webhook from the {Commented(webhookVariable)} environment variable");
            Line("GPAL.Notify");
            Line($"    .WithWebhook((Credentials)GPAL.CredentialsFor(CredentialServiceType.StaticKey).WithKeyFromEnv({Quoted(webhookVariable)}))");
            Line($"    .Send({Quoted(rule.Name)} + $\": {{email.Subject}} from {{email.From}}, in {{folder}}\");");
        }

        if (0 < tests.Count)
        {
            indent = indent.Substring(4);
            Line("}");
        }

        indent = indent.Substring(4);
        Line("}");
        code.AppendLine("    }");
        code.AppendLine("}");

        codeArea.Text = code.ToString();

        GPAL.Form
            .WithTitle($"Show Code: {rule.Name}")
            .WithWidth(760)
            .WithFormControl(codeHelp)
            .WithFormControl(codeArea)
            .WithFormControl(copyCodeBtn)
            .WithFormControl(saveCodeBtn)
            .WithFormControl(closeCodeBtn)
            .ShowDialog();
    }

    // -- Account load and save ------------------------------------------------

    static void LoadAccount()
    {
        // GPALMail.yaml, written by SaveSettings. The mail object reports what it loaded, so the tab can show it.
        mail.LoadSettings();

        smtpHostInput.Text = mail.SMTPServerName;
        smtpPortInput.Text = mail.SMTPPortNum.ToString();
        imapHostInput.Text = mail.IMAPServerName;
        imapPortInput.Text = mail.IMAPPortNum.ToString();

        // the from address is who this account sends as, which here is the same as who it logs in as
        userInput.Text = mail.FromAddress;

        LoadTestAccount();

        if (string.IsNullOrWhiteSpace(mail.IMAPServerName))
            SetStatus("No account yet. Fill in the Account tab, or take a test account.");
        else if (string.IsNullOrEmpty(passwordInput.Text))
            SetStatus($"Account loaded for [{mail.FromAddress}]. The password is never saved, so type it in.");
        else
            SetStatus($"Test account [{userInput.Text}] is back. Send Test Message, then Check Now.");
    }

    // A kept test account, which is the only password this program writes down. Used only when the saved servers are
    // that account's, so a real account saved after it is not replaced by it
    static void LoadTestAccount()
    {
        if (true == File.Exists(testAccountFile))
        {
            TestAccount kept = new TestAccount();

            GPAL.Converter
                .WithInput((GPALFile)testAccountFile)
                .SaveTo(ref kept);

            if (true == string.IsNullOrWhiteSpace(mail.FromAddress) || true == string.Equals(mail.FromAddress, kept.User, StringComparison.OrdinalIgnoreCase))
            {
                userInput.Text = kept.User;
                passwordInput.Text = kept.Pass;
            }
        }
    }

    static void OnShowPassword(object s, EventArgs e)
    {
        passwordInput.ShowPassword = false == passwordInput.ShowPassword;
        showPasswordBtn.Text = true == passwordInput.ShowPassword ? "👁 Hide" : "👁 Show";
    }

    // Off the form's thread, since it waits for a check to finish, and a check updates the form as it goes
    static void OnSaveAccount(object s, EventArgs e)
    {
        // servers, ports and the from address. the credential is not part of a settings file, so the password
        // does not reach disk. Not in the middle of a check
        lock (mailLock)
        {
            ApplyAccount();
            mail.SaveSettings();
        }

        SetStatus("Servers saved to GPALMail.yaml. The password stayed out of it.");
    }

    // The tab's servers and login, applied to the mail object on every call, so an edited account is used at once
    static void ApplyAccount()
    {
        ICredentials login = GPAL.CredentialsFor(CredentialServiceType.None)
            .WithUsername(TextOf(userInput))
            .WithPassword(passwordInput.Text ?? string.Empty)
            .ToGPALObject();

        mail.WithSMTPServerName(TextOf(smtpHostInput))
            .WithSMTPPortNum(PortFrom(smtpPortInput, 587))
            .WithAcceptAnyCertificate(anyCertBox.Checked)
            .WithIMAPServerName(TextOf(imapHostInput))
            .WithIMAPPortNum(PortFrom(imapPortInput, 993))
            .WithCredentials(login);

        // an address is parsed when it is set, so an empty one is left unset
        if (0 < TextOf(userInput).Length)
            mail.WithFromEmailAddress(TextOf(userInput));
    }

    static int PortFrom(GPALInput input, int fallback)
    {
        int retVal;

        if (false == int.TryParse(TextOf(input), out retVal))
            retVal = fallback;

        return retVal;
    }

    static bool AccountIsReady()
    {
        bool retVal = false == string.IsNullOrWhiteSpace(userInput.Text)
                   && false == string.IsNullOrWhiteSpace(passwordInput.Text);

        if (false == retVal)
            SetStatus("Fill in the Account tab first, or press Get Test Account.");

        return retVal;
    }

    // -- Test account ---------------------------------------------------------

    static void OnTestAccount(object s, EventArgs e)
    {
        EtherealAccount test;

        // the new account changes the mail object, so not in the middle of a check
        lock (mailLock)
            test = AskEtherealForAnAccount();

        SetStatus(null == test
            ? "Ethereal did not answer. Try again, or fill the account in by hand."
            : $"Test account [{test.user}], saved for next time. Mail to it is readable at [{test.web}]. Send Test Message, then Check Now.");
    }

    // Ethereal creates an account on request with real SMTP and IMAP and delivers nothing outside. An unused account
    // stops authenticating, which is what a refused login here means.
    static EtherealAccount AskEtherealForAnAccount()
    {
        EtherealAccount test = GPAL.RESTClient
            .WithAPIBase("https://api.nodemailer.com/")
            .WithEndpoint("user")
            .WithParameters(new { requestor = "GPAL", version = "1.0" })
            .WithHttpMethod("POST")
            .Execute<EtherealAccount>();

        if (null != test && null != test.smtp)
        {
            smtpHostInput.Text = test.smtp.host;
            smtpPortInput.Text = test.smtp.port.ToString();
            imapHostInput.Text = test.imap.host;
            imapPortInput.Text = test.imap.port.ToString();
            userInput.Text = test.user;
            passwordInput.Text = test.pass;

            // the account and the servers, written where the next run looks for them
            GPAL.Converter
                .WithInput(new TestAccount { User = test.user, Pass = test.pass })
                .SaveTo((GPALFile)testAccountFile);

            ApplyAccount();
            mail.SaveSettings();
        }
        else
            test = null;

        return test;
    }

    static void OnAbout(object s, EventArgs e)
    {
        GPAL.Form
            .WithTitle("About InboxWatcher")
            .WithWidth(420)
            .WithFormControl(aboutText)
            .WithFormControl(GPAL.SpacerFor(12))
            .WithFormControl(moreBotsLink)
            .WithFormControl(GPAL.SpacerFor(12))
            .WithFormControl(aboutOkBtn)
            .ShowDialog();
    }

    static void SetStatus(string message) => statusBar.Text = message;
}
