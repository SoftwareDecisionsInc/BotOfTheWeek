# GPAL Privacy Policy

**Effective: September 2026**
**Software Decisions, Inc. (SDi)**

This policy covers everything SDi publishes for GPAL: the **GPAL** library, **GPAL Studio**, the **GPAL REST API** desktop app, the **OttoMatic Desktop** recorder, the **OttoMatic** and **OttoMagic** browser extensions, and the **Bot of the Week** programs.

## Summary

**Software Decisions, Inc. does not collect, receive, store, sell, or share any of your data.** None of these products has analytics, telemetry, tracking, advertising, or accounts, and none of them contacts SDi. Nothing they read or produce is ever sent to SDi.

Everything they work with stays on your own computer, or goes only where your own automation sends it.

## The GPAL library and your automation

GPAL is a library that runs inside programs you write or run. It does only what that program tells it to:

- it opens the websites, applications, files, databases, mail servers, and online services (such as AI providers or Google Sheets) that your automation names, using the credentials you give it,
- it writes only the files your automation asks for, plus its own settings file (GPAL.yaml) and temporary files, kept beside the program,
- it creates no log files of its own. The events it reports go only where your program sends them.

Credentials you give GPAL are used to reach the service they belong to, and are not sent anywhere else.

**Browser drivers.** When you automate a browser with Selenium and its driver is out of date, GPAL downloads a matching driver from the browser maker's public download site (Google for Chrome, Microsoft for Edge, Mozilla for Firefox). These downloads carry no data about you or your automation.

## GPAL Studio

GPAL Studio builds automation on your computer. The workflows, selectors, and settings you create are saved as files on your computer. It exchanges data with OttoMatic and OttoMagic only through the GPAL REST API on the same computer.

## The GPAL REST API

The GPAL REST API is a desktop app that connects the browser extensions to GPAL and GPAL Studio. It listens for requests from programs on your computer and, where Windows allows it, from other devices on your local network, so you can control a browser from another machine you own. It passes data only between the extensions and the program that asked for it, and sends nothing to SDi.

## OttoMatic Desktop

OttoMatic Desktop records the steps you perform in Windows programs so you can turn them into GPAL automation code. It only records while you have recording turned on, and it watches the mouse and keyboard only then. A recorded step can include:

- the program and window you were working in,
- details of the control you used (its name, type, and position in the window),
- the text in a field you recorded, read from the field itself rather than from your keystrokes,
- keys that move between or act on fields, such as Enter, Tab, and shortcuts using Ctrl or Alt,
- a small screenshot of the control, for image matching.

What it records stays in its window on your computer. It leaves only when you copy it to the clipboard or save it as a file yourself. OttoMatic Desktop has no network connection of any kind.

## The browser extensions

### OttoMatic (recorder)

OttoMatic records the steps you perform in the browser so you can turn them into GPAL automation code. It only records while you have recording turned on. A recorded step can include:

- the address of the page you were on,
- details of the element you interacted with (its tag, attributes, text, and position on the page),
- values you typed or selected in form fields you recorded,
- screenshots of the element or page, when you ask for them.

### OttoMagic (player)

OttoMagic carries out the commands of the GPAL automation you run on your computer: opening pages, clicking, typing, reading page content, taking screenshots, managing cookies and downloads, and making web requests. It does this only when your own automation tells it to, and it only reads what that automation asks for. OttoMagic also keeps activity and error logs in local extension storage so you can troubleshoot your automation.

### Where the extensions keep data

- **Local extension storage.** Recorded steps, settings, and logs are kept in the browser's storage for the extension on your computer.
- **The GPAL REST API on your computer.** When "Run API" is turned on, the extensions exchange data with the GPAL REST API over the browser's native messaging channel. It passes the data to GPAL Studio or to your own GPAL code.
- **Files you save.** Data leaves an extension as a file only when you copy, download, or export it yourself.

## Bot of the Week programs

Each Bot of the Week is a GPAL program you download and run yourself. It follows the same rules as the GPAL library: it reaches only what the program is written to reach, and keeps its settings in a file beside it. Each one ships with its full source, so you can see exactly what it does.

## Third parties

None of these products shares data with third parties. The only exceptions are actions you start yourself:

- the websites, applications, and services your automation opens or calls receive what your automation sends them, just as if you had used them yourself,
- OttoMatic's **Translate** context-menu item opens Google Translate in a new tab with the text you selected,
- browser driver downloads, described above.

## Your controls

- **Recording** happens only while you have it turned on in OttoMatic or OttoMatic Desktop.
- **Run API** in OttoMatic's settings turns the connection to the GPAL REST API on or off at any time.
- **Erase** in OttoMatic deletes your recorded workflow. OttoMagic's settings let you clear its logs, or clear them on every start.
- **Uninstalling** an extension removes all of its stored data from the browser. Uninstalling the GPAL REST API removes it from your computer. The GPAL library, OttoMatic Desktop, and the Bot of the Week programs are removed by deleting them.

## Accessing your data

Because all data stays on your computer, you can see it at any time: in the files your automation and GPAL Studio write, in OttoMatic's popup and OttoMatic Desktop's window for recorded steps, and in OttoMagic's popup for its activity and error logs. SDi holds no copy, so there is nothing for us to provide, correct, or delete on request.

## Children

These products are developer tools and are not directed at children under 13.

## Changes to this policy

We will update this policy whenever a new feature changes how any of these products handles data, and change the effective date above.

## Contact

Questions about this policy: [GPAL][@][sditx.com]
