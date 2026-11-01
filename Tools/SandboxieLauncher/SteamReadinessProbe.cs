using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PlaytestOps.Sandboxie;

public sealed record ProbeResponse(string Nonce, string BoxName, int Pid, uint SessionId, long CreationTime, ulong SteamId, string? Error);

public sealed class SteamReadinessProbe(ISandboxieClient client) : ISteamReadinessProbe {
    public async Task<ulong> GetSteamIdAsync(string box, LauncherOptions options, CancellationToken cancellationToken) {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot resolve launcher executable.");
        var arguments = new List<string>();
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            arguments.Add(Assembly.GetExecutingAssembly().Location);
        arguments.AddRange(["--internal-probe", "--start", options.StartPath, "--box", box,
            "--api", options.SteamApiPath, "--port", port.ToString(), "--nonce", nonce,
            "--timeout", options.TimeoutSeconds.ToString()]);
        if (box == HostSessionClient.Host) {
            if (!options.IncludeHost) throw new InvalidOperationException("Host Steam probing requires explicit --include-host.");
            arguments.Add("--allow-host");
        }
        var baseline = client.GetProcesses(box).Select(process => (process.Pid, process.CreationTime)).ToHashSet();
        await client.LaunchAsync(box, executable, Path.GetDirectoryName(options.GamePath)!, arguments, deadline.Token);
        using var connection = await listener.AcceptTcpClientAsync(deadline.Token);
        using var reader = new StreamReader(connection.GetStream(), Encoding.UTF8, false, 1024, true);
        var characters = new char[4097];
        var count = 0;
        while (count < characters.Length) {
            var read = await reader.ReadAsync(characters.AsMemory(count, 1), deadline.Token);
            if (read == 0 || characters[count] == '\n') break;
            count += read;
        }
        if (count == 0 || count >= characters.Length) throw new InvalidDataException("Sandbox probe returned an invalid response size.");
        var response = JsonSerializer.Deserialize<ProbeResponse>(new string(characters, 0, count))
            ?? throw new InvalidDataException("Sandbox probe response was empty.");
        var process = client.GetProcess(response.Pid);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(nonce), Encoding.UTF8.GetBytes(response.Nonce ?? ""))
            || !string.Equals(response.BoxName, box, StringComparison.OrdinalIgnoreCase)
            || process is null || !string.Equals(process.BoxName, box, StringComparison.OrdinalIgnoreCase)
            || process.SessionId != response.SessionId || process.CreationTime != response.CreationTime
            || process.SessionId != (uint)Process.GetCurrentProcess().SessionId
            || !Launcher.ImageMatches(process.ImageName, executable)
            || baseline.Contains((process.Pid, process.CreationTime)))
            throw new InvalidDataException("Sandbox probe identity could not be verified.");
        // Acknowledge only after verifying native membership while the probe remains alive.
        await connection.GetStream().WriteAsync(new byte[] { 1 }, deadline.Token);
        if (response.Error is not null || response.SteamId == 0)
            throw new InvalidOperationException($"Steam is not ready in {box}: {response.Error ?? "no authenticated identity"}");
        return response.SteamId;
    }

    public static async Task<int> RunInternalAsync(string startPath, string box, string apiPath, int port, string nonce, int timeoutSeconds, bool allowHost = false) {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        // Never initialize Steam until native membership establishes that this is the selected sandbox.
        var process = QuerySelf(startPath);
        if (allowHost && box == HostSessionClient.Host && process is null) {
            using var current = Process.GetCurrentProcess();
            process = new(current.Id, HostSessionClient.Host, current.MainModule?.FileName ?? "", checked((uint)current.SessionId),
                current.StartTime.ToUniversalTime().ToFileTimeUtc());
        }
        if (process is null || !string.Equals(process.BoxName, box, StringComparison.OrdinalIgnoreCase)) return 4;
        ulong steamId = 0;
        string? error = null;
        try { steamId = await ReadIdentityAsync(apiPath, deadline.Token); }
        catch (Exception exception) when (exception is not AccessViolationException) { error = exception.Message; }
        var response = new ProbeResponse(nonce, box, Environment.ProcessId, process.SessionId, process.CreationTime, steamId, error);
        using var connection = new TcpClient();
        await connection.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response) + "\n");
        await connection.GetStream().WriteAsync(payload, deadline.Token);
        var acknowledgment = new byte[1];
        await connection.GetStream().ReadExactlyAsync(acknowledgment, deadline.Token);
        return steamId != 0 && acknowledgment[0] == 1 ? 0 : 3;
    }

    private static async Task<ulong> ReadIdentityAsync(string apiPath, CancellationToken cancellationToken) {
        if (!Path.IsPathFullyQualified(apiPath) || !File.Exists(apiPath)) throw new InvalidOperationException("Steam API DLL is unavailable inside the sandbox.");
        if (!File.Exists("steam_appid.txt") || !uint.TryParse(File.ReadAllText("steam_appid.txt").Trim(), out var appId) || appId == 0)
            throw new InvalidOperationException("Sandbox-visible steam_appid.txt must already contain a valid positive App ID.");
        var library = NativeLibrary.Load(apiPath);
        var initialized = false;
        Action? shutdown = null;
        try {
            var initialize = ResolveInitializer(library);
            shutdown = Marshal.GetDelegateForFunctionPointer<VoidCall>(NativeLibrary.GetExport(library, "SteamAPI_Shutdown")).Invoke;
            var loggedOn = Marshal.GetDelegateForFunctionPointer<UserBoolCall>(NativeLibrary.GetExport(library, "SteamAPI_ISteamUser_BLoggedOn"));
            var getSteamId = Marshal.GetDelegateForFunctionPointer<UserIdCall>(NativeLibrary.GetExport(library, "SteamAPI_ISteamUser_GetSteamID"));
            var runCallbacks = NativeLibrary.TryGetExport(library, "SteamAPI_RunCallbacks", out var callbacksExport)
                ? Marshal.GetDelegateForFunctionPointer<VoidCall>(callbacksExport) : null;
            UserCall? getUser = null;
            for (var version = 23; version >= 19; version--) {
                if (NativeLibrary.TryGetExport(library, $"SteamAPI_SteamUser_v{version:000}", out var export)) {
                    getUser = Marshal.GetDelegateForFunctionPointer<UserCall>(export);
                    break;
                }
            }
            if (getUser is null) throw new InvalidOperationException("Steam API does not expose a supported SteamUser interface (v019–v023).");
            string? initializationError = null;
            while (true) {
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(initializationError ?? "Steam readiness timed out.", cancellationToken);
                if (!initialized) {
                    var attempt = initialize();
                    initialized = attempt.Success;
                    initializationError = attempt.Error;
                }
                if (initialized) {
                    runCallbacks?.Invoke();
                    var user = getUser();
                    if (user != IntPtr.Zero && loggedOn(user)) {
                        var identity = getSteamId(user);
                        if (identity != 0) return identity;
                    }
                }
                try { await Task.Delay(250, cancellationToken); }
                catch (OperationCanceledException) when (!initialized && initializationError is not null) {
                    throw new OperationCanceledException(initializationError, cancellationToken);
                }
            }
        } finally {
            if (initialized) shutdown?.Invoke();
            NativeLibrary.Free(library);
        }
    }
    private static Func<(bool Success, string? Error)> ResolveInitializer(IntPtr library) {
        // Valve's modern steam_api.h defines Init/InitEx inline in C++ and exports
        // InitFlat for dynamic bindings. ESteamAPIInitResult is an int with OK == 0;
        // steam_api_common.h defines SteamErrMsg as char[1024], not a WCHAR buffer.
        // https://github.com/rlabrecque/SteamworksSDK/blob/main/public/steam/steam_api.h
        // https://github.com/rlabrecque/SteamworksSDK/blob/main/public/steam/steam_api_common.h
        if (NativeLibrary.TryGetExport(library, "SteamAPI_InitFlat", out var flatExport)) {
            var initialize = Marshal.GetDelegateForFunctionPointer<InitFlatCall>(flatExport);
            return () => {
                var error = new byte[1024];
                var result = initialize(error);
                if (result == 0) return (true, null);
                var end = Array.IndexOf(error, (byte)0);
                var message = Encoding.UTF8.GetString(error, 0, end < 0 ? error.Length : end);
                if (message.Length > 512) message = message[..512];
                return (false, $"SteamAPI_InitFlat failed (result {result}): {message}");
            };
        }
        // Older SDKs export a C++ bool-returning Init or compatibility InitSafe.
        // Never try another initialization mode after an actual Flat failure.
        foreach (var name in new[] { "SteamAPI_Init", "SteamAPI_InitSafe" }) {
            if (NativeLibrary.TryGetExport(library, name, out var export)) {
                var initialize = Marshal.GetDelegateForFunctionPointer<BoolCall>(export);
                return () => initialize() ? (true, null) : (false, $"{name} returned false.");
            }
        }
        throw new InvalidOperationException("Steam API exposes none of the supported initialization exports (InitFlat, Init, InitSafe).");
    }
    private static BoxProcess? QuerySelf(string startPath) {
        var library = NativeLibrary.Load(Path.Combine(Path.GetDirectoryName(startPath)!, "SbieDll.dll"));
        try {
            var query = Marshal.GetDelegateForFunctionPointer<QueryProcessCall>(NativeLibrary.GetExport(library, "SbieApi_QueryProcessEx2"));
            var box = new char[40];
            var image = new char[1024];
            var status = query((nint)Environment.ProcessId, 1024, box, image, 0, out var sessionId, out var creationTime);
            if (status == unchecked((int)0xC000000B)) return null;
            if (status != 0) throw new InvalidOperationException($"Cannot verify sandbox probe membership: NTSTATUS 0x{unchecked((uint)status):X8}.");
            var boxEnd = Array.IndexOf(box, '\0');
            var imageEnd = Array.IndexOf(image, '\0');
            if (boxEnd <= 0 || imageEnd <= 0 || creationTime <= 0) throw new InvalidOperationException("Sandbox probe identity is incomplete.");
            return new(Environment.ProcessId, new string(box, 0, boxEnd), new string(image, 0, imageEnd), sessionId, creationTime);
        } finally { NativeLibrary.Free(library); }
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    private delegate int QueryProcessCall(nint pid, uint imageCapacity,
        [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2)] char[] box,
        [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2)] char[] image,
        nint sid, out uint session, out long creationTime);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool BoolCall();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitFlatCall([Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 1024)] byte[] errorMessage);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void VoidCall();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr UserCall();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool UserBoolCall(IntPtr user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong UserIdCall(IntPtr user);
}
