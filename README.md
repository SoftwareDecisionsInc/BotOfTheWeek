# GPAL Bot of the Week

A new GPAL bot every week: a working program built on [GPAL](https://gpal.sditx.com/), the Generally Positive
Automation Library, with its full source and a walkthrough of how it works.

| Bot | What it does | Published | Program | Video | Walkthrough |
|---|---|---|---|---|---|
| [CastManager](./CastManager) | Keeps a list of favorites, websites, local files, videos and movies, and casts the one you pick to a named Cast device. Tab or desktop. The browser runs on a hidden desktop, so the stream plays on the TV while your screen, mouse and keyboard stay yours. The list is saved as YAML through GPAL.Converter, and the first entry is loaded in the browser on startup so the first cast is instant. | 2026-10-19 | [Download](https://gpal.sditx.com/download/botw/castmanager) | [Watch](https://www.youtube.com/watch?v=qflCUNZxpH4) | [Walkthrough](https://gpal.sditx.com/docs/botw/botw-castmanager) |

## Build

Open `BotOfTheWeek.sln` in Visual Studio 2022 (.NET Framework 4.8) and build. Every bot builds against the GPAL
package on nuget.org, version 1.0.2, which Visual Studio restores the first time. Each bot also opens on its
own through its project file.

The Program link is each bot ready to run: unzip anywhere and start its exe, signed by Software Decisions, Inc.

## This repository

This is the read-only public release mirror of the Bot of the Week programs. Development happens privately, and
pull requests are not accepted here. Every video: https://www.youtube.com/playlist?list=PLNAxd-4mdRs_272uxvAetn6EWT8CENurF

## License

Source available under the **Business Source License 1.1**, with the same Additional Use Grant as GPAL. Full terms:
[LICENSE.md](./LICENSE.md). Privacy: [privacy.md](./privacy.md).

Copyright (c) 2026 Software Decisions, Inc. All rights reserved.