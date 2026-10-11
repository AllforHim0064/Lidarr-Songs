# Lidarr Tracks validation

- Backend: Linux x64 .NET 8 Core, HTTP API, Host and Console projects compiled successfully.
- Frontend: production webpack build succeeded; the two changed JavaScript files passed ESLint.
- Regression tests: 59 passed, 0 failed (NUnit). Includes the new matching/selection cases, selected-song completion/restart tests, Newznab request generation, existing completed downloads, tracked-download history and failed-download handling.
- Startup smoke test: a new isolated configuration initialized SQLite databases and started the HTTP server.
- API smoke test: the Soulseek jobs endpoint returned 200; invalid track search returned 400; an invalid grab token returned 409; the frontend returned 200; the scheduled Soulseek importer appeared in the task list.
- Patch hygiene: git diff --check passed.

The workspace required direct MSBuild invocation and an in-process NUnitLite runner because its process metadata prevented the standard dotnet/VSTest launcher from working reliably. These are workspace constraints, not changes to the fork's test suite. Backend analyzer execution was disabled for this build. The tests remain in the upstream NUnit project and can be run with the normal tooling on a standard host.

Not validated: live account downloads, private indexer results, torrent/Usenet client cleanup during real seeding, the Docker image build, or Windows/macOS runtime packages. The fork supports manual song selection; automatic per-track monitoring/retries and selective file priorities within album torrents are not implemented. Album fallback intentionally transfers the album and imports only the selected song.
