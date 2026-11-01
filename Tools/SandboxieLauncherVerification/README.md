# Isolated Sandboxie launcher verification

Run from the repository root:

```powershell
dotnet run --project Tools/SandboxieLauncherVerification/Checks.csproj
```

These executable checks use temporary placeholder build files, a fake Sandboxie client and fake Steam identities. They cover configuration validation before process starts, existing Steam reuse, distinct-account gating, explicit host opt-in, four-context orchestration, failed-startup rollback, conflicting games, receipt preservation and stopping despite another recorded PID being reused.

They do not start native applications, load Steam DLLs, authenticate accounts, exercise Sandboxie, or demonstrate multiplayer connectivity. Actual-machine evidence is summarized separately in [the launcher validation record](../SandboxieLauncher/VERIFICATION.md).
