# Third-party components

The application uses .NET / ASP.NET Core / WPF (MIT), Microsoft.Data.Sqlite
(MIT), SQLite (public domain), SQLitePCLRaw (Apache-2.0), Microsoft SignalR
JavaScript client (MIT), and Microsoft Edge WebView2 SDK (Microsoft license).
NuGet and npm packages retain their respective copyright and license notices.
The bundled browser SignalR client is unmodified; its license is in
`wwwroot/vendor/signalr.LICENSE.txt`.

Optional portable builds include the separate `ffmpeg.exe` and `ffprobe.exe`
processes from Gyan's FFmpeg 9.1 essentials Windows build. Their license is
GPL v3. `tools/FFmpeg-LICENSE.txt` contains the license; `tools/FFmpeg-README.txt`
contains the distributor's complete component/build information.

- Distributor: https://www.gyan.dev/ffmpeg/builds/
- Exact archive: https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.1-essentials_build.zip
- Archive SHA-256: `b9bbbb3acece000cbeec994f856649354e52c773807841e78908374b24d658fd`
- FFmpeg source revision, as identified by its distributor: https://github.com/FFmpeg/FFmpeg/commit/74e4393595
- FFmpeg license information: https://ffmpeg.org/legal.html

FFmpeg is invoked as a separate process with argument arrays; it is not linked
into the application. For redistribution, retain the distributor's notices and
provide corresponding source for FFmpeg and its enabled libraries as required
by their licenses. Builds without `-IncludeFFmpeg` expect user-provided tools.
