# Privacy Policy for Screeny

*Last updated: November 27, 2026*

Screeny keeps usage history and diagnostic logs on your device. It does not upload those files, send telemetry, or provide cloud sync or cloud backup.

## Usage history

While tracking is on, Screeny checks which app is in the foreground and records periods of use or inactivity. A saved session includes a record ID, process name, display name, date, start and end times, duration, focus status, and the time Screeny saved the record. The display name can come from the executable's product name.

Screeny reads window titles to distinguish sessions. It looks up executable paths to get app names and icons. Window titles, executable paths, process IDs, and window handles are used while Screeny runs and are not saved as usage fields. A title change can still start a new session, so its timing can show up in your history.

Screeny saves history in an ordinary SQLite database, `screentime.db`, without app-level encryption. SQLite can create companion journal files. Screeny can also keep a renamed copy of a database it finds to be corrupt. For an unpackaged copy, these files are under `%LOCALAPPDATA%\ScreenTimeTracker`. Windows can redirect files for a packaged copy.

## Diagnostic logs

Screeny appends to a separate app data file, `app_log.txt`, at launch. The log can contain timestamps, unredacted launch arguments, the computer name, the working folder and the app's folder, and Windows and .NET version details. Errors can add exception messages, stack traces, and other exception details. Screeny can also try to write `Screeny_ErrorLog.txt` next to its executable when an error occurs.

Treat the database, its companion files and backups, and the logs as private. Don't post raw files in a public support request.

## Reset and uninstall

You can view your history in Screeny. The tray menu's **Delete all data…** action clears usage records from the current database and discards queued sessions when deletion succeeds. It does not remove the database file, diagnostic logs, the file that marks the first launch, or backups. It is not a secure erase. If tracking resumes, Screeny can record new sessions.

When you uninstall a packaged copy, Windows can remove files it redirected into package-managed storage. A packaged copy can also use preexisting files from an unpackaged copy in the same Windows account, such as a diagnostic log. Those files can remain after uninstall, as can other files and backups stored outside the package-managed location. See [how Windows handles files for packaged desktop apps](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes#appdata-operations-on-windows-10-version-1903-and-later).

## Windows permissions

Screeny runs as a full-trust desktop app and can track usage in the background. Windows does not restrict it to its own data folder. Access to its files depends on Windows permissions, so other software or people with sufficient access can read them.

## Contact

For privacy questions, contact the developer through the [Screeny GitHub repository](https://github.com/ArnoGevorkyan/Screeny).
