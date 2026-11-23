// =============================================================================
// GPAL Bot of the Week - ChannelManager
//
// Lists every video and playlist on your YouTube channel, and replaces a video
// with a new file while keeping its title, description, tags, category,
// privacy, thumbnail and its place in every playlist. Uploads new videos, with
// titles and descriptions an AI can write. Adds and removes videos
// from playlists, deletes videos, and exports the video list to CSV, JSON,
// XML, YAML, HTML or tab delimited. Manages any number of channels: each one's
// credentials are kept under its name in youtube-channels.yaml beside the exe
// via GPAL.Converter, and you pick the channel to manage when it starts.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using GenerallyPositive;
using GenerallyPositive.Browser;
using GenerallyPositive.GPALForm;
using static GenerallyPositive.Enums;

// ── Saved channels (serialized to/from YAML by GPAL.Converter) ──────────────

// one channel: its name and ID as YouTube gives them, and the three values that connect to it
class YouTubeChannel
{
    public string Name         { get; set; } = string.Empty;
    public string ChannelId    { get; set; } = string.Empty;
    public string ClientId     { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public string AIPrompt     { get; set; } = string.Empty;    // Write with AI's prompt for this channel
}

// youtube-channels.yaml: every channel connected so far, the one used last, and the AI that writes upload details
class ChannelSettings
{
    public List<YouTubeChannel> Channels    { get; set; } = new List<YouTubeChannel>();
    public string               LastChannel { get; set; } = string.Empty;
    public string               AIProvider  { get; set; } = string.Empty;
    public string               AIModel     { get; set; } = string.Empty;
    public string               AIKey       { get; set; } = string.Empty;
}

// one file to upload, with the title and description it goes up with
class UploadJob
{
    public string Path        { get; set; } = string.Empty;
    public string Title       { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

// the youtube-creds.yaml the other GPAL YouTube programs save, which Import settings reads
class YouTubeCreds
{
    public string ClientId     { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
}

// ── YouTube's answer to channels?part=snippet&mine=true, the parts read ──────

class ChannelList    { public List<ChannelItem> items { get; set; } }
class ChannelItem    { public string id { get; set; } public ChannelSnippet snippet { get; set; } }
class ChannelSnippet { public string title { get; set; } }

// ── Program ───────────────────────────────────────────────────────────────────

static class Program
{
    static readonly string ChannelsFile = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "youtube-channels.yaml");

    // the last item in the channel list, for adding a channel
    const string NewChannelItem = "➕ New channel";

    // ── Working state ─────────────────────────────────────────────────────────
    static bool                      credConnected { get; set; }
    static ChannelSettings           settings      { get; set; } = new ChannelSettings();
    static string                    channelName   { get; set; } = string.Empty;
    static IAllowFormSettingsAndActions credForm   { get; set; }
    static IAllowFormSettingsAndActions mainForm   { get; set; }
    static ICredentials              credentials   { get; set; }
    static List<YouTubeVideoInfo>    videos        { get; set; } = new List<YouTubeVideoInfo>();
    static List<YouTubePlaylistInfo> playlists     { get; set; } = new List<YouTubePlaylistInfo>();
    static List<YouTubePlaylistItem> playlistItems { get; set; } = new List<YouTubePlaylistItem>();
    static int                       pickedIndex   { get; set; } = -1;
    static bool                      exportChosen  { get; set; }
    static bool                      chaptersChosen { get; set; }

    // ── Export formats: label, DataFormat, file extension, SaveFileDialog filter ──
    static readonly DataFormat[] formatTypes   = { DataFormat.CSV, DataFormat.JSON, DataFormat.XML, DataFormat.YAML, DataFormat.HTML, DataFormat.TAB };
    static readonly string[]     formatExts    = { ".csv", ".json", ".xml", ".yaml", ".html", ".txt" };
    static readonly string[]     formatFilters =
    {
        "CSV files (*.csv)|*.csv",
        "JSON files (*.json)|*.json",
        "XML files (*.xml)|*.xml",
        "YAML files (*.yaml)|*.yaml",
        "HTML files (*.html)|*.html",
        "Text files (*.txt)|*.txt",
    };

    // ── Step 1: credentials ───────────────────────────────────────────────────

    // the saved channels, then New channel
    static GPALComboBox channelCombo = GPAL.ComboBox
        .WithItems(NewChannelItem)
        .WithSelectedIndex(0)
        .WithCallback<EventHandler>((s, e) => ShowChannel(channelCombo.SelectedIndex))
        .WithName("channel")
        .ToGPALObject();

    static GPALInput clientIdInput     = GPAL.Input.WithName("clientId").ToGPALObject();
    static GPALInput clientSecretInput = GPAL.Input.WithName("clientSecret").ToGPALObject();
    static GPALInput refreshTokenInput = GPAL.Input.WithName("refreshToken").ToGPALObject();

    // not the default button: the default closes the form, and a failed connect should leave it open to try again
    static GPALButton connectBtn = GPAL.Button
        .WithName("connect")
        .WithText("🔑 Connect")
        .WithCallback<EventHandler>(OnConnect)
        .ToGPALObject();

    // fills the three values from another GPAL YouTube program's saved settings
    static GPALButton importBtn = GPAL.Button
        .WithName("import")
        .WithText("📥 Import settings...")
        .WithCallback<EventHandler>(OnImport)
        .ToGPALObject();

    static GPALButton removeChannelBtn = GPAL.Button
        .WithName("removeChannel")
        .WithText("🗑️ Remove Channel")
        .WithCallback<EventHandler>(OnRemoveChannel)
        .ToGPALObject();

    static GPALButton cancelCredBtn = GPAL.Button
        .WithName("cancelCred")
        .WithText("🚪 Cancel")
        .WithCallback<EventHandler>((s, e) => HideParentForm(s))
        .ToGPALObject();

    // ── Step 1: setup guide, a tab per step ───────────────────────────────────

    // Google sends no scope when it refreshes a token, so one refresh token made with this scope works in every
    // GPAL YouTube program
    const string YouTubeScope = "https://www.googleapis.com/auth/youtube";
    const string PlaygroundRedirect = "https://developers.google.com/oauthplayground";

    static GPALTab connectTab = GPAL.Tab.WithName("connectTab").WithText("Connect").ToGPALObject();
    static GPALTab apiTab = GPAL.Tab.WithName("apiTab").WithText("1. Turn on the API").ToGPALObject();
    static GPALTab authTab = GPAL.Tab.WithName("authTab").WithText("2. Sign-in screen").ToGPALObject();
    static GPALTab clientTab = GPAL.Tab.WithName("clientTab").WithText("3. Client ID and secret").ToGPALObject();
    static GPALTab tokenTab = GPAL.Tab.WithName("tokenTab").WithText("4. Refresh token").ToGPALObject();

    static GPALTextArea connectHelp = (GPALTextArea)GPAL.TextArea
        .WithReadOnly(true)
        .WithHeight(150)
        .WithName("connectHelp")
        .WithText("Pick a channel and Connect, or pick New channel and enter the three values from your Google Cloud project. Each channel is saved under its YouTube name to youtube-channels.yaml beside this program." + Environment.NewLine +
                  "A new channel can use the Client ID and Client Secret of one already saved: pick that channel, then New channel, and they stay. It still needs its own Refresh Token, made while signed in as the new channel (tab 4)." + Environment.NewLine +
                  "Already set up another GPAL YouTube program? Import settings reads the settings file it saved (VideoReplacer, YouTubeUploader, YouTubeTest, YouTubeSearchTester)." + Environment.NewLine +
                  "First time, or Google turned the token down? Tabs 1 to 4 walk through getting all three.")
        .ToGPALObject();

    static GPALTextArea apiSteps = (GPALTextArea)GPAL.TextArea
        .WithReadOnly(true)
        .WithHeight(150)
        .WithName("apiSteps")
        .WithText("1. Click the button below and sign in with the Google account that owns or manages your YouTube channel." + Environment.NewLine +
                  "2. Pick your project in the project list at the top of the page, or click New project and make one." + Environment.NewLine +
                  "3. On the YouTube Data API v3 page click Enable. If it shows Manage instead, the API is already on.")
        .ToGPALObject();

    static GPALButton openApiBtn = GPAL.Button
        .WithName("openApi")
        .WithText("🌐 Open YouTube Data API v3")
        .WithCallback<EventHandler>((s, e) => OpenUrl("https://console.cloud.google.com/apis/library/youtube.googleapis.com"))
        .ToGPALObject();

    static GPALTextArea authSteps = (GPALTextArea)GPAL.TextArea
        .WithReadOnly(true)
        .WithHeight(250)
        .WithName("authSteps")
        .WithText("1. Open Google Auth Platform. If it shows Get started, click it. If it shows the Overview, that part is done; go to step 4." + Environment.NewLine +
                  "2. App Information: any app name, and your email as the user support email. Next." + Environment.NewLine +
                  "3. Audience: External. Next. Contact Information: your email. Next. Agree to the user data policy, Continue, then Create." + Environment.NewLine +
                  "4. Open Audience and click Publish app, then Confirm. While the app is in Testing, Google blocks every account not listed under Test users (\"Access blocked: ... has not completed the Google verification process\"), and the refresh token stops working after 7 days." + Environment.NewLine +
                  "5. Open Data Access, click Add or remove scopes, paste the scope below under Manually add scopes, click Add to table, then Update, then Save.")
        .ToGPALObject();

    static GPALInput authScopeInput = GPAL.Input.WithReadOnly().WithName("authScope").WithText(YouTubeScope).ToGPALObject();

    static GPALButton openAuthBtn = GPAL.Button
        .WithName("openAuth")
        .WithText("🌐 Open Google Auth Platform")
        .WithCallback<EventHandler>((s, e) => OpenUrl("https://console.cloud.google.com/auth/overview"))
        .ToGPALObject();

    static GPALButton openAudienceBtn = GPAL.Button
        .WithName("openAudience")
        .WithText("🌐 Open Audience")
        .WithCallback<EventHandler>((s, e) => OpenUrl("https://console.cloud.google.com/auth/audience"))
        .ToGPALObject();

    static GPALButton openDataAccessBtn = GPAL.Button
        .WithName("openDataAccess")
        .WithText("🌐 Open Data Access")
        .WithCallback<EventHandler>((s, e) => OpenUrl("https://console.cloud.google.com/auth/scopes"))
        .ToGPALObject();

    static GPALTextArea clientSteps = (GPALTextArea)GPAL.TextArea
        .WithReadOnly(true)
        .WithHeight(220)
        .WithName("clientSteps")
        .WithText("1. Open Clients and click Create client. Application type: Web application. Any name." + Environment.NewLine +
                  "2. Under Authorized redirect URIs click Add URI, paste the URI below, then click Create." + Environment.NewLine +
                  "3. Copy the Client ID and the Client Secret into the Connect tab right away, or click Download JSON. Google shows the secret only this once." + Environment.NewLine +
                  "4. Lost the secret? Open the client, click Add secret and use the new one, then disable and delete the old one." + Environment.NewLine + Environment.NewLine +
                  "A new redirect URI can take from 5 minutes to a few hours to start working. If the Playground says redirect_uri_mismatch, wait and try again.")
        .ToGPALObject();

    static GPALInput redirectInput = GPAL.Input.WithReadOnly().WithName("redirect").WithText(PlaygroundRedirect).ToGPALObject();

    static GPALButton openClientsBtn = GPAL.Button
        .WithName("openClients")
        .WithText("🌐 Open Clients")
        .WithCallback<EventHandler>((s, e) => OpenUrl("https://console.cloud.google.com/auth/clients"))
        .ToGPALObject();

    static GPALTextArea tokenSteps = (GPALTextArea)GPAL.TextArea
        .WithReadOnly(true)
        .WithHeight(270)
        .WithName("tokenSteps")
        .WithText("1. Open the OAuth 2.0 Playground. Click the gear at the top right, tick Use your own OAuth credentials, and paste the Client ID and Client Secret. Leave OAuth flow on Server-side and Access type on Offline. Close the gear." + Environment.NewLine +
                  "2. Under Step 1 on the left, paste the scope below into Input your own scopes, then click Authorize APIs." + Environment.NewLine +
                  "3. Pick your Google account. When Google asks which account or brand account to use, pick the YouTube channel's name, not your own. That choice decides which channel the token works on." + Environment.NewLine +
                  "4. At \"Google hasn't verified this app\" click Advanced, then Go to [your app name] (unsafe), then Continue. It is your own app." + Environment.NewLine +
                  "5. Back in the Playground click Exchange authorization code for tokens, and copy the Refresh token into the Connect tab. If the field looks empty, close and reopen Step 2." + Environment.NewLine + Environment.NewLine +
                  "One token made with this scope works in every GPAL YouTube program.")
        .ToGPALObject();

    static GPALInput tokenScopeInput = GPAL.Input.WithReadOnly().WithName("tokenScope").WithText(YouTubeScope).ToGPALObject();

    static GPALButton openPlaygroundBtn = GPAL.Button
        .WithName("openPlayground")
        .WithText("🌐 Open OAuth 2.0 Playground")
        .WithCallback<EventHandler>((s, e) => OpenUrl("https://developers.google.com/oauthplayground/"))
        .ToGPALObject();

    // ── Videos tab ────────────────────────────────────────────────────────────

    static GPALTab videosTab = GPAL.Tab.WithName("videosTab").WithText("Videos").ToGPALObject();

    static GPALListView videosList = GPAL.ListView
        .WithColumns("Title", "Published", "Privacy", "Video ID")
        .WithColumnWidth(420, 90, 80, -2)
        .WithView(ListViewMode.Details)
        .WithFullRowSelect(true)
        .WithHeight(400)
        .WithName("videos")
        .ToGPALObject();

    static GPALButton refreshVideosBtn = GPAL.Button
        .WithName("refreshVideos")
        .WithText("🔄 Refresh")
        .WithCallback<EventHandler>((s, e) => refreshVideosBtn.Enabled = false)
        .WithCallbackOffUIThread<EventHandler>(OnRefreshVideos)
        .ToGPALObject();

    static GPALButton replaceBtn = GPAL.Button
        .WithName("replace")
        .WithText("🔁 Replace Video...")
        .WithCallback<EventHandler>((s, e) => replaceBtn.Enabled = false)
        .WithCallbackOffUIThread<EventHandler>(OnReplace)
        .ToGPALObject();

    static GPALButton addToPlaylistBtn = GPAL.Button
        .WithName("addToPlaylist")
        .WithText("➕ Add to Playlist...")
        .WithCallback<EventHandler>((s, e) => addToPlaylistBtn.Enabled = false)
        .WithCallbackOffUIThread<EventHandler>(OnAddToPlaylist)
        .ToGPALObject();

    static GPALButton removeFromPlaylistBtn = GPAL.Button
        .WithName("removeFromPlaylist")
        .WithText("➖ Remove from Playlist...")
        .WithCallback<EventHandler>((s, e) => removeFromPlaylistBtn.Enabled = false)
        .WithCallbackOffUIThread<EventHandler>(OnRemoveFromPlaylist)
        .ToGPALObject();

    static GPALButton deleteBtn = GPAL.Button
        .WithName("delete")
        .WithText("🗑 Delete Video")
        .WithCallback<EventHandler>((s, e) => deleteBtn.Enabled = false)
        .WithCallbackOffUIThread<EventHandler>(OnDelete)
        .ToGPALObject();

    static GPALButton exportBtn = GPAL.Button
        .WithName("export")
        .WithText("💾 Export...")
        .WithCallback<EventHandler>(OnExport)
        .ToGPALObject();

    static GPALButton exportChaptersBtn = GPAL.Button
        .WithName("exportChapters")
        .WithText("📑 Export Chapters...")
        .WithCallback<EventHandler>(OnExportChapters)
        .ToGPALObject();

    // opens the Upload Videos window; the uploads run after it closes
    static GPALButton uploadBtn = GPAL.Button
        .WithName("upload")
        .WithText("⬆️ Upload Videos...")
        .WithCallback<EventHandler>(OnUploadVideos)
        .ToGPALObject();

    // the video buttons in two columns: upload, replace and playlists on the left, delete and export on the right.
    // Four rows of 26 pixels, a button and its two margins each; GPAL 1.1.1 and earlier measure a splitter 3 pixels
    // short per row and cut the last button off, so the height is given until GPAL 1.1.2 is out
    static GPALSplitter videoActions = (GPALSplitter)GPAL.Splitter.HSplit(0.5)
        .WithHeight(104)
        .WithFormControl(uploadBtn)
        .WithFormControl(replaceBtn)
        .WithFormControl(addToPlaylistBtn)
        .WithFormControl(removeFromPlaylistBtn)
        .WithFormControl(GPAL.SplitRight)
        .WithFormControl(deleteBtn)
        .WithFormControl(exportBtn)
        .WithFormControl(exportChaptersBtn)
        .ToGPALObject();

    // the Connect window again, to open another saved channel or add one
    static GPALButton switchChannelBtn = GPAL.Button
        .WithName("switchChannel")
        .WithText("🔀 Switch Channel...")
        .WithCallback<EventHandler>(OnSwitchChannel)
        .ToGPALObject();

    static GPALButton aboutBtn = GPAL.Button
        .WithName("about")
        .WithText("ℹ About")
        .WithCallback<EventHandler>(OnAbout)
        .ToGPALObject();

    // ── Playlists tab ─────────────────────────────────────────────────────────

    static GPALTab playlistsTab = GPAL.Tab.WithName("playlistsTab").WithText("Playlists").ToGPALObject();

    static GPALListView playlistsList = GPAL.ListView
        .WithColumns("Title", "Items", "Privacy", "Playlist ID")
        .WithColumnWidth(420, 60, 80, -2)
        .WithView(ListViewMode.Details)
        .WithFullRowSelect(true)
        .WithHeight(180)
        .WithName("playlists")
        .WithCallbackOffUIThread<EventHandler>(OnPlaylistSelected)
        .ToGPALObject();

    static GPALButton refreshPlaylistsBtn = GPAL.Button
        .WithName("refreshPlaylists")
        .WithText("🔄 Refresh")
        .WithCallback<EventHandler>((s, e) => refreshPlaylistsBtn.Enabled = false)
        .WithCallbackOffUIThread<EventHandler>(OnRefreshPlaylists)
        .ToGPALObject();

    static GPALButton removeDeletedBtn = GPAL.Button
        .WithName("removeDeleted")
        .WithText("🧹 Remove Deleted Videos...")
        .WithCallback<EventHandler>((s, e) => removeDeletedBtn.Enabled = false)
        .WithCallbackOffUIThread<EventHandler>(OnRemoveDeleted)
        .ToGPALObject();

    static GPALListView playlistItemsList = GPAL.ListView
        .WithColumns("Position", "Title", "Video ID")
        .WithColumnWidth(70, 480, -2)
        .WithView(ListViewMode.Details)
        .WithFullRowSelect(true)
        .WithHeight(200)
        .WithName("playlistItems")
        .ToGPALObject();

    // ── GPAL Log tab ──────────────────────────────────────────────────────────

    static GPALTab logTab = GPAL.Tab.WithName("logTab").WithText("GPAL Log").ToGPALObject();

    // The Log tab shows every event GPAL publishes. The status strip shows only what is happening now.
    static GPALTextArea logArea = (GPALTextArea)GPAL.TextArea.WithReadOnly(true).WithHeight(420).WithName("log");

    static GPALCheckbox debugCheck = GPAL.CheckboxFor("Include DEBUG in the log", false);

    static GPALButton clearLogBtn = GPAL.Button
        .WithName("clearLog")
        .WithText("🧹 Clear Log")
        .WithCallback<EventHandler>((s, e) => logArea.Text = string.Empty)
        .ToGPALObject();

    // ── How To tab ────────────────────────────────────────────────────────────

    static GPALTab howToTab = GPAL.Tab.WithName("howToTab").WithText("How To").ToGPALObject();

    // The steps, so the bot explains itself.
    static readonly string[] instructions =
    {
        "ChannelManager runs your YouTube channels from one window: it lists the videos and",
        "playlists, uploads, replaces, deletes, sorts videos into playlists and exports.",
        "",
        "1.  Connect. The first window asks which channel. Pick a saved one and press Connect,",
        "    or pick New channel and enter the Client ID, Client Secret and Refresh Token from",
        "    your Google Cloud project. Its tabs 1 to 4 walk through getting all three.",
        "",
        "    Already set up another GPAL YouTube program? Import settings reads the settings",
        "    file it saved. Remove Channel takes a channel off the saved list; the channel on",
        "    YouTube is not touched.",
        "",
        "    Every channel connected is saved under its YouTube name to youtube-channels.yaml",
        "    beside this exe, with the AI settings from Upload Videos. Keep that file to",
        "    yourself: it holds what signs in to your channels and your AI key.",
        "",
        "2.  Switch Channel, on the Videos tab, opens the Connect window again to pick another",
        "    saved channel or add one. Cancel leaves the open channel as it was.",
        "",
        "3.  Videos tab. Refresh reads the list again. Pick a video, then:",
        "",
        "        Replace Video        pick the new file. The new video gets the old one's title,",
        "                             description, tags, category, privacy, thumbnail and its",
        "                             place in each of your playlists, and you are asked",
        "                             whether to delete the old one.",
        "        Add to Playlist      puts the video in the playlist you pick",
        "        Remove from          takes it out of the playlist you pick",
        "        Playlist",
        "        Delete Video         deletes it from YouTube, after you say yes. This cannot be",
        "                             undone.",
        "",
        "    A chapters file beside the new file, one m:ss Name per line, takes the place of the",
        "    chapter lines in the description, or goes at the end when it has none:",
        "    chapters.txt, then <new file name>.chapters.txt, then any other *chapters*.txt.",
        "",
        "    These work on the whole list, nothing picked:",
        "",
        "        Export               every video, with the fields you tick, as CSV, JSON, XML,",
        "                             YAML, HTML or tab delimited",
        "        Export Chapters      each video whose title holds the text you give gets a",
        "                             <title>.chapters.txt, in a folder you pick, holding the",
        "                             chapter lines from its description",
        "",
        "4.  Upload Videos opens a window of its own. Add Files picks the videos; each starts",
        "    with its file name as its title. Pick one to change its title and description.",
        "    The tags, category, privacy and playlist apply to every video. Private keeps a new",
        "    video out of sight until you have checked it on YouTube.",
        "    A <video name>.chapters.txt beside a video is added to the end of its description.",
        "",
        "    Upload checks every title and description against what YouTube takes, then closes",
        "    the window; the uploads run after it, and the status bar follows them.",
        "",
        "    Write with AI fills in, and replaces, the title and description of every video in",
        "    the list. Its tab takes the provider (Anthropic, OpenAI or xAI), the model, your",
        "    API key, and a prompt that each channel keeps for itself. The AI gets the prompt,",
        "    the file name, and a notes file beside the video when there is one: the video's",
        "    name with .txt or .md, so Concert 2019.txt for Concert 2019.mp4. Check each title",
        "    and description before you upload.",
        "",
        "5.  Playlists tab. Pick a playlist to see the videos in it. Refresh reads the",
        "    playlists again.",
        "",
        "    Remove Deleted Videos looks through every playlist for videos YouTube shows as",
        "    Deleted video, lists them, and takes them out once you say yes. Private video",
        "    entries stay: those are usually another channel's video made private, which can",
        "    come back.",
        "",
        "6.  GPAL Log tab. Everything GPAL reports while it works. When the status bar says to",
        "    see the GPAL Log, this is where the reason is."
    };

    // the steps, filled in when the main window is built
    static GPALTextArea instructionArea = (GPALTextArea)GPAL.TextArea
        .WithReadOnly(true)
        .WithHeight(560)
        .WithName("instructions");

    // ── Status strip ──────────────────────────────────────────────────────────

    static GPALStatusStrip statusBar = GPAL.StatusStrip
        .WithText("Ready")
        .WithName("status")
        .ToGPALObject();

    // ── Playlist picker ───────────────────────────────────────────────────────

    static GPALComboBox playlistCombo = GPAL.ComboBox
        .WithName("playlistPick")
        .ToGPALObject();

    // the default button closes the dialog; its callback takes the choice first
    static GPALButton pickOkBtn = GPAL.Button
        .Default
        .WithName("pickOk")
        .WithText("OK")
        .WithWidth(50)
        .WithCallback<EventHandler>((s, e) => pickedIndex = playlistCombo.SelectedIndex)
        .ToGPALObject();

    static GPALButton pickCancelBtn = GPAL.Button
        .WithName("pickCancel")
        .WithText("Cancel")
        .WithCallback<EventHandler>((s, e) => HideParentForm(s))
        .ToGPALObject();

    // ── Export ────────────────────────────────────────────────────────────────

    static GPALCheckbox cbTitle       = GPAL.Checkbox.WithChecked(true).WithText("Title").ToGPALObject();
    static GPALCheckbox cbDescription = GPAL.Checkbox.WithChecked(true).WithText("Description").ToGPALObject();
    static GPALCheckbox cbTags        = GPAL.Checkbox.WithChecked(true).WithText("Tags").ToGPALObject();
    static GPALCheckbox cbUrl         = GPAL.Checkbox.WithChecked(true).WithText("URL").ToGPALObject();
    static GPALCheckbox cbPublished   = GPAL.Checkbox.WithChecked(true).WithText("Published Date").ToGPALObject();
    static GPALCheckbox cbVideoId     = GPAL.Checkbox.WithChecked(false).WithText("Video ID").ToGPALObject();

    static GPALComboBox formatCombo = GPAL.ComboBox
        .WithItems("CSV", "JSON", "XML", "YAML", "HTML", "Tab Delimited")
        .WithSelectedIndex(0)
        .WithName("format")
        .ToGPALObject();

    // the default button closes the dialog; its callback says the export goes ahead
    static GPALButton exportSaveBtn = GPAL.Button
        .Default
        .WithName("exportSave")
        .WithText("💾 Save...")
        .WithCallback<EventHandler>((s, e) => exportChosen = true)
        .ToGPALObject();

    static GPALButton exportCancelBtn = GPAL.Button
        .WithName("exportCancel")
        .WithText("Cancel")
        .WithCallback<EventHandler>((s, e) => HideParentForm(s))
        .ToGPALObject();

    // ── Export chapters ───────────────────────────────────────────────────────

    static GPALInput chaptersMatchInput = GPAL.Input.WithName("chaptersMatch").WithText("full show").ToGPALObject();

    // the default button closes the dialog; its callback says the export goes ahead
    static GPALButton chaptersSaveBtn = GPAL.Button
        .Default
        .WithName("chaptersSave")
        .WithText("📁 Choose Folder...")
        .WithCallback<EventHandler>((s, e) => chaptersChosen = true)
        .ToGPALObject();

    static GPALButton chaptersCancelBtn = GPAL.Button
        .WithName("chaptersCancel")
        .WithText("Cancel")
        .WithCallback<EventHandler>((s, e) => HideParentForm(s))
        .ToGPALObject();

    // ── Upload Videos window ──────────────────────────────────────────────────

    static List<UploadJob> uploadJobs    { get; set; } = new List<UploadJob>();
    static int             uploadEditing { get; set; } = -1;     // the file whose title and description the boxes hold
    static bool            uploadChosen  { get; set; }

    // the categories YouTube offers for an upload, as its own pages name them
    static readonly YouTubeCategory[] categoryTypes =
    {
        YouTubeCategory.PeopleBlogs, YouTubeCategory.Education, YouTubeCategory.ScienceTechnology, YouTubeCategory.Music,
        YouTubeCategory.Entertainment, YouTubeCategory.FilmAnimation, YouTubeCategory.Gaming, YouTubeCategory.HowtoStyle,
        YouTubeCategory.Comedy, YouTubeCategory.NewsPolitics, YouTubeCategory.Sports, YouTubeCategory.TravelEvents,
        YouTubeCategory.AutosVehicles, YouTubeCategory.PetsAnimals, YouTubeCategory.NonprofitsActivism,
    };
    static readonly string[] categoryLabels =
    {
        "People & Blogs", "Education", "Science & Technology", "Music", "Entertainment", "Film & Animation", "Gaming",
        "Howto & Style", "Comedy", "News & Politics", "Sports", "Travel & Events", "Autos & Vehicles", "Pets & Animals",
        "Nonprofits & Activism",
    };

    // Private first: a new upload stays out of sight until it has been checked on YouTube
    static readonly YouTubePrivacy[] privacyTypes = { YouTubePrivacy.Private, YouTubePrivacy.Unlisted, YouTubePrivacy.Public };

    // the providers GPAL.AI talks to, with the model each starts on; any model id the provider knows can be typed in
    static readonly string[] aiProviders      = { "Anthropic", "OpenAI", "XAI" };
    static readonly string[] aiProviderLabels = { "Anthropic (Claude)", "OpenAI (GPT)", "xAI (Grok)" };
    static readonly string[] aiDefaultModels  = { AIModel.ClaudeSonnet46.ToString(), AIModel.GPT4o.ToString(), AIModel.Grok3Latest.ToString() };

    const string DefaultAIPrompt =
        "Write YouTube details for the video described below.\n" +
        "Title: under 100 characters, plain and specific.\n" +
        "Description: two or three short paragraphs saying what the video shows.\n" +
        "Plain ASCII only. Use only what the file name and notes say; do not invent names, places or dates.\n\n" +
        "Respond with ONLY this, nothing else:\n" +
        "TITLE:\n" +
        "[the title]\n\n" +
        "DESCRIPTION:\n" +
        "[the description]";

    static GPALTab uploadVideosTab = GPAL.Tab.WithName("uploadVideosTab").WithText("Videos").ToGPALObject();
    static GPALTab uploadAITab     = GPAL.Tab.WithName("uploadAITab").WithText("Write with AI").ToGPALObject();

    static GPALListView uploadList = GPAL.ListView
        .WithColumns("File", "Title")
        .WithColumnWidth(240, -2)
        .WithView(ListViewMode.Details)
        .WithFullRowSelect(true)
        .WithHeight(140)
        .WithName("uploadList")
        .WithCallback<EventHandler>(OnUploadPicked)
        .ToGPALObject();

    static GPALButton uploadAddBtn = GPAL.Button
        .WithName("uploadAdd")
        .WithText("📂 Add Files...")
        .WithCallback<EventHandler>(OnUploadAdd)
        .ToGPALObject();

    static GPALButton uploadRemoveBtn = GPAL.Button
        .WithName("uploadRemove")
        .WithText("➖ Remove File")
        .WithCallback<EventHandler>(OnUploadRemove)
        .ToGPALObject();

    static GPALSplitter uploadFileActions = (GPALSplitter)GPAL.Splitter.HSplit(0.5)
        .WithFormControl(uploadAddBtn)
        .WithFormControl(GPAL.SplitRight)
        .WithFormControl(uploadRemoveBtn)
        .ToGPALObject();

    static GPALInput    uploadTitleInput = GPAL.Input.WithName("uploadTitle").ToGPALObject();
    static GPALTextArea uploadDescArea   = (GPALTextArea)GPAL.TextArea.WithHeight(110).WithName("uploadDescription").ToGPALObject();
    static GPALInput    uploadTagsInput  = GPAL.Input.WithName("uploadTags").ToGPALObject();

    static GPALComboBox uploadCategoryCombo = GPAL.ComboBox
        .WithItems(categoryLabels)
        .WithSelectedIndex(0)
        .WithName("uploadCategory")
        .ToGPALObject();

    static GPALComboBox uploadPrivacyCombo = GPAL.ComboBox
        .WithItems("Private", "Unlisted", "Public")
        .WithSelectedIndex(0)
        .WithName("uploadPrivacy")
        .ToGPALObject();

    static GPALComboBox uploadPlaylistCombo = GPAL.ComboBox
        .WithName("uploadPlaylist")
        .ToGPALObject();

    static GPALLabel uploadStatus = (GPALLabel)GPAL.LabelFor("Add the videos to upload.");

    static GPALButton uploadAIBtn = GPAL.Button
        .WithName("uploadAI")
        .WithText("✨ Write with AI")
        .WithCallback<EventHandler>((s, e) => SetUploadBusy(true))
        .WithCallbackOffUIThread<EventHandler>(OnWriteWithAI)
        .ToGPALObject();

    static GPALButton uploadGoBtn = GPAL.Button
        .WithName("uploadGo")
        .WithText("⬆️ Upload")
        .WithCallback<EventHandler>(OnUploadGo)
        .ToGPALObject();

    static GPALButton uploadCancelBtn = GPAL.Button
        .WithName("uploadCancel")
        .WithText("Cancel")
        .WithCallback<EventHandler>((s, e) => HideParentForm(s))
        .ToGPALObject();

    static GPALTextArea aiDirections = (GPALTextArea)GPAL.TextArea
        .WithReadOnly(true)
        .WithHeight(200)
        .WithName("aiDirections")
        .WithText("Write with AI fills in, and replaces, the title and description of every video in the list. Check each one before you upload." + Environment.NewLine + Environment.NewLine +
                  "For each video the AI gets three things: the prompt below, the video's file name, and the text of a notes file beside the video when there is one. " +
                  "The notes file has the video's name with .txt or .md in place of its own extension: for Concert 2019.mp4 that is Concert 2019.txt or Concert 2019.md in the same folder. " +
                  "Put in it whatever the AI should know: who, where, when, what the video covers." + Environment.NewLine + Environment.NewLine +
                  "With no notes file the file name is all it has, so a name like \"2019-06-14 Band Name - Venue.mp4\" goes a long way." + Environment.NewLine + Environment.NewLine +
                  "Change the prompt to suit the channel; each channel keeps its own. Keep the part that asks for TITLE: and DESCRIPTION:, since that is how the answer is read. " +
                  "The provider, model and key are kept for every channel, in youtube-channels.yaml beside this program.")
        .ToGPALObject();

    static GPALComboBox aiProviderCombo = GPAL.ComboBox
        .WithItems(aiProviderLabels)
        .WithSelectedIndex(0)
        .WithCallback<EventHandler>((s, e) => OnAIProviderPicked())
        .WithName("aiProvider")
        .ToGPALObject();

    static GPALInput aiModelInput = GPAL.Input.WithName("aiModel").ToGPALObject();

    static GPALInput aiKeyInput = (GPALInput)GPAL.Input
        .IsPassword
        .WithName("aiKey");

    static GPALTextArea aiPromptArea = (GPALTextArea)GPAL.TextArea.WithHeight(150).WithName("aiPrompt").ToGPALObject();

    // ── About ─────────────────────────────────────────────────────────────────

    static GPALLabel aboutText = (GPALLabel)GPAL.LabelFor(
        "ChannelManager, a GPAL Bot of the Week.\n\n" +
        "List the videos and playlists on each channel you run,\n" +
        "upload and replace videos, tidy playlists, and export\n" +
        "the list and each video's chapters.\n\n" +
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

    [STAThread]
    static void Main()
    {
        // Leave out DEBUG and DEEPDEBUG; they are GPAL's internal tracing, raw REST bodies included, not for a person
        // to read.
        GPAL
            .WithHandlerEvents(GPALEventType.All & ~(GPALEventType.DEBUG | GPALEventType.DEEPDEBUG))
            .WithInformationHandler(OnGPALEvent)
            .WithExceptionHandler(OnGPALEvent);

        LoadSavedChannels();

        // ── Step 1: Google OAuth credentials ──────────────────────────────────
        credForm = GPAL.Form
            .WithTitle("ChannelManager -- Step 1 of 2: Google Credentials")
            .WithWidth(720)
            .WithHeight(660)
            .WithFormControl(connectTab)
            .WithFormControl(connectHelp)
            .WithFormControl(GPAL.LabelFor("Channel:"))
            .WithFormControl(channelCombo)
            .WithFormControl(GPAL.LabelFor("Client ID:"))
            .WithFormControl(clientIdInput)
            .WithFormControl(GPAL.LabelFor("Client Secret:"))
            .WithFormControl(clientSecretInput)
            .WithFormControl(GPAL.LabelFor("Refresh Token:"))
            .WithFormControl(refreshTokenInput)
            .WithFormControl(GPAL.SpacerFor(12))
            .WithFormControl(connectBtn)
            .WithFormControl(importBtn)
            .WithFormControl(removeChannelBtn)
            .WithFormControl(cancelCredBtn)

            .WithFormControl(apiTab)
            .WithFormControl(apiSteps)
            .WithFormControl(openApiBtn)

            .WithFormControl(authTab)
            .WithFormControl(authSteps)
            .WithFormControl(GPAL.LabelFor("Scope:"))
            .WithFormControl(authScopeInput)
            .WithFormControl(openAuthBtn)
            .WithFormControl(openAudienceBtn)
            .WithFormControl(openDataAccessBtn)

            .WithFormControl(clientTab)
            .WithFormControl(clientSteps)
            .WithFormControl(GPAL.LabelFor("Redirect URI:"))
            .WithFormControl(redirectInput)
            .WithFormControl(openClientsBtn)

            .WithFormControl(tokenTab)
            .WithFormControl(tokenSteps)
            .WithFormControl(GPAL.LabelFor("Scope:"))
            .WithFormControl(tokenScopeInput)
            .WithFormControl(openPlaygroundBtn);

        credForm.ShowDialog();

        if (!credConnected) return;

        // ── Step 2: the channel ───────────────────────────────────────────────
        mainForm = GPAL.Form
            .WithTitle($"ChannelManager  |  {channelName}  |  A GPAL Bot of the Week")
            .WithWidth(840)
            .WithHeight(705)
            .WithFormControl(videosTab)
            .WithFormControl(GPAL.LabelFor("Your videos:"))
            .WithFormControl(videosList)
            .WithFormControl(refreshVideosBtn)
            .WithFormControl(videoActions)
            .WithFormControl(GPAL.SpacerFor(12))
            .WithFormControl(switchChannelBtn)
            .WithFormControl(aboutBtn)
            .WithFormControl(playlistsTab)
            .WithFormControl(GPAL.LabelFor("Your playlists:"))
            .WithFormControl(playlistsList)
            .WithFormControl(refreshPlaylistsBtn)
            .WithFormControl(removeDeletedBtn)
            .WithFormControl(GPAL.LabelFor("Videos in the selected playlist:"))
            .WithFormControl(playlistItemsList)
            .WithFormControl(logTab)
            .WithFormControl(debugCheck)
            .WithFormControl(clearLogBtn)
            .WithFormControl(logArea)
            .WithFormControl(howToTab)
            .WithFormControl(instructionArea)
            .WithFormControl(statusBar);

        instructionArea.Text = string.Join(Environment.NewLine, instructions);

        // The lists fill in once the form is up; GPAL holds rows added before a list exists. GPAL forms have no event
        // for the form being shown, so the first load is started here on a task of its own.
        _ = Task.Run(LoadEverything);

        mainForm.ShowDialog();
    }

    // ── Credentials ───────────────────────────────────────────────────────────

    // opens a Google page in the default browser
    static void OpenUrl(string url) => GPAL.Launcher.WithExecutable(url).Launch();

    // reads youtube-channels.yaml beside the exe, when there is one, and shows the channel used last, or New channel
    // when there are none
    static void LoadSavedChannels()
    {
        if (File.Exists(ChannelsFile))
        {
            ChannelSettings saved = new ChannelSettings();
            GPAL.Converter
                .WithInput((GPALFile)ChannelsFile)
                .SaveTo(ref saved);

            settings = saved;
        }

        int last = settings.Channels.FindIndex(c => settings.LastChannel == c.ChannelId);

        FillChannelCombo(0 <= last ? last : settings.Channels.Count);
    }

    // the saved channels by name, then New channel, with the one at index picked and shown
    static void FillChannelCombo(int index)
    {
        List<string> names = settings.Channels.ConvertAll(c => c.Name);
        names.Add(NewChannelItem);

        // the string[] overload shows the items on a form that is already up; the list overload only stores them
        channelCombo.ClearItems();
        channelCombo.WithItems(names.ToArray());
        channelCombo.SelectedIndex = index;

        ShowChannel(index);
    }

    // A saved channel fills in its three values. New channel empties only the Refresh Token: the Client ID and
    // Client Secret shown stay, for a channel made in the same Google Cloud project
    static void ShowChannel(int index)
    {
        if (0 <= index && index < settings.Channels.Count)
        {
            clientIdInput.Text     = settings.Channels[index].ClientId;
            clientSecretInput.Text = settings.Channels[index].ClientSecret;
            refreshTokenInput.Text = settings.Channels[index].RefreshToken;
        }
        else
            refreshTokenInput.Text = string.Empty;
    }

    // takes the picked channel out of youtube-channels.yaml; the channel itself is not touched
    static void OnRemoveChannel(object s, EventArgs e)
    {
        int index = channelCombo.SelectedIndex;

        if (0 > index || index >= settings.Channels.Count)
        {
            MessageBox.Show("Pick a saved channel to remove.", "Remove Channel", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        YouTubeChannel channel = settings.Channels[index];

        if (DialogResult.Yes != MessageBox.Show($"Remove [{channel.Name}] from the saved channels?\n\nThe channel on YouTube is not touched; connecting to it again needs its three values.",
                "Remove Channel", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2))
            return;

        settings.Channels.RemoveAt(index);

        GPAL.Converter
            .WithInput(settings)
            .SaveTo((GPALFile)ChannelsFile);

        GPAL.PublishSimpleEvent(GPALEventType.NOTICE, $"Removed [{channel.Name}] from the saved channels");
        FillChannelCombo(settings.Channels.Count);
    }

    // Reads the connection values from another GPAL YouTube program's youtube-creds.yaml. VideoReplacer, YouTubeTest,
    // YouTubeSearchTester and YouTubeUploader all save the same three property names.
    static void OnImport(object s, EventArgs e)
    {
        using (var dlg = new OpenFileDialog())
        {
            dlg.Title    = "Import settings from another GPAL YouTube program";
            dlg.Filter   = "GPAL YouTube settings (*.yaml;*.yml;*.json)|*.yaml;*.yml;*.json|All files (*.*)|*.*";
            dlg.FileName = "youtube-creds.yaml";

            if (DialogResult.OK == dlg.ShowDialog())
            {
                var found = new YouTubeCreds();
                GPAL.Converter
                    .WithInput((GPALFile)dlg.FileName)
                    .SaveTo(ref found);

                if (string.IsNullOrWhiteSpace(found.ClientId) &&
                    string.IsNullOrWhiteSpace(found.ClientSecret) &&
                    string.IsNullOrWhiteSpace(found.RefreshToken))
                {
                    MessageBox.Show($"No client id, client secret or refresh token was found in [{Path.GetFileName(dlg.FileName)}].",
                        "Import Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                else
                {
                    // New channel first, since picking it empties the Refresh Token; a value the file does not hold
                    // leaves the input as it was
                    channelCombo.SelectedIndex = settings.Channels.Count;

                    if (false == string.IsNullOrWhiteSpace(found.ClientId))     clientIdInput.Text     = found.ClientId;
                    if (false == string.IsNullOrWhiteSpace(found.ClientSecret)) clientSecretInput.Text = found.ClientSecret;
                    if (false == string.IsNullOrWhiteSpace(found.RefreshToken)) refreshTokenInput.Text = found.RefreshToken;

                    GPAL.PublishSimpleEvent(GPALEventType.NOTICE, $"Imported the connection values from [{dlg.FileName}]");
                    MessageBox.Show($"Imported the connection values from [{Path.GetFileName(dlg.FileName)}]. Click Connect to add the channel.",
                        "Import Settings", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }
    }

    static void OnConnect(object s, EventArgs e)
    {
        string clientId     = clientIdInput.Text.Trim();
        string clientSecret = clientSecretInput.Text.Trim();
        string refreshToken = refreshTokenInput.Text.Trim();

        if (string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret) ||
            string.IsNullOrWhiteSpace(refreshToken))
        {
            MessageBox.Show("Client ID, Client Secret and Refresh Token are all required.", "Missing Fields",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // the channel open now keeps its credentials until these are proven, so a failed switch changes nothing
        ICredentials trying = GPAL.Credentials
            .WithService(CredentialServiceType.Google)
            .WithServiceKey($"{clientId}:{clientSecret}")
            .WithScope(OAuthScope.YouTube_Manage)
            .WithRefreshToken(refreshToken)
            .ToGPALObject();

        // The connect succeeded only if a token came back, and only then are the credentials saved.
        trying.FetchAccessToken(out string token);

        if (string.IsNullOrEmpty(token))
        {
            MessageBox.Show("Google did not give an access token for these credentials. Check all three, " +
                            "and that the refresh token was made with the youtube scope.", "Connect Failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // the channel the token signs in as, straight from the YouTube Data API
        ChannelList mine = GPAL.RESTClient
            .WithAPIBase("https://www.googleapis.com/youtube/v3/")
            .WithEndpoint("channels")
            .WithParameters(new { part = "snippet", mine = "true" })
            .WithHeader("Authorization", $"Bearer {token}")
            .WithHttpMethod("GET")
            .Execute<ChannelList>();

        ChannelItem channel = 0 < (mine?.items?.Count ?? 0) ? mine.items[0] : null;

        if (null == channel)
        {
            MessageBox.Show("Google gave a token, but YouTube named no channel for it. If the GPAL Log shows YouTube refused the call, " +
                            "check the YouTube Data API is on (tab 1). Otherwise make the refresh token again, and when Google asks " +
                            "which account or brand account to use, pick the channel's name (tab 4).", "Connect Failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        string name = channel.snippet?.title ?? channel.id;

        // a channel connected again keeps one entry, with the values just used and the AI prompt it had; the list stays
        // in name order
        YouTubeChannel before = settings.Channels.FirstOrDefault(c => channel.id == c.ChannelId);

        settings.Channels.RemoveAll(c => channel.id == c.ChannelId);
        settings.Channels.Add(new YouTubeChannel
        {
            Name         = name,
            ChannelId    = channel.id,
            ClientId     = clientId,
            ClientSecret = clientSecret,
            RefreshToken = refreshToken,
            AIPrompt     = before?.AIPrompt ?? string.Empty,
        });
        settings.Channels.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        settings.LastChannel = channel.id;
        channelName          = name;

        GPAL.Converter
            .WithInput(settings)
            .SaveTo((GPALFile)ChannelsFile);

        GPAL.PublishSimpleEvent(GPALEventType.NOTICE, $"Connected to [{channelName}]");

        credentials   = trying;
        credConnected = true;
        HideParentForm(s);
    }

    // Shows the Connect window again with the channel open now picked. A connect there, to a saved channel or a new
    // one, reloads every list for it; Cancel leaves the channel open now as it was
    static void OnSwitchChannel(object s, EventArgs e)
    {
        int open = settings.Channels.FindIndex(c => settings.LastChannel == c.ChannelId);

        FillChannelCombo(0 <= open ? open : settings.Channels.Count);

        credConnected = false;
        credForm.ShowDialog();

        if (false == credConnected)
        {
            credConnected = true;
            return;
        }

        mainForm.WithTitle($"ChannelManager  |  {channelName}  |  A GPAL Bot of the Week");

        // off the UI thread, as at the start, so the form keeps painting while the lists fill
        _ = Task.Run(LoadEverything);
    }

    // ── Loading ───────────────────────────────────────────────────────────────

    static void LoadEverything()
    {
        SetBusy(true);
        LoadVideos();
        LoadPlaylists();
        SetStatus($"Loaded {videos.Count} video(s) and {playlists.Count} playlist(s).");
        SetBusy(false);
    }

    static void LoadVideos()
    {
        SetStatus("Loading videos...");

        GPAL.YouTube
            .WithCredentials(credentials)
            .GetMyVideos(out List<YouTubeVideoInfo> found);

        videos = found;
        videosList.ClearItems();

        foreach (YouTubeVideoInfo v in found)
            videosList.AddItem(v.Title,
                v.PublishedAt.HasValue ? v.PublishedAt.Value.ToString("yyyy-MM-dd") : string.Empty,
                v.Privacy.HasValue ? v.Privacy.Value.ToString() : string.Empty,
                v.VideoId);
    }

    static void LoadPlaylists()
    {
        SetStatus("Loading playlists...");

        GPAL.YouTube
            .WithCredentials(credentials)
            .GetMyPlaylists(out List<YouTubePlaylistInfo> found);

        playlists = found;
        playlistsList.ClearItems();
        playlistItemsList.ClearItems();
        playlistItems = new List<YouTubePlaylistItem>();

        foreach (YouTubePlaylistInfo p in found)
            playlistsList.AddItem(p.Title,
                p.ItemCount.ToString(),
                p.Privacy.HasValue ? p.Privacy.Value.ToString() : string.Empty,
                p.PlaylistId);
    }

    static void LoadPlaylistItems(YouTubePlaylistInfo playlist)
    {
        List<YouTubePlaylistItem> items = ReadPlaylistItems(playlist.PlaylistId);

        // a Switch Channel while this was reading replaced the playlists; these belong to the channel left behind
        if (false == playlists.Contains(playlist)) return;

        playlistItems = items;
        playlistItemsList.ClearItems();

        // positions are shown counted from 1, the way YouTube shows them
        foreach (YouTubePlaylistItem item in playlistItems)
            playlistItemsList.AddItem((item.Position + 1).ToString(), item.Title, item.VideoId);
    }

    static List<YouTubePlaylistItem> ReadPlaylistItems(string playlistId)
    {
        GPAL.YouTube
            .WithCredentials(credentials)
            .WithPlaylist(playlistId)
            .GetPlaylistItems(out List<YouTubePlaylistItem> items);

        return items;
    }

    // ── Videos tab callbacks ──────────────────────────────────────────────────

    static void OnRefreshVideos(object s, EventArgs e)
    {
        try
        {
            // DEEPDEBUG stays out either way; raw protocol traffic buries everything else.
            GPAL.WithHandlerEvents(true == debugCheck.Checked
                ? GPALEventType.All & ~GPALEventType.DEEPDEBUG
                : GPALEventType.All & ~(GPALEventType.DEBUG | GPALEventType.DEEPDEBUG));

            SetBusy(true);
            LoadVideos();
            SetStatus($"Loaded {videos.Count} video(s).");
            SetBusy(false);
        }
        finally
        {
            // the click turned the button off; it comes back as the program's state has it
            SetBusy(false);
        }
    }

    static void OnReplace(object s, EventArgs e)
    {
        try
        {
            // DEEPDEBUG stays out either way; raw protocol traffic buries everything else.
            GPAL.WithHandlerEvents(true == debugCheck.Checked
                ? GPALEventType.All & ~GPALEventType.DEEPDEBUG
                : GPALEventType.All & ~(GPALEventType.DEBUG | GPALEventType.DEEPDEBUG));

            YouTubeVideoInfo video = SelectedVideo();
            if (null == video) return;

            // dialogs open on the UI thread; the handler itself runs off it
            string path = null;
            replaceBtn.OnUiThread(() =>
            {
                using (var dlg = new OpenFileDialog())
                {
                    dlg.Title  = $"New file for {video.Title}";
                    dlg.Filter = "Video files (*.mp4;*.mov;*.mkv;*.avi;*.webm)|*.mp4;*.mov;*.mkv;*.avi;*.webm|All files (*.*)|*.*";
                    if (dlg.ShowDialog() == DialogResult.OK)
                        path = dlg.FileName;
                }
            });
            if (null == path) return;

            // a chapters file beside the new file holds the new video's chapters, one "m:ss Name" per line: chapters.txt,
            // then <new file's name>.chapters.txt, then any *chapters*.txt, the first by name when there are several.
            // they take the place of the first run of chapter lines in the old description, or go at the end when it has none
            string folder       = Path.GetDirectoryName(path);
            string chaptersPath = Path.Combine(folder, "chapters.txt");
            if (false == File.Exists(chaptersPath))
                chaptersPath = Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + ".chapters.txt");
            if (false == File.Exists(chaptersPath))
            {
                string[] found = Directory.GetFiles(folder, "*chapters*.txt");
                Array.Sort(found, StringComparer.OrdinalIgnoreCase);
                chaptersPath = 0 < found.Length ? found[0] : null;
                if (1 < found.Length)
                    GPAL.PublishSimpleEvent(GPALEventType.WARNING, $"[{found.Length}] chapters files beside the new file; using [{Path.GetFileName(chaptersPath)}].", null, GPALObjectType.Other);
            }

            string newDescription = null;
            string chaptersNote   = "No chapters.txt, " + Path.GetFileNameWithoutExtension(path) + ".chapters.txt or other *chapters*.txt beside the new file, so the description is copied as it is.";
            if (null != chaptersPath)
            {
                var chapterLine = new Regex(@"^\s*\(?\d{1,2}:\d{2}(:\d{2})?\)?\s+\S");
                var chapters    = new List<string>(File.ReadAllText(chaptersPath).Trim().Replace("\r\n", "\n").Split('\n'));
                var lines       = new List<string>((video.Description ?? string.Empty).Replace("\r\n", "\n").Split('\n'));
                int first       = lines.FindIndex(l => chapterLine.IsMatch(l));
                if (first < 0)
                {
                    newDescription = (video.Description ?? string.Empty).TrimEnd() + "\n\n" + string.Join("\n", chapters);
                }
                else
                {
                    int end = first;
                    while (end < lines.Count && chapterLine.IsMatch(lines[end])) end++;
                    lines.RemoveRange(first, end - first);
                    lines.InsertRange(first, chapters);
                    newDescription = string.Join("\n", lines);
                }
                newDescription = newDescription.Trim();
                chaptersNote   = $"The description's chapters are replaced with the {chapters.Count} in {Path.GetFileName(chaptersPath)}.";
            }

            DialogResult go = DialogResult.None;
            replaceBtn.OnUiThread(() => go = MessageBox.Show(
                $"Replace [{video.Title}] with [{Path.GetFileName(path)}]?\n\n" +
                "The new video gets the old one's title, description, tags, category, privacy, thumbnail " +
                "and its place in each of your playlists. The old video stays on YouTube until you delete it.\n\n" +
                chaptersNote,
                "Replace Video", MessageBoxButtons.YesNo, MessageBoxIcon.Question));
            if (DialogResult.Yes != go) return;

            SetBusy(true);
            SetStatus($"Replacing [{video.Title}]...");

            // checks processing every 30 seconds rather than the default minute
            YouTubeUploadResult result = GPAL.YouTube
                .WithCredentials(credentials)
                .WithVideoId(video.VideoId)
                .WaitForProcessing(30)
                .ReplaceVideo(path);

            if (null == result)
            {
                SetStatus($"Could not replace [{video.Title}]. See the GPAL Log.");
            }
            else if ("processed" != result.Status && "uploaded" != result.Status && "unknown" != result.Status)
            {
                SetStatus($"The new video for [{video.Title}] ended [{result.Status}]. The old video and its playlists are unchanged.");
            }
            else
            {
                if (null != newDescription)
                {
                    SetStatus($"Updating the chapters on [{result.VideoId}]...");
                    GPAL.YouTube
                        .WithCredentials(credentials)
                        .WithVideoId(result.VideoId)
                        .WithDescription(newDescription)
                        .UpdateVideo();
                }

                SetStatus($"Replaced [{video.Title}]. New video [{result.VideoId}] at {result.Url}");
                GPAL.PublishSimpleEvent(GPALEventType.NOTICE, $"Replaced [{video.Title}]. New video [{result.VideoId}] at [{result.Url}]");

                DialogResult delete = DialogResult.None;
                replaceBtn.OnUiThread(() => delete = MessageBox.Show(
                    $"Delete the old video now?\n\n[{video.Title}]  ({video.VideoId})",
                    "Delete Old Video", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2));

                if (DialogResult.Yes == delete)
                {
                    SetStatus($"Deleting the old [{video.Title}]...");
                    GPAL.YouTube
                        .WithCredentials(credentials)
                        .WithVideoId(video.VideoId)
                        .DeleteVideo();
                }

                LoadVideos();
                LoadPlaylists();
                SetStatus($"Replaced [{video.Title}]. New video [{result.VideoId}] at {result.Url}");
            }

            SetBusy(false);
        }
        finally
        {
            // the click turned the button off; it comes back as the program's state has it
            SetBusy(false);
        }
    }

    static void OnAddToPlaylist(object s, EventArgs e)
    {
        try
        {
            // DEEPDEBUG stays out either way; raw protocol traffic buries everything else.
            GPAL.WithHandlerEvents(true == debugCheck.Checked
                ? GPALEventType.All & ~GPALEventType.DEEPDEBUG
                : GPALEventType.All & ~(GPALEventType.DEBUG | GPALEventType.DEEPDEBUG));

            YouTubeVideoInfo video = SelectedVideo();
            if (null == video) return;

            YouTubePlaylistInfo playlist = null;
            addToPlaylistBtn.OnUiThread(() => playlist = PickPlaylist("Add to Playlist", $"Add [{video.Title}] to:"));
            if (null == playlist) return;

            SetBusy(true);
            SetStatus($"Adding [{video.Title}] to [{playlist.Title}]...");

            GPAL.YouTube
                .WithCredentials(credentials)
                .WithVideoId(video.VideoId)
                .AddToPlaylist(playlist.PlaylistId);

            bool added = ReadPlaylistItems(playlist.PlaylistId).Exists(i => video.VideoId == i.VideoId);

            LoadPlaylists();
            SetStatus(added
                ? $"Added [{video.Title}] to [{playlist.Title}]."
                : $"Could not add [{video.Title}] to [{playlist.Title}]. See the GPAL Log.");
            SetBusy(false);
        }
        finally
        {
            // the click turned the button off; it comes back as the program's state has it
            SetBusy(false);
        }
    }

    static void OnRemoveFromPlaylist(object s, EventArgs e)
    {
        try
        {
            // DEEPDEBUG stays out either way; raw protocol traffic buries everything else.
            GPAL.WithHandlerEvents(true == debugCheck.Checked
                ? GPALEventType.All & ~GPALEventType.DEEPDEBUG
                : GPALEventType.All & ~(GPALEventType.DEBUG | GPALEventType.DEEPDEBUG));

            YouTubeVideoInfo video = SelectedVideo();
            if (null == video) return;

            YouTubePlaylistInfo playlist = null;
            removeFromPlaylistBtn.OnUiThread(() => playlist = PickPlaylist("Remove from Playlist", $"Remove [{video.Title}] from:"));
            if (null == playlist) return;

            SetBusy(true);
            SetStatus($"Removing [{video.Title}] from [{playlist.Title}]...");

            GPAL.YouTube
                .WithCredentials(credentials)
                .WithPlaylist(playlist.PlaylistId)
                .WithVideoId(video.VideoId)
                .RemoveFromPlaylist();

            bool gone = !ReadPlaylistItems(playlist.PlaylistId).Exists(i => video.VideoId == i.VideoId);

            LoadPlaylists();
            SetStatus(gone
                ? $"Removed [{video.Title}] from [{playlist.Title}]."
                : $"Could not remove [{video.Title}] from [{playlist.Title}]. See the GPAL Log.");
            SetBusy(false);
        }
        finally
        {
            // the click turned the button off; it comes back as the program's state has it
            SetBusy(false);
        }
    }

    static void OnDelete(object s, EventArgs e)
    {
        try
        {
            // DEEPDEBUG stays out either way; raw protocol traffic buries everything else.
            GPAL.WithHandlerEvents(true == debugCheck.Checked
                ? GPALEventType.All & ~GPALEventType.DEEPDEBUG
                : GPALEventType.All & ~(GPALEventType.DEBUG | GPALEventType.DEEPDEBUG));

            YouTubeVideoInfo video = SelectedVideo();
            if (null == video) return;

            DialogResult sure = DialogResult.None;
            deleteBtn.OnUiThread(() => sure = MessageBox.Show(
                $"Delete [{video.Title}] from YouTube?\n\nThis cannot be undone.",
                "Delete Video", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2));
            if (DialogResult.Yes != sure) return;

            SetBusy(true);
            SetStatus($"Deleting [{video.Title}]...");

            GPAL.YouTube
                .WithCredentials(credentials)
                .WithVideoId(video.VideoId)
                .DeleteVideo();

            LoadVideos();
            LoadPlaylists();

            bool gone = !videos.Exists(v => video.VideoId == v.VideoId);
            SetStatus(gone
                ? $"Deleted [{video.Title}]."
                : $"Could not delete [{video.Title}]. See the GPAL Log.");
            SetBusy(false);
        }
        finally
        {
            // the click turned the button off; it comes back as the program's state has it
            SetBusy(false);
        }
    }

    static void OnExport(object s, EventArgs e)
    {
        // DEEPDEBUG stays out either way; raw protocol traffic buries everything else.
        GPAL.WithHandlerEvents(true == debugCheck.Checked
            ? GPALEventType.All & ~GPALEventType.DEEPDEBUG
            : GPALEventType.All & ~(GPALEventType.DEBUG | GPALEventType.DEEPDEBUG));

        if (0 == videos.Count)
        {
            SetStatus("No videos to export.");
            return;
        }

        exportChosen = false;

        GPAL.Form
            .WithTitle("Export Videos")
            .WithWidth(360)
            .WithFormControl(GPAL.LabelFor($"Export {videos.Count} video(s). Fields to export:"))
            .WithFormControl(cbTitle)
            .WithFormControl(cbDescription)
            .WithFormControl(cbTags)
            .WithFormControl(cbUrl)
            .WithFormControl(cbPublished)
            .WithFormControl(cbVideoId)
            .WithFormControl(GPAL.LabelFor("Format:"))
            .WithFormControl(formatCombo)
            .WithFormControl(GPAL.SpacerFor(12))
            .WithFormControl(exportSaveBtn)
            .WithFormControl(exportCancelBtn)
            .ShowDialog();

        if (!exportChosen) return;

        int fmtIdx = formatCombo.SelectedIndex;
        if (fmtIdx < 0 || fmtIdx >= formatTypes.Length) fmtIdx = 0;

        string path;
        using (var dlg = new SaveFileDialog())
        {
            dlg.Title      = "Export videos";
            dlg.Filter     = formatFilters[fmtIdx] + "|All files (*.*)|*.*";
            dlg.DefaultExt = formatExts[fmtIdx].TrimStart('.');
            dlg.FileName   = "youtube-videos" + formatExts[fmtIdx];
            if (dlg.ShowDialog() != DialogResult.OK) return;
            path = dlg.FileName;
        }

        GPAL.Converter
            .WithInput(BuildExportGrid())
            .WithOutputType(formatTypes[fmtIdx])
            .WithFirstLineHasColumnNames(true)
            .SaveTo((GPALFile)path);

        SetStatus($"Exported {videos.Count} video(s) to {Path.GetFileName(path)}.");
    }

    // writes the chapter lines of each video whose title holds the given text to <title>.chapters.txt in one folder
    static void OnExportChapters(object s, EventArgs e)
    {
        // DEEPDEBUG stays out either way; raw protocol traffic buries everything else.
        GPAL.WithHandlerEvents(true == debugCheck.Checked
            ? GPALEventType.All & ~GPALEventType.DEEPDEBUG
            : GPALEventType.All & ~(GPALEventType.DEBUG | GPALEventType.DEEPDEBUG));

        if (0 == videos.Count)
        {
            SetStatus("No videos to export.");
            return;
        }

        chaptersChosen = false;

        GPAL.Form
            .WithTitle("Export Chapters")
            .WithWidth(420)
            .WithFormControl(GPAL.LabelFor("Each video whose title holds this text gets its own <title>.chapters.txt:"))
            .WithFormControl(chaptersMatchInput)
            .WithFormControl(GPAL.SpacerFor(12))
            .WithFormControl(chaptersSaveBtn)
            .WithFormControl(chaptersCancelBtn)
            .ShowDialog();

        if (!chaptersChosen) return;

        string match = (chaptersMatchInput.Text ?? string.Empty).Trim();
        List<YouTubeVideoInfo> matched = videos.FindAll(v => (v.Title ?? string.Empty).IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0);
        if (0 == matched.Count)
        {
            SetStatus($"No video titles hold [{match}].");
            return;
        }

        string folder;
        using (var dlg = new FolderBrowserDialog())
        {
            dlg.Description = $"Folder for the chapters of {matched.Count} video(s)";
            if (dlg.ShowDialog() != DialogResult.OK) return;
            folder = dlg.SelectedPath;
        }

        // the file name keeps the title's letters and digits: accents come off (Motley Crue) and letters such as ø and ß
        // are spelled out (o, ss), apostrophes go, a run of spaces and periods becomes one period and a run holding
        // anything else (arrows, emoji, commas, dashes) becomes one dash, and periods and dashes at the ends go, so
        // "Hoobastank Live 2026 🡆  🡄 Oct 17 ⬘ The Woodlands, TX"
        // is Hoobastank.Live.2026-Oct.17-The.Woodlands-TX; a name Windows keeps for a device (Con.Funk.Shun reads as CON)
        // or titles that end up the same name get the video ID, so every file is its own
        var spelled = new Dictionary<char, string>
        {
            ['ø'] = "o", ['Ø'] = "O", ['æ'] = "ae", ['Æ'] = "AE", ['ß'] = "ss", ['ł'] = "l", ['Ł'] = "L",
            ['đ'] = "d", ['Đ'] = "D", ['ð'] = "d", ['Ð'] = "D", ['þ'] = "th", ['Þ'] = "Th",
        };
        var names = new Dictionary<YouTubeVideoInfo, string>();
        var used  = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (YouTubeVideoInfo v in matched)
        {
            var plain = new StringBuilder();
            foreach (char c in (v.Title ?? string.Empty).Normalize(NormalizationForm.FormD))
                if (spelled.TryGetValue(c, out string letters)) plain.Append(letters);
                else if (UnicodeCategory.NonSpacingMark != CharUnicodeInfo.GetUnicodeCategory(c)) plain.Append(c);

            string name = Regex.Replace(plain.ToString(), "['\u2018\u2019\u02BC\u00B4`]", string.Empty);
            name = Regex.Replace(name, "[^A-Za-z0-9]+", m => Regex.IsMatch(m.Value, @"^[\s.]+$") ? "." : "-").Trim('.', '-');
            if (0 == name.Length) name = v.VideoId;
            else if (Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|-|$)", RegexOptions.IgnoreCase)) name += "." + v.VideoId;
            names[v] = name;
            used[name] = used.TryGetValue(name, out int count) ? count + 1 : 1;
        }

        // the same chapter line Replace reads: the first run of "m:ss Name" lines in the description
        var chapterLine = new Regex(@"^\s*\(?\d{1,2}:\d{2}(:\d{2})?\)?\s+\S");
        int written     = 0;
        var noChapters  = new List<string>();
        foreach (YouTubeVideoInfo v in matched)
        {
            var lines = new List<string>((v.Description ?? string.Empty).Replace("\r\n", "\n").Split('\n'));
            int first = lines.FindIndex(l => chapterLine.IsMatch(l));
            if (0 > first)
            {
                noChapters.Add(v.Title);
            }
            else
            {
                int end = first;
                while (end < lines.Count && chapterLine.IsMatch(lines[end])) end++;

                string name = 1 < used[names[v]] ? $"{names[v]}.{v.VideoId}" : names[v];
                File.WriteAllText(Path.Combine(folder, name + ".chapters.txt"),
                    string.Join(Environment.NewLine, lines.GetRange(first, end - first).ConvertAll(l => l.Trim())) + Environment.NewLine);
                written++;
            }
        }

        foreach (string title in noChapters)
            GPAL.PublishSimpleEvent(GPALEventType.WARNING, $"No chapters in the description of [{title}], so no file was written for it.", null, GPALObjectType.Other);

        SetStatus($"Wrote chapters for {written} of {matched.Count} video(s) to {folder}." +
                  (0 < noChapters.Count ? $" {noChapters.Count} had no chapters; see the GPAL Log tab." : string.Empty));
    }

    // ── Playlists tab callbacks ───────────────────────────────────────────────

    static void OnRefreshPlaylists(object s, EventArgs e)
    {
        try
        {
            // DEEPDEBUG stays out either way; raw protocol traffic buries everything else.
            GPAL.WithHandlerEvents(true == debugCheck.Checked
                ? GPALEventType.All & ~GPALEventType.DEEPDEBUG
                : GPALEventType.All & ~(GPALEventType.DEBUG | GPALEventType.DEEPDEBUG));

            SetBusy(true);
            LoadPlaylists();
            SetStatus($"Loaded {playlists.Count} playlist(s).");
            SetBusy(false);
        }
        finally
        {
            // the click turned the button off; it comes back as the program's state has it
            SetBusy(false);
        }
    }

    // YouTube keeps a deleted video's place in every playlist it was in, titled "Deleted video" and still holding the
    // old video ID. This finds those entries in every playlist and, once you say yes, takes them out. Entries titled
    // "Private video" stay: those are usually another channel's video made private, which can come back.
    static void OnRemoveDeleted(object s, EventArgs e)
    {
        try
        {
            // DEEPDEBUG stays out either way; raw protocol traffic buries everything else.
            GPAL.WithHandlerEvents(true == debugCheck.Checked
                ? GPALEventType.All & ~GPALEventType.DEEPDEBUG
                : GPALEventType.All & ~(GPALEventType.DEBUG | GPALEventType.DEEPDEBUG));

            SetBusy(true);
            SetStatus("Looking for deleted videos in every playlist...");

            // each playlist that holds deleted videos, with those entries
            List<YouTubePlaylistInfo>       holding = new List<YouTubePlaylistInfo>();
            List<List<YouTubePlaylistItem>> deleted = new List<List<YouTubePlaylistItem>>();
            int                             total   = 0;
            StringBuilder                   list    = new StringBuilder();

            foreach (YouTubePlaylistInfo playlist in playlists)
            {
                List<YouTubePlaylistItem> found = ReadPlaylistItems(playlist.PlaylistId).FindAll(i => "Deleted video" == i.Title);

                if (0 < found.Count)
                {
                    holding.Add(playlist);
                    deleted.Add(found);
                    total += found.Count;

                    // positions counted from 1, the way YouTube shows them
                    list.AppendLine($"[{playlist.Title}]: {found.Count} at position(s) {string.Join(", ", found.ConvertAll(i => (i.Position + 1).ToString()))}");
                }
            }

            if (0 == total)
            {
                SetStatus($"No deleted videos in any of your {playlists.Count} playlist(s).");
                return;
            }

            DialogResult sure = DialogResult.None;
            removeDeletedBtn.OnUiThread(() => sure = MessageBox.Show(
                $"Remove {total} deleted video(s) from your playlists?\n\n{list}",
                "Remove Deleted Videos", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2));
            if (DialogResult.Yes != sure)
            {
                SetStatus("Nothing was removed.");
                return;
            }

            for (int p = 0; p < holding.Count; p++)
            {
                SetStatus($"Removing {deleted[p].Count} deleted video(s) from [{holding[p].Title}]...");

                IGPALYouTube remove = GPAL.YouTube
                    .WithCredentials(credentials)
                    .WithPlaylist(holding[p].PlaylistId);

                // RemoveFromPlaylist takes out every entry of a video, so a video in the playlist twice is named once;
                // naming it again would ask YouTube to remove entries already gone
                List<string> named = new List<string>();

                foreach (YouTubePlaylistItem item in deleted[p])
                    if (false == named.Contains(item.VideoId))
                    {
                        named.Add(item.VideoId);
                        remove.WithVideoId(item.VideoId);
                    }

                remove.RemoveFromPlaylist();
            }

            // read back, so the status says what YouTube now holds
            int left = 0;
            foreach (YouTubePlaylistInfo playlist in holding)
                left += ReadPlaylistItems(playlist.PlaylistId).FindAll(i => "Deleted video" == i.Title).Count;

            LoadPlaylists();
            SetStatus(0 == left
                ? $"Removed {total} deleted video(s) from {holding.Count} playlist(s)."
                : $"{left} of {total} deleted video(s) are still in your playlists. See the GPAL Log.");
            SetBusy(false);
        }
        finally
        {
            // the click turned the button off; it comes back as the program's state has it
            SetBusy(false);
        }
    }

    static void OnPlaylistSelected(object s, EventArgs e)
    {
        // DEEPDEBUG stays out either way; raw protocol traffic buries everything else.
        GPAL.WithHandlerEvents(true == debugCheck.Checked
            ? GPALEventType.All & ~GPALEventType.DEEPDEBUG
            : GPALEventType.All & ~(GPALEventType.DEBUG | GPALEventType.DEEPDEBUG));

        // A new selection first clears the old one, which arrives here with nothing selected.
        int idx = playlistsList.SelectedIndex;
        if (idx < 0 || idx >= playlists.Count) return;

        YouTubePlaylistInfo playlist = playlists[idx];
        SetStatus($"Loading [{playlist.Title}]...");
        LoadPlaylistItems(playlist);

        // after a Switch Channel the status belongs to the new channel's load
        if (true == playlists.Contains(playlist))
            SetStatus($"[{playlist.Title}] holds {playlistItems.Count} video(s).");
    }

    // ── Upload Videos ─────────────────────────────────────────────────────────

    // Shows the Upload Videos window for the open channel. The uploads run after it closes, off the UI thread, so the
    // main window keeps painting and the status bar follows them
    static void OnUploadVideos(object s, EventArgs e)
    {
        // DEEPDEBUG stays out either way; raw protocol traffic buries everything else.
        GPAL.WithHandlerEvents(true == debugCheck.Checked
            ? GPALEventType.All & ~GPALEventType.DEEPDEBUG
            : GPALEventType.All & ~(GPALEventType.DEBUG | GPALEventType.DEEPDEBUG));

        YouTubeChannel open = settings.Channels.Find(c => settings.LastChannel == c.ChannelId);

        // the AI as saved; the prompt is this channel's own
        int provider = Array.IndexOf(aiProviders, settings.AIProvider);
        aiProviderCombo.SelectedIndex = 0 <= provider ? provider : 0;
        aiModelInput.Text = false == string.IsNullOrWhiteSpace(settings.AIModel) ? settings.AIModel : aiDefaultModels[0 <= provider ? provider : 0];
        aiKeyInput.Text   = settings.AIKey;
        aiPromptArea.Text = false == string.IsNullOrWhiteSpace(open?.AIPrompt) ? open.AIPrompt : DefaultAIPrompt;

        // no playlist, then the channel's
        List<string> lists = playlists.ConvertAll(p => p.Title);
        lists.Insert(0, "(none)");
        uploadPlaylistCombo.ClearItems();
        uploadPlaylistCombo.WithItems(lists.ToArray());
        uploadPlaylistCombo.SelectedIndex = 0;

        uploadJobs    = new List<UploadJob>();
        uploadEditing = -1;
        uploadChosen  = false;
        uploadList.ClearItems();
        uploadTitleInput.Text = string.Empty;
        uploadDescArea.Text   = string.Empty;
        uploadStatus.Text     = "Add the videos to upload.";
        SetUploadBusy(false);

        GPAL.Form
            .WithTitle($"Upload Videos to {channelName}")
            .WithWidth(720)
            .WithHeight(720)
            .WithFormControl(uploadVideosTab)
            .WithFormControl(uploadList)
            .WithFormControl(uploadFileActions)
            .WithFormControl(GPAL.LabelFor("Title of the picked video (100 characters at most):"))
            .WithFormControl(uploadTitleInput)
            .WithFormControl(GPAL.LabelFor("Description of the picked video; a <video name>.chapters.txt beside it is added to the end:"))
            .WithFormControl(uploadDescArea)
            .WithFormControl(GPAL.LabelFor("Tags for every video, separated by commas:"))
            .WithFormControl(uploadTagsInput)
            .WithFormControl(GPAL.LabelFor("Category:"))
            .WithFormControl(uploadCategoryCombo)
            .WithFormControl(GPAL.LabelFor("Privacy:"))
            .WithFormControl(uploadPrivacyCombo)
            .WithFormControl(GPAL.LabelFor("Add every video to playlist:"))
            .WithFormControl(uploadPlaylistCombo)
            .WithFormControl(uploadStatus)
            .WithFormControl(uploadAIBtn)
            .WithFormControl(uploadGoBtn)
            .WithFormControl(uploadCancelBtn)
            .WithFormControl(uploadAITab)
            .WithFormControl(aiDirections)
            .WithFormControl(GPAL.LabelFor("Provider:"))
            .WithFormControl(aiProviderCombo)
            .WithFormControl(GPAL.LabelFor("Model:"))
            .WithFormControl(aiModelInput)
            .WithFormControl(GPAL.LabelFor("API key:"))
            .WithFormControl(aiKeyInput)
            .WithFormControl(GPAL.LabelFor("Prompt for this channel:"))
            .WithFormControl(aiPromptArea)
            .ShowDialog();

        // what was entered for the AI is kept, uploaded or not
        SaveAISettings();

        if (false == uploadChosen) return;

        // read here, on the UI thread, for the uploads to use
        List<UploadJob> jobs = uploadJobs;
        string[] tags = uploadTagsInput.Text.Split(',').Select(t => t.Trim()).Where(t => 0 < t.Length).ToArray();
        YouTubeCategory category = categoryTypes[Math.Max(0, uploadCategoryCombo.SelectedIndex)];
        YouTubePrivacy privacy = privacyTypes[Math.Max(0, uploadPrivacyCombo.SelectedIndex)];
        int listIndex = uploadPlaylistCombo.SelectedIndex - 1;
        YouTubePlaylistInfo playlist = 0 <= listIndex && listIndex < playlists.Count ? playlists[listIndex] : null;

        _ = Task.Run(() => UploadAll(jobs, tags, category, privacy, playlist));
    }

    static void UploadAll(List<UploadJob> jobs, string[] tags, YouTubeCategory category, YouTubePrivacy privacy, YouTubePlaylistInfo playlist)
    {
        try
        {
            SetBusy(true);

            int uploaded = 0;

            for (int i = 0; i < jobs.Count; i++)
            {
                UploadJob job = jobs[i];
                SetStatus($"Uploading {i + 1} of {jobs.Count}: [{Path.GetFileName(job.Path)}]...");

                IGPALYouTube upload = GPAL.YouTube
                    .WithCredentials(credentials)
                    .WithTitle(job.Title)
                    .WithDescription(DescriptionFor(job))
                    .WithTags(tags)
                    .WithCategory(category)
                    .WithPrivacy(privacy);

                if (null != playlist)
                    upload.WithPlaylist(playlist.PlaylistId);

                if (null != upload.Upload((GPALFile)job.Path))
                    uploaded++;
            }

            LoadVideos();
            LoadPlaylists();
            SetStatus(uploaded == jobs.Count
                ? $"Uploaded {uploaded} video(s){(null != playlist ? $" to [{playlist.Title}]" : string.Empty)}. YouTube may take a while to process them."
                : $"Uploaded {uploaded} of {jobs.Count} video(s). See the GPAL Log for the rest.");
            SetBusy(false);
        }
        finally
        {
            // SetBusy(true) above turned the buttons off; they come back however the uploads ended
            SetBusy(false);
        }
    }

    // the description a video goes up with: its own, then <video name>.chapters.txt beside the file when there is one
    static string DescriptionFor(UploadJob job)
    {
        string chaptersPath = Path.ChangeExtension(job.Path, null) + ".chapters.txt";

        return true == File.Exists(chaptersPath)
            ? job.Description.TrimEnd() + "\n\n" + File.ReadAllText(chaptersPath).Trim()
            : job.Description;
    }

    // the title and description boxes hold the picked video's; this puts what was typed back on it
    static void KeepUploadEdits()
    {
        if (0 <= uploadEditing && uploadEditing < uploadJobs.Count)
        {
            uploadJobs[uploadEditing].Title       = uploadTitleInput.Text.Trim();
            uploadJobs[uploadEditing].Description = uploadDescArea.Text.Trim();
        }
    }

    // A new selection first clears the old one, which arrives here with nothing selected; that is when the boxes
    // are kept on the video they belonged to
    static void OnUploadPicked(object s, EventArgs e)
    {
        KeepUploadEdits();

        uploadEditing = uploadList.SelectedIndex;

        bool picked = 0 <= uploadEditing && uploadEditing < uploadJobs.Count;

        uploadTitleInput.Text = true == picked ? uploadJobs[uploadEditing].Title : string.Empty;
        uploadDescArea.Text   = true == picked ? uploadJobs[uploadEditing].Description : string.Empty;
    }

    // the list again from uploadJobs, with nothing picked
    static void ShowUploadList()
    {
        KeepUploadEdits();

        uploadEditing = -1;
        uploadList.ClearItems();

        foreach (UploadJob job in uploadJobs)
            uploadList.AddItem(Path.GetFileName(job.Path), job.Title);

        uploadTitleInput.Text = string.Empty;
        uploadDescArea.Text   = string.Empty;
    }

    static void OnUploadAdd(object s, EventArgs e)
    {
        using (var dlg = new OpenFileDialog())
        {
            dlg.Title       = "Videos to upload";
            dlg.Filter      = "Videos (*.mp4;*.mov;*.mkv;*.avi;*.wmv;*.webm;*.m4v;*.mpg;*.mpeg;*.flv)|*.mp4;*.mov;*.mkv;*.avi;*.wmv;*.webm;*.m4v;*.mpg;*.mpeg;*.flv|All files (*.*)|*.*";
            dlg.Multiselect = true;

            if (DialogResult.OK != dlg.ShowDialog()) return;

            // each starts with its file name as its title, cut to the 100 characters YouTube allows
            foreach (string path in dlg.FileNames)
                if (false == uploadJobs.Exists(j => string.Equals(j.Path, path, StringComparison.OrdinalIgnoreCase)))
                {
                    string title = Path.GetFileNameWithoutExtension(path);
                    uploadJobs.Add(new UploadJob { Path = path, Title = 100 < title.Length ? title.Substring(0, 100) : title });
                }
        }

        ShowUploadList();
        uploadStatus.Text = $"{uploadJobs.Count} video(s) to upload. Pick one to change its title and description.";
    }

    static void OnUploadRemove(object s, EventArgs e)
    {
        int index = uploadList.SelectedIndex;

        if (0 > index || index >= uploadJobs.Count)
        {
            uploadStatus.Text = "Pick a video to remove.";
            return;
        }

        uploadEditing = -1;
        uploadJobs.RemoveAt(index);
        ShowUploadList();
        uploadStatus.Text = $"{uploadJobs.Count} video(s) to upload.";
    }

    // Every video needs a title and description YouTube takes: a title of one line, not empty, 100 characters at most;
    // a description, chapters included, of 5000 characters at most; no < or > in either. The window closes only when
    // they all pass
    static void OnUploadGo(object s, EventArgs e)
    {
        KeepUploadEdits();

        string problem = 0 == uploadJobs.Count ? "Add the videos to upload first." : null;

        foreach (UploadJob job in uploadJobs)
            if (null == problem)
            {
                string name        = Path.GetFileName(job.Path);
                string description = DescriptionFor(job);

                if (0 == job.Title.Length)
                    problem = $"[{name}] has no title.";
                else if (0 <= job.Title.IndexOfAny(new[] { '\r', '\n' }))
                    problem = $"[{name}] has a title of more than one line; YouTube takes one.";
                else if (100 < job.Title.Length)
                    problem = $"[{name}] has a title of {job.Title.Length} characters; YouTube takes 100 at most.";
                else if (5000 < description.Length)
                    problem = $"[{name}] has a description of {description.Length} characters with its chapters; YouTube takes 5000 at most.";
                else if (0 <= job.Title.IndexOfAny(new[] { '<', '>' }) || 0 <= description.IndexOfAny(new[] { '<', '>' }))
                    problem = $"[{name}] has a < or > in its title, description or chapters, which YouTube does not take.";
            }

        if (null != problem)
        {
            MessageBox.Show(problem, "Upload Videos", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        uploadChosen = true;
        HideParentForm(s);
    }

    // a picked provider starts on its own model, unless a model of the user's own is in the box
    static void OnAIProviderPicked()
    {
        int provider = aiProviderCombo.SelectedIndex;

        if (0 <= provider && provider < aiDefaultModels.Length &&
            (string.IsNullOrWhiteSpace(aiModelInput.Text) || aiDefaultModels.Contains(aiModelInput.Text.Trim())))
            aiModelInput.Text = aiDefaultModels[provider];
    }

    // the provider, model and key for every channel, and the prompt for the open one, into youtube-channels.yaml
    static void SaveAISettings()
    {
        settings.AIProvider = aiProviders[Math.Max(0, aiProviderCombo.SelectedIndex)];
        settings.AIModel    = aiModelInput.Text.Trim();
        settings.AIKey      = aiKeyInput.Text.Trim();

        YouTubeChannel open = settings.Channels.Find(c => settings.LastChannel == c.ChannelId);

        if (null != open)
            open.AIPrompt = aiPromptArea.Text.Trim();

        GPAL.Converter
            .WithInput(settings)
            .SaveTo((GPALFile)ChannelsFile);
    }

    // the window's list, boxes and buttons off while the AI writes, so nothing is changed under it
    static void SetUploadBusy(bool busy)
    {
        uploadList.Enabled       = !busy;
        uploadTitleInput.Enabled = !busy;
        uploadDescArea.Enabled   = !busy;
        uploadCancelBtn.Enabled  = !busy;
        uploadAddBtn.Enabled     = !busy;
        uploadRemoveBtn.Enabled  = !busy;
        uploadAIBtn.Enabled      = !busy;
        uploadGoBtn.Enabled      = !busy;
    }

    // Asks the AI for a title and description for each video in the list, from the prompt, the file name and the
    // notes file beside the video. A video whose answer has no TITLE: and DESCRIPTION: keeps what it had
    static void OnWriteWithAI(object s, EventArgs e)
    {
        // the list is held here: a window opened again later has a list of its own
        List<UploadJob> jobs = uploadJobs;

        try
        {
            string provider = aiProviders[Math.Max(0, aiProviderCombo.SelectedIndex)];
            string model    = aiModelInput.Text.Trim();
            string key      = aiKeyInput.Text.Trim();
            string prompt   = aiPromptArea.Text.Trim();

            if (0 == jobs.Count)
            {
                uploadStatus.Text = "Add the videos to upload first.";
                return;
            }

            if (0 == key.Length || 0 == model.Length || 0 == prompt.Length)
            {
                uploadStatus.Text = "The Write with AI tab needs a model, an API key and a prompt.";
                return;
            }

            SaveAISettings();

            // what was typed goes on its video and the boxes are let go, so they cannot write over the answers
            ShowUploadList();

            int  written  = 0;
            bool answered = true;

            for (int i = 0; i < jobs.Count && true == answered; i++)
            {
                UploadJob job  = jobs[i];
                string    name = Path.GetFileName(job.Path);

                uploadStatus.Text = $"Writing {i + 1} of {jobs.Count}: [{name}]...";

                // <video name>.txt, then <video name>.md, beside the video
                string notesPath = Path.ChangeExtension(job.Path, ".txt");
                if (false == File.Exists(notesPath))
                    notesPath = Path.ChangeExtension(job.Path, ".md");

                string notes = true == File.Exists(notesPath) ? File.ReadAllText(notesPath).Trim() : "(none)";

                GPAL.AI
                    .WithProvider(provider)
                    .WithCredentials(key)
                    .WithModel(model)
                    .WithTask(AITask.TextGeneration)
                    .WithTaskConfig(new ClassificationConfig
                    {
                        PromptTemplate       = "{0}",
                        AdditionalParameters = new Dictionary<string, object> { ["MaxTokens"] = 1500 },
                    })
                    .WithInputFrom($"{prompt}\n\nFile name: {name}\n\nNotes:\n{notes}")
                    .WithOutputTo(out IGPALGrid<string> results)
                    .Execute();

                string answer = results?.FirstOrDefault()?[0] ?? string.Empty;

                // TITLE: up to DESCRIPTION:, then the rest
                int titleAt       = answer.IndexOf("TITLE:", StringComparison.OrdinalIgnoreCase);
                int descriptionAt = answer.IndexOf("DESCRIPTION:", StringComparison.OrdinalIgnoreCase);

                if (0 == answer.Trim().Length)
                {
                    // no answer at all is the provider, model or key, and the next video would get the same
                    answered = false;
                    GPAL.PublishSimpleEvent(GPALEventType.WARNING, $"No answer from the AI for [{name}], so Write with AI stopped. Check the provider, model and key");
                }
                else if (0 <= titleAt && titleAt < descriptionAt)
                {
                    // the title is the first line with words on it, without the bold or quotes some models put around it; the
                    // description is kept as written
                    string title = answer.Substring(titleAt + "TITLE:".Length, descriptionAt - titleAt - "TITLE:".Length)
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim().Trim('*', '"').Trim())
                        .FirstOrDefault(l => 0 < l.Length) ?? string.Empty;

                    job.Title       = 100 < title.Length ? title.Substring(0, 100).Trim() : title;
                    job.Description = answer.Substring(descriptionAt + "DESCRIPTION:".Length).Trim();
                    written++;
                }
                else
                    GPAL.PublishSimpleEvent(GPALEventType.WARNING, $"The AI's answer for [{name}] had no TITLE: and DESCRIPTION:, so it keeps its title and description");
            }

            // the window this run belongs to only; one opened since has its own list
            if (jobs == uploadJobs)
            {
                ShowUploadList();
                uploadStatus.Text = written == jobs.Count
                    ? $"Wrote {written} title(s) and description(s). Pick each video to check them before you upload."
                    : $"Wrote {written} of {jobs.Count}. See the GPAL Log in the main window for why.";
            }
        }
        finally
        {
            // a window opened since has its own buttons, already on
            if (jobs == uploadJobs)
                SetUploadBusy(false);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static YouTubeVideoInfo SelectedVideo()
    {
        int idx = videosList.SelectedIndex;
        YouTubeVideoInfo video = idx >= 0 && idx < videos.Count ? videos[idx] : null;

        if (null == video)
            SetStatus("Select a video first.");

        return video;
    }

    // a small form with the channel's playlists in a combo box. null when cancelled or there are none
    static YouTubePlaylistInfo PickPlaylist(string title, string caption)
    {
        if (0 == playlists.Count)
        {
            SetStatus("There are no playlists on the channel.");
            return null;
        }

        var titles = new string[playlists.Count];
        for (int i = 0; i < playlists.Count; i++)
            titles[i] = playlists[i].Title;

        pickedIndex = -1;
        playlistCombo.ClearItems();
        playlistCombo.WithItems(titles).WithSelectedIndex(0);

        GPAL.Form
            .WithTitle(title)
            .WithWidth(420)
            .WithFormControl(GPAL.LabelFor(caption))
            .WithFormControl(playlistCombo)
            .WithFormControl(GPAL.SpacerFor(12))
            .WithFormControl(pickOkBtn)
            .WithFormControl(pickCancelBtn)
            .ShowDialog();

        return pickedIndex >= 0 && pickedIndex < playlists.Count ? playlists[pickedIndex] : null;
    }

    static IGPALGrid<string> BuildExportGrid()
    {
        var headers = new List<string>();
        if (cbTitle.Checked)       headers.Add("Title");
        if (cbDescription.Checked) headers.Add("Description");
        if (cbTags.Checked)        headers.Add("Tags");
        if (cbUrl.Checked)         headers.Add("URL");
        if (cbPublished.Checked)   headers.Add("Published Date");
        if (cbVideoId.Checked)     headers.Add("Video ID");

        IAllowGridActions<string> grid = GPAL.Grid.AddRow(headers);

        foreach (YouTubeVideoInfo v in videos)
        {
            var row = new List<string>();
            if (cbTitle.Checked)       row.Add(v.Title       ?? string.Empty);
            if (cbDescription.Checked) row.Add(v.Description ?? string.Empty);
            if (cbTags.Checked)        row.Add(v.Tags != null ? string.Join("; ", v.Tags) : string.Empty);
            if (cbUrl.Checked)         row.Add(v.Url         ?? string.Empty);
            if (cbPublished.Checked)   row.Add(v.PublishedAt.HasValue ? v.PublishedAt.Value.ToString("yyyy-MM-dd") : string.Empty);
            if (cbVideoId.Checked)     row.Add(v.VideoId     ?? string.Empty);
            grid = grid.AddRow(row);
        }

        return grid.ToGPALObject();
    }

    // one operation at a time; the buttons that start one are off while it runs
    static void SetBusy(bool busy)
    {
        refreshVideosBtn.Enabled      = !busy;
        replaceBtn.Enabled            = !busy;
        addToPlaylistBtn.Enabled      = !busy;
        removeFromPlaylistBtn.Enabled = !busy;
        deleteBtn.Enabled             = !busy;
        exportBtn.Enabled             = !busy;
        exportChaptersBtn.Enabled     = !busy;
        refreshPlaylistsBtn.Enabled   = !busy;
        removeDeletedBtn.Enabled      = !busy;
        switchChannelBtn.Enabled      = !busy;
        uploadBtn.Enabled             = !busy;
    }

    static void HideParentForm(object sender)
    {
        Control ctrl = (Control)sender;
        while (ctrl.Parent != null) ctrl = ctrl.Parent;
        ctrl.Hide();
    }

    static void OnAbout(object s, EventArgs e)
    {
        GPAL.Form
            .WithTitle("About ChannelManager")
            .WithWidth(430)
            .WithFormControl(aboutText)
            .WithFormControl(GPAL.SpacerFor(12))
            .WithFormControl(moreBotsLink)
            .WithFormControl(GPAL.SpacerFor(12))
            .WithFormControl(aboutOkBtn)
            .ShowDialog();
    }

    static void SetStatus(string msg) => statusBar.Text = msg;

    // ── GPAL event handler ────────────────────────────────────────────────────

    static void OnGPALEvent(object sender, GPAL.GPALEventArgs e)
    {
        string tag = e.GPALEventType == GPALEventType.WARNING   ? "WARN"
                   : e.GPALEventType == GPALEventType.ERROR     ? "ERR "
                   : e.GPALEventType == GPALEventType.EXCEPTION ? "EXCP"
                   : "INFO";
        string line = $"[{e.DateTimeStamp:HH:mm:ss}] [{tag}] {e.Message}";
        if (e.ExceptionRaised != null) line += $"  ({e.ExceptionRaised.Message})";
        System.Diagnostics.Debug.WriteLine(line);

        // AppendLine marshals to the UI thread and keeps the last 2000 lines. Events published on the credentials form,
        // before the channel form shows, have no log yet.
        if (true == logArea.IsReady)
            logArea.AppendLine(line, 2_000);
    }
}
