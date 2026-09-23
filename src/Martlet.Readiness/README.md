# Private Desktop readiness protocol

`Martlet.Readiness` is an inert one-shot local report helper. It does not decide
whether Desktop is runnable and cannot activate a package. The launcher creates
a random private named pipe and nonce, then supplies a fixed environment bound
to protocol version, purpose, application version, profile ID, settings
revision, payload SHA-256, apphost SHA-256 and UTC deadline. The report adds the
actual current process ID.

The server additionally verifies the named-pipe client PID and queries the OS
process image path. It accepts only an exact canonical message before both its
monotonic and UTC deadlines. `Initialized` is the sole passing state;
`Degraded` and `Failed` are retained failures. Wrong/malformed/duplicate fields,
nonce, version, profile, settings revision, payload, apphost, PID, process path,
purpose, protocol or deadline fail closed.

A future Desktop entrypoint should create the helper only in the launcher's
private environment and report after its own bounded initialization has
actually completed:

```csharp
var readiness = DesktopReadinessReporter.CreateFromEnvironment();
// Load and validate the real profile/settings and initialize required services.
await readiness.ReportInitializedAsync(cancellationToken);
```

The public API provides separate fixed `ReportInitializedAsync`,
`ReportDegradedAsync` and `ReportFailedAsync` methods. It accepts no caller
boolean, arbitrary status enum, JSON, diagnostic text, path, digest, process ID
or deadline. Each reporter is one-use. Secrets and pipe names must not be logged
or persisted.

This protocol is local process correlation, not a sandbox or release
qualification. The child necessarily receives its nonce; current-user/admin
code remains outside this threat boundary. Real Desktop integration, packaged
startup/migration behavior, signing and clean-Windows qualification are
deferred.
