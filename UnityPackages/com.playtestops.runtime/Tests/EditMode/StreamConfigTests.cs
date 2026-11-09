using NUnit.Framework;
using UnityEngine;

namespace PlaytestOps.RuntimeStreaming.Tests
{
    public sealed class StreamConfigTests
    {
        [TestCase("http://127.0.0.1:5282", "ws")]
        [TestCase("https://localhost:5282", "wss")]
        [TestCase("http://[::1]:5282", "ws")]
        public void ValidLoopbackConfigurationCreatesTokenFreeSocketUri(string origin, string scheme)
        {
            var config = ValidConfig();
            config.origin = origin;

            Assert.That(StreamConfig.TryRead(JsonUtility.ToJson(config), out var parsed), Is.True);
            Assert.That(parsed.SocketUri.Scheme, Is.EqualTo(scheme));
            Assert.That(parsed.SocketUri.AbsolutePath, Is.EqualTo("/api/streaming/runtime/test-session/test-instance"));
            Assert.That(parsed.SocketUri.Query, Is.Empty);
            Assert.That(parsed.SocketUri.UserInfo, Is.Empty);
            Assert.That(parsed.SocketUri.AbsoluteUri, Does.Not.Contain(config.token));
        }

        [TestCase("http://192.0.2.1:5282")]
        [TestCase("http://user:password@localhost:5282")]
        [TestCase("http://localhost:5282/unexpected-path")]
        [TestCase("http://localhost:5282/?token=secret")]
        [TestCase("http://localhost:5282/#fragment")]
        [TestCase("file:///localhost")]
        public void UnsafeOriginIsRejected(string origin)
        {
            var config = ValidConfig();
            config.origin = origin;
            Assert.That(StreamConfig.TryRead(JsonUtility.ToJson(config), out _), Is.False);
        }

        [TestCase(64, 64, 1, 100)]
        [TestCase(4096, 4096, 60, 50000)]
        public void InclusiveCaptureLimitsAreAccepted(int width, int height, int frameRate, int bitrate)
        {
            var config = ValidConfig();
            config.width = width;
            config.height = height;
            config.frameRate = frameRate;
            config.maxBitrateKbps = bitrate;
            Assert.That(StreamConfig.TryRead(JsonUtility.ToJson(config), out _), Is.True);
        }

        [TestCase("width", 63)]
        [TestCase("width", 4097)]
        [TestCase("height", 63)]
        [TestCase("height", 4097)]
        [TestCase("frameRate", 0)]
        [TestCase("frameRate", 61)]
        [TestCase("bitrate", 99)]
        [TestCase("bitrate", 50001)]
        public void OutOfRangeCaptureSettingsAreRejected(string setting, int value)
        {
            var config = ValidConfig();
            switch (setting)
            {
                case "width": config.width = value; break;
                case "height": config.height = value; break;
                case "frameRate": config.frameRate = value; break;
                case "bitrate": config.maxBitrateKbps = value; break;
            }
            Assert.That(StreamConfig.TryRead(JsonUtility.ToJson(config), out _), Is.False);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("too-short")]
        [TestCase("01234567890123456789012345678901\r\nInjected: value")]
        public void MissingShortOrHeaderInjectingTokenIsRejected(string token)
        {
            var config = ValidConfig();
            config.token = token;
            Assert.That(StreamConfig.TryRead(JsonUtility.ToJson(config), out _), Is.False);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("../other")]
        [TestCase("session?token=secret")]
        public void InvalidRoutingIdentifierIsRejected(string identifier)
        {
            var config = ValidConfig();
            config.sessionId = identifier;
            Assert.That(StreamConfig.TryRead(JsonUtility.ToJson(config), out _), Is.False);
            config = ValidConfig();
            config.instanceId = identifier;
            Assert.That(StreamConfig.TryRead(JsonUtility.ToJson(config), out _), Is.False);
        }

        [Test]
        public void OversizedConfigurationOrTokenIsRejected()
        {
            var validJson = JsonUtility.ToJson(ValidConfig());
            Assert.That(StreamConfig.TryRead(validJson + new string(' ', 8193 - validJson.Length), out _), Is.False);
            var config = ValidConfig();
            config.token = new string('t', 513);
            Assert.That(StreamConfig.TryRead(JsonUtility.ToJson(config), out _), Is.False);
        }

        private static StreamConfig ValidConfig() => new StreamConfig
        {
            sessionId = "test-session", instanceId = "test-instance", origin = "http://127.0.0.1:5282",
            token = new string('t', 32), width = 1280, height = 720, frameRate = 30, maxBitrateKbps = 6000
        };
    }

    public sealed class SteamReadinessTests
    {
        [SetUp, TearDown]
        public void ResetReadiness() => StreamingGameReadiness.Reset();

        [Test]
        public void SteamReadinessCarriesLobbyAndAccount()
        {
            StreamingGameReadiness.MarkSteamReady("host", "109775244908765432", "76561198000000001");
            Assert.That(StreamingGameReadiness.IsReady, Is.True);
            Assert.That(StreamingGameReadiness.Role, Is.EqualTo("host"));
            Assert.That(StreamingGameReadiness.LobbyId, Is.EqualTo("109775244908765432"));
            Assert.That(StreamingGameReadiness.SteamId, Is.EqualTo("76561198000000001"));
        }

        [TestCase("0", "76561198000000001")]
        [TestCase("109775244908765432", "")]
        [TestCase("invalid", "76561198000000001")]
        public void SteamReadinessRejectsMissingIdentity(string lobby, string account)
        {
            Assert.Throws<System.ArgumentException>(() => StreamingGameReadiness.MarkSteamReady("client", lobby, account));
            Assert.That(StreamingGameReadiness.IsReady, Is.False);
        }

        [Test]
        public void ReadySteamIdentityCannotBeReplaced()
        {
            StreamingGameReadiness.MarkSteamReady("host", "109775244908765432", "76561198000000001");
            Assert.Throws<System.InvalidOperationException>(() =>
                StreamingGameReadiness.MarkSteamReady("host", "109775244908765433", "76561198000000002"));
        }
    }

    public sealed class StreamingCursorTests
    {
        [TearDown]
        public void ClearOverride() => StreamingInput.SetMouseLockRequested(null);

        [TestCase(CursorLockMode.None, false)]
        [TestCase(CursorLockMode.Confined, false)]
        [TestCase(CursorLockMode.Locked, true)]
        public void NoOverrideFollowsNativeLockedState(CursorLockMode nativeState, bool expected)
        {
            StreamingInput.SetMouseLockRequested(null);
            Assert.That(StreamingInput.ResolveMouseLockRequested(nativeState), Is.EqualTo(expected));
        }

        [TestCase(CursorLockMode.None)]
        [TestCase(CursorLockMode.Confined)]
        [TestCase(CursorLockMode.Locked)]
        public void ExplicitFalseOverridesEveryNativeState(CursorLockMode nativeState)
        {
            StreamingInput.SetMouseLockRequested(false);
            Assert.That(StreamingInput.ResolveMouseLockRequested(nativeState), Is.False);
            Assert.That(StreamingInput.MouseLockRequested, Is.False);
        }

        [TestCase(CursorLockMode.None)]
        [TestCase(CursorLockMode.Confined)]
        [TestCase(CursorLockMode.Locked)]
        public void ExplicitTrueWorksWhileNativeWindowCursorIsFree(CursorLockMode nativeState)
        {
            StreamingInput.SetMouseLockRequested(true);
            Assert.That(StreamingInput.ResolveMouseLockRequested(nativeState), Is.True);
            Assert.That(StreamingInput.MouseLockRequested, Is.True);
        }

        [Test]
        public void SubsystemResetClearsPreviousGameOverride()
        {
            StreamingInput.SetMouseLockRequested(false);
            StreamingInput.ResetMouseLockRequested();
            Assert.That(StreamingInput.ResolveMouseLockRequested(CursorLockMode.Locked), Is.True);
            Assert.That(StreamingInput.ResolveMouseLockRequested(CursorLockMode.None), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CursorSignalCarriesBothLockAndUnlockIntent(bool requested)
        {
            var json = JsonUtility.ToJson(new SignalMessage
            {
                type = "runtime.cursor", sessionId = "session", instanceId = "instance",
                mouseLockRequested = requested
            });
            Assert.That(json, Does.Contain("\"mouseLockRequested\":" + (requested ? "true" : "false")));
            var parsed = JsonUtility.FromJson<SignalMessage>(json);
            Assert.That(parsed.type, Is.EqualTo("runtime.cursor"));
            Assert.That(parsed.sessionId, Is.EqualTo("session"));
            Assert.That(parsed.instanceId, Is.EqualTo("instance"));
            Assert.That(parsed.mouseLockRequested, Is.EqualTo(requested));
        }
    }
}
