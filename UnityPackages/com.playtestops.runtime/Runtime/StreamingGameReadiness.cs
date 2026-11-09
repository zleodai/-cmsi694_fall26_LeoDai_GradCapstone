using System;
using UnityEngine;

namespace PlaytestOps.RuntimeStreaming
{
    /// <summary>A game-owned readiness barrier, separate from framebuffer/stream readiness.</summary>
    public static class StreamingGameReadiness
    {
        public static bool IsReady { get; private set; }
        public static string Role { get; private set; }
        public static string LobbyId { get; private set; }
        public static string SteamId { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void Reset()
        {
            IsReady = false;
            Role = null;
            LobbyId = null;
            SteamId = null;
        }

        /// <summary>Call after the host actually listens, or after the client has joined its lobby.</summary>
        public static void MarkReady(string role)
        {
            if (role != "host" && role != "client") throw new ArgumentException("Expected host or client.", nameof(role));
            if (IsReady && Role != role) throw new InvalidOperationException("The game role cannot change during a stream.");
            Role = role;
            IsReady = true;
        }

        /// <summary>Call after a Steam lobby and the local gameplay player are ready.</summary>
        public static void MarkSteamReady(string role, string lobbyId, string steamId)
        {
            if (!ulong.TryParse(lobbyId, out var lobby) || lobby == 0 || lobbyId.Length > 20 ||
                !ulong.TryParse(steamId, out var account) || account == 0 || steamId.Length > 20)
                throw new ArgumentException("Expected numeric Steam lobby and account identities.");
            if (IsReady && (LobbyId != lobbyId || SteamId != steamId))
                throw new InvalidOperationException("The Steam identity cannot change during a stream.");
            MarkReady(role);
            LobbyId = lobbyId;
            SteamId = steamId;
        }
    }
}
