WebM for Retards [![Build status](https://ci.appveyor.com/api/projects/status/g0gybayk8rw86aa1/branch/master?svg=true)](https://ci.appveyor.com/project/nixxquality/webmconverter/branch/master)
=============
A wrapper around ffmpeg and AviSynth made for converting videos to WebM without having to use the command line.

> **About this fork:** this is [toasty-wheeler/WebMConverter](https://github.com/toasty-wheeler/WebMConverter), a fork of
> [nixxquality/WebMConverter](https://github.com/nixxquality/WebMConverter) that also includes every update published on
> [gitgud.io/nixx/WebMConverter](https://gitgud.io/nixx/WebMConverter) (up to v2.26.1). What it adds:
>
> - **Live preview panel** between the Main box and the option tabs. It shows the real output of your edits (crop, trim, captions,
>   subtitles, rate, rotate and so on, or your own Advanced script) and updates as you change them. Seek with the slider,
>   step with the arrow keys or mouse wheel, Space plays and pauses. "Hide preview" in the status bar collapses it.
> - **Dark mode**: the theme button in the status bar switches between Automatic (follows Windows), Dark and Light.
> - A resizable main window.
> - Targets .NET Framework 4.8. Build with Visual Studio 2017 or newer: restore NuGet packages, then build `WebMConverter.sln` (Release, x86).
>
> The download link and issue tracker below are upstream's.

- **Download it [here][LatestDownload].**
- Don't run ffmpeg.exe you retard, run WebMConverter.exe.
- And I shouldn't have to tell you to extract the zip file first.

Important to know:
* Requires .NET Framework 4.5 (Windows 7 comes with 3.5, so you might want to [update][DotNet45])
* Requires [AviSynth][AviSynth] (2.6.0, 32-bit)
* Already includes ffmpeg
* Only works on Windows (I assume Linux users don't need GUIs)

This software is released under the MIT license. See LICENSE.md.

If you have any issues with this program, you may report them on [GitGud][NewIssue] or you can email me at `nixx@is-fantabulo.us`.

 [LatestDownload]: https://nixx.is-fantabulo.us/WebM%20for%20Retards/latest.zip
 [DotNet45]: https://www.microsoft.com/en-us/download/details.aspx?id=30653
 [AviSynth]: http://avisynth.nl/index.php/Main_Page#Official_builds
 [NewIssue]: https://gitgud.io/nixx/WebMConverter/issues/new
