# Existing Steam sessions through Sandboxie

This Windows x64 console tool starts or reuses Steam in explicitly selected, already configured Sandboxie boxes, verifies their authenticated Steam identities, and launches an existing game build in those boxes. It does not install or configure Sandboxie/Steam, enter credentials, create boxes, rebuild games, or modify game files. Sign into each sandbox's Steam client manually before attempting the game launch.

One to four named boxes are accepted. An explicitly selected `DefaultBox` is supported. Add `--include-host` to also launch a normal game instance through the normal Steam client, with a total limit of four sessions. With that flag, the normal Steam account must authenticate and differ from every selected sandbox account before any game starts. Without the flag, normal Steam is neither launched nor probed, and distinct-account checks compare only the selected sandboxes.

Build with the installed .NET 10 SDK:

```powershell
dotnet build .\Tools\SandboxieLauncher\SandboxieLauncher.csproj
$launcher = '.\Tools\SandboxieLauncher\bin\Debug\net10.0\SandboxieLauncher.exe'
& $launcher inspect --start 'C:\Program Files\Sandboxie\Start.exe'
```

Use the installed Sandboxie path shown on your machine. `inspect` reads configured enabled boxes and their current Windows-session process identities. It does not start applications.

For an existing Corporate Hell build, set explicit paths and choose a new receipt in an existing directory outside both the game and Steam installation:

```powershell
$options = @(
    '--start', 'C:\Program Files\Sandboxie\Start.exe',
    '--steam', 'C:\Program Files (x86)\Steam\steam.exe',
    '--game', 'C:\Users\Leo\GameDev\Corporate Hell\Builds\Main\Corporate Hell.exe',
    '--box', 'DefaultBox',
    '--include-host',
    '--receipt', "$env:TEMP\playtestops-steam-session-001.json",
    '--timeout', '60'
)
& $launcher dry-run @options
& $launcher launch @options
```

Repeat `--box <name>` for additional existing boxes. Names must be distinct, 1–38 ASCII letters, digits or underscores. Do not launch other copies of the selected game in those boxes concurrently with this operation. The launcher refuses a box already running that executable. `dry-run` checks files, boxes and process conflicts but does not initialize Steam or establish authentication.

The native Steam DLL defaults to `<game name>_Data\Plugins\x86_64\steam_api64.dll`; use `--api <absolute path>` if the existing build uses another location within its build directory. The build must already contain a positive numeric `steam_appid.txt`. The tool never creates or updates it. The current Corporate Hell test build uses App ID 480, but other valid existing App IDs are supported.

Before starting any games, the launcher runs its own short-lived probe in each selected sandbox and, only with `--include-host`, in the normal session. The probe first verifies its native membership and then initializes the build's Steam API with modern `SteamAPI_InitFlat` (or legacy `SteamAPI_Init`/`SteamAPI_InitSafe` when the flat initializer is absent), followed by `BLoggedOn` and `GetSteamID`. The parent validates the probe's nonce, PID, box or unboxed host context, Windows session and process creation time. Every selected context must report an authenticated, nonzero Steam identity; duplicate identities abort before game startup. Host probing requires explicit opt-in; a boxed probe cannot masquerade as a normal host probe. Existing sandbox policies must permit the .NET launcher, the selected files and local TCP communication; failures do not trigger policy changes.

The probe uses the Steamworks flat API exports `SteamAPI_SteamUser_v023` through `v019`. Unsupported native DLL versions fail explicitly. Local TCP carries one bounded response without credentials or account names. Console output hashes Steam IDs; the local receipt contains the raw IDs for local evidence, so keep receipts private and out of source control.

After game startup, Sandboxie must report one unambiguous new matching game process in each requested box. The receipt records the PID, image name, box, session and creation time. On partial failure, the launcher attempts to recover and stop only unambiguous new game processes it observed; it does not terminate an entire box. A very late process or ambiguous external launch requires manual inspection. Steam stays running after failure and normal stop.

To stop games recorded by a previous launch:

```powershell
& $launcher stop --include-host --start 'C:\Program Files\Sandboxie\Start.exe' --receipt "$env:TEMP\playtestops-steam-session-001.json"
```

`stop` checks process identity again and terminates only matching recorded game processes. Receipts containing a normal host game require `--include-host` on stop as well. Normal host game identity additionally includes its full executable path and proof that the PID is unboxed. A reused PID or changed identity is refused. Steam, unrelated processes and sandbox contents remain intact. Do not edit receipts or use receipts from another Windows session. A stopped/failed receipt remains local evidence and is never reused as a new output path.

## Evidence boundaries

Successful launch proves authenticated Steam identity in each requested context and observed game processes. It does not prove that the game itself initialized Steam under that identity, joined a lobby, used a particular Mirror transport, connected to another player, or stayed healthy after observation. When selecting one box without `--include-host`, there is no second selected account to compare. Verify actual game identity, lobby and multiplayer behavior separately through the existing game's UI/logs.

The tool does not integrate with the dashboard's approved-build streaming, WebRTC input, Arena/KCP profile, game-readiness messages, or Unity Editor bridge. Those contracts are unchanged. Source/fixture checks are separate from live Sandboxie/Steam/game evidence.

Native Sandboxie declarations follow the project's `SbieApi_QueryProcessEx2`, `SbieApi_EnumProcessEx` and process identity contract. Launch uses `Start.exe /box:<explicit name> /silent <executable> ...` with structured arguments; a zero launcher exit code alone is not readiness. See the [Sandboxie command interface](https://sandboxie-plus.github.io/sandboxie-docs/Content/StartCommandLine.html) and [Sandboxie source](https://github.com/sandboxie-plus/Sandboxie).

Run the isolated checks with `dotnet run --project Tools/SandboxieLauncherVerification/Checks.csproj`. See [the validation record](VERIFICATION.md) for live-test coverage and its limits.
