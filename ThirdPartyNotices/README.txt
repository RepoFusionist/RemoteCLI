Third-party notices for the Remote CLI desktop release
======================================================

This folder is copied to the build output and to the published application
directory (next to RemoteCli.Desktop.exe). The consolidated summary, component
inventory, and scope statements are in ..\THIRD-PARTY-NOTICES.txt.

The published EXE must be distributed together with this folder and with
..\THIRD-PARTY-NOTICES.txt. xterm.js and the shiki bundle carry no copyright
header inside their minified sources, so these texts are the only surviving
attribution for them.

Files:
- WebView2-LICENSE.txt          Microsoft.Web.WebView2 1.0.2903.40 (BSD-style)
                                Copied verbatim from the NuGet package.
- WebView2-NOTICE.txt           WebView2 third-party notices
                                Copied verbatim from the NuGet package.
- ModelContextProtocol-Apache-2.0.txt
                                Apache License 2.0, applied to
                                ModelContextProtocol / ModelContextProtocol.Core
                                2.3.0 (declared in the package nuspec as
                                license expression Apache-2.0). Verbatim text
                                from https://www.apache.org/licenses/LICENSE-2.0.txt.
- xterm.js-MIT.txt              xterm.js 6.0.0, xterm.css, and
                                @xterm/addon-serialize 0.14.0 (MIT).
- shiki-MIT.txt                 shiki 4.5.0 (MIT), used only by the optional
                                code-highlight UI plugin package. Copied from
                                the upstream shiki package LICENSE.
- shiki-bundle-NOTICE.txt       Attribution for the other MIT packages inside
                                the minified code-highlight bundle (@shikijs/*,
                                oniguruma-to-es, oniguruma-parser, regex,
                                regex-recursion, regex-utilities). The minifier
                                strips their copyright comments, so they are
                                listed explicitly.
- Microsoft.Extensions-MIT.txt  Microsoft.Extensions.* 10.0.13 and
                                Microsoft.Extensions.AI.Abstractions 10.11.0
                                (MIT).
- dotnet-runtime-MIT.txt        .NET 10 runtime (MIT), as bundled by the
                                self-contained desktop publish.
- dotnet-runtime-THIRD-PARTY-NOTICES.txt
                                Third-party notices for the .NET runtime
                                binaries (ICU, zlib, Brotli, and others).
                                Copied verbatim from the installed .NET 10 SDK
                                (C:\Program Files\dotnet\ThirdPartyNotices.txt).
- OfficeKit-MPL-2.0.txt         Mozilla Public License 2.0, declared by the
                                optional Office preview kit package.
- OfficeKit-LibreOffice-NOTICE.txt
                                Upstream LibreOffice NOTICE shipped inside the
                                optional Office preview kit package.

Office preview kit (IMPORTANT)
------------------------------
@deepseek-ai/libreoffice-kit-win32-x64 0.1.2 is an optional third-party runtime
download from its upstream npm package (MPL-2.0). It is a modified build of
LibreOffice 26.8.0.3. The desktop app fetches it on first use and installs it
under %LOCALAPPDATA%\RemoteCLI\OfficeKit; it is NOT bundled in the Remote CLI
desktop release or its ZIP package.

Do not redistribute the kit from this repository. The kit's own license texts
cover its notice obligations, but the MPL-2.0 source-availability requirement
for this exact modified build (and the LGPL source/relinking terms that apply if
a recipient elects an LGPL branch for Graphite2, Hunspell, or Hyphen) cannot be
satisfied by us. The MPL/NOTICE files here are included for transparency only
and do not create or transfer that upstream obligation.
