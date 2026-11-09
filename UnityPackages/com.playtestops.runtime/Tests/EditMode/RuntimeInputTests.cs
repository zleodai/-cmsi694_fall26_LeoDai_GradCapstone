using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.Utilities;

namespace PlaytestOps.RuntimeStreaming.Tests
{
    public sealed class RuntimeInputTests
    {
        private const string LeaseId = "test-lease";
        private const string LeaseToken = "test-lease-secret";
        private const string PlayerUpdatesInEditMode = "RUN_PLAYER_UPDATES_IN_EDIT_MODE";
        private readonly List<InputReply> replies = new List<InputReply>();
        private InputSettings previousSettings;
        private InputSettings.UpdateMode previousUpdateMode;
        private bool previousPlayerUpdatesInEditMode;
        private bool settingsCaptured;
        private RuntimeInput input;
        private InputAction action;
        private Action<InputReply> observeReply;
        private Action beforeUpdate;
        private StreamConfig config;

        [SetUp]
        public void SetUp()
        {
            settingsCaptured = false;
            previousSettings = InputSystem.settings;
            previousUpdateMode = previousSettings.updateMode;
            // Manual mode alone still selects Editor updates outside Play Mode. Use the
            // Input System's test override so the runtime sees actual Manual player updates.
            // The setter is public; reading the prior nonserialized flag requires reflection.
            var featureGetter = typeof(InputSettings).GetMethod("IsFeatureEnabled",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(featureGetter, Is.Not.Null, "Input System feature flag getter is unavailable.");
            previousPlayerUpdatesInEditMode = (bool)featureGetter.Invoke(previousSettings,
                new object[] { PlayerUpdatesInEditMode });
            settingsCaptured = true;
            previousSettings.SetInternalFeatureFlag(PlayerUpdatesInEditMode, true);
            previousSettings.updateMode = InputSettings.UpdateMode.ProcessEventsManually;
            replies.Clear();
            observeReply = null;
            config = new StreamConfig { sessionId = "test-session", instanceId = "test-instance" };
            input = new RuntimeInput(config, reply =>
            {
                replies.Add(reply);
                observeReply?.Invoke(reply);
            });
            Assert.That(input.Grant(new SignalMessage
            {
                leaseId = LeaseId,
                leaseToken = LeaseToken,
                expiresUtc = DateTimeOffset.UtcNow.AddSeconds(5).ToString("O"),
                timeoutMs = 1500
            }), Is.True);
            Send(Packet(1, "state"));
            var beforePhase = InputUpdateType.None;
            var afterPhase = InputUpdateType.None;
            Action captureBeforePhase = () => beforePhase = InputState.currentUpdateType;
            Action captureAfterPhase = () => afterPhase = InputState.currentUpdateType;
            InputSystem.onBeforeUpdate += captureBeforePhase;
            InputSystem.onAfterUpdate += captureAfterPhase;
            try
            {
                InputSystem.Update();
            }
            finally
            {
                InputSystem.onBeforeUpdate -= captureBeforePhase;
                InputSystem.onAfterUpdate -= captureAfterPhase;
            }
            Assert.That(replies.Where(reply => reply.type == "input.ack").Select(reply => reply.seq).ToArray(),
                Is.EqualTo(new long[] { 1 }),
                "The setup snapshot must be applied in a Manual player update. "
                + "currentUpdateType=" + InputState.currentUpdateType + "; before=" + beforePhase
                + "; after=" + afterPhase + "; hasControl=" + StreamingInput.HasControl
                + "; keyboardEnabled=" + StreamingInput.Keyboard.enabled
                + "; mouseEnabled=" + StreamingInput.Mouse.enabled
                + "; replies=[" + string.Join(", ", replies.Select(reply => JsonUtility.ToJson(reply))) + "]");
            replies.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            if (beforeUpdate != null) InputSystem.onBeforeUpdate -= beforeUpdate;
            beforeUpdate = null;
            observeReply = null;
            try
            {
                action?.Dispose();
                action = null;
                input?.Dispose();
                input = null;
            }
            finally
            {
                if (settingsCaptured)
                {
                    try
                    {
                        previousSettings.SetInternalFeatureFlag(PlayerUpdatesInEditMode,
                            previousPlayerUpdatesInEditMode);
                    }
                    finally
                    {
                        previousSettings.updateMode = previousUpdateMode;
                        settingsCaptured = false;
                    }
                }
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void GrantReportsReadinessOnlyAfterAValidLeaseIsInstalled(bool valid)
        {
            var accepted = input.Grant(new SignalMessage
            {
                leaseId = LeaseId,
                leaseToken = LeaseToken,
                expiresUtc = DateTimeOffset.UtcNow.AddSeconds(valid ? 5 : -1).ToString("O"),
                timeoutMs = 1500
            });

            Assert.That(accepted, Is.EqualTo(valid));
            Assert.That(StreamingInput.HasControl, Is.EqualTo(valid));
            Send(Packet(1, "state"));
            InputSystem.Update();
            Assert.That(replies.Any(reply => reply.type == "input.ack"), Is.EqualTo(valid));
        }

        [Test]
        public void KeyAcknowledgmentFollowsDeviceAndActionProcessing()
        {
            var performed = ObserveWAction();
            var keyDownAtAcknowledgment = false;
            var actionCountAtAcknowledgment = -1;
            observeReply = reply =>
            {
                if (reply.type != "input.ack" || reply.seq != 2) return;
                keyDownAtAcknowledgment = StreamingInput.Keyboard.wKey.isPressed;
                actionCountAtAcknowledgment = performed();
            };
            var key = Packet(2, "key");
            key.code = "KeyW";
            key.down = true;
            key.clientSentMs = 123.5;
            key.diagnosticId = 42;
            Send(key);

            Assert.That(replies, Is.Empty, "Receiving a packet must not acknowledge an unapplied input.");
            Assert.That(StreamingInput.Keyboard.wKey.isPressed, Is.False);
            Assert.That(input.LastDiagnosticId, Is.Zero);

            InputSystem.Update();

            var ack = Acknowledgment(2);
            Assert.That(ack.leaseId, Is.EqualTo(LeaseId));
            Assert.That(keyDownAtAcknowledgment, Is.True);
            Assert.That(actionCountAtAcknowledgment, Is.EqualTo(1));
            Assert.That(ack.clientSentMs, Is.EqualTo(123.5));
            Assert.That(ack.diagnosticId, Is.EqualTo(42));
            Assert.That(ack.runtimeAppliedMs, Is.GreaterThanOrEqualTo(ack.runtimeReceivedMs));
            Assert.That(input.LastAppliedSequence, Is.EqualTo(2));
            Assert.That(input.LastDiagnosticId, Is.EqualTo(42));
        }

        [Test]
        public void DuplicatePacketsDoNotRetriggerActionsOrAdvanceDiagnostics()
        {
            var performed = ObserveWAction();
            var key = Packet(2, "key");
            key.code = "KeyW";
            key.down = true;
            key.diagnosticId = 17;
            Send(key);
            Send(key);
            InputSystem.Update();
            var original = Acknowledgment(2);
            Assert.That(performed(), Is.EqualTo(1));
            replies.Clear();

            // A retransmission cannot replace the already applied packet's metadata or key state.
            key.down = false;
            key.diagnosticId = 99;
            Send(key);
            InputSystem.Update();

            var duplicate = Acknowledgment(2);
            Assert.That(duplicate.duplicate, Is.True);
            Assert.That(original.leaseId, Is.EqualTo(LeaseId));
            Assert.That(duplicate.leaseId, Is.EqualTo(original.leaseId));
            Assert.That(duplicate.runtimeReceivedMs, Is.EqualTo(original.runtimeReceivedMs));
            Assert.That(duplicate.runtimeAppliedMs, Is.EqualTo(original.runtimeAppliedMs));
            Assert.That(duplicate.diagnosticId, Is.EqualTo(17));
            Assert.That(performed(), Is.EqualTo(1));
            Assert.That(StreamingInput.Keyboard.wKey.isPressed, Is.True);
            Assert.That(input.LastDiagnosticId, Is.EqualTo(17));
        }

        [TestCase(914514.1977637607d)]
        [TestCase(87393.299999952316d)]
        [TestCase(0.10000000000000002d)]
        public void FractionalTimestampWireEchoUsesBoundedPrecisionAndRetainsLeaseIdentity(double sentAt)
        {
            var json = JsonUtility.ToJson(Packet(2, "ping"));
            const string field = "\"clientSentMs\":";
            int start = json.IndexOf(field, StringComparison.Ordinal) + field.Length;
            int end = json.IndexOf(',', start);
            // Use the browser's original decimal text, bypassing a preceding Unity
            // serialization roundtrip. The live failing timestamp differs by one ULP
            // after JsonUtility parses it, despite representing the same input packet.
            json = json.Substring(0, start) + sentAt.ToString("R", CultureInfo.InvariantCulture) + json.Substring(end);
            input.Receive(Encoding.UTF8.GetBytes(json));
            InputSystem.Update();

            var wireReply = JsonUtility.FromJson<InputReply>(JsonUtility.ToJson(Acknowledgment(2)));
            Assert.That(wireReply.leaseId, Is.EqualTo(LeaseId));
            Assert.That(wireReply.seq, Is.EqualTo(2));
            Assert.That(wireReply.clientSentMs, Is.EqualTo(sentAt).Within(0.000001d),
                "Fractional JsonUtility echoes require browser roundoff tolerance or integral timestamps, not strict bitwise equality.");
        }

        [TestCase(914514d)]
        [TestCase(1234567d)]
        public void IntegralMillisecondTimestampRoundTripsExactly(double sentAt)
        {
            var packet = Packet(2, "ping");
            packet.clientSentMs = sentAt;
            Send(packet);
            InputSystem.Update();
            var wireReply = JsonUtility.FromJson<InputReply>(JsonUtility.ToJson(Acknowledgment(2)));
            Assert.That(wireReply.leaseId, Is.EqualTo(LeaseId));
            Assert.That(wireReply.clientSentMs, Is.EqualTo(sentAt));
        }

        [Test]
        public void PriorLeasePacketErrorRetainsItsOriginWithoutAffectingANewerLease()
        {
            const string newerId = "newer-lease";
            const string newerToken = "newer-lease-secret";
            Assert.That(input.Grant(new SignalMessage
            {
                leaseId = newerId, leaseToken = newerToken, timeoutMs = 1500,
                expiresUtc = DateTimeOffset.UtcNow.AddSeconds(5).ToString("O")
            }), Is.True);
            var current = Packet(1, "state");
            current.leaseId = newerId;
            current.leaseToken = newerToken;
            current.keys = new[] { "KeyA" };
            current.buttons = 1;
            Send(current);
            InputSystem.Update();
            Assert.That(Acknowledgment(1).leaseId, Is.EqualTo(newerId));
            replies.Clear();

            Send(Packet(3, "reset"));
            InputSystem.Update();

            var rejected = replies.Single(reply => reply.type == "input.error");
            Assert.That(rejected.reason, Is.EqualTo("unauthorized_input"));
            Assert.That(rejected.leaseId, Is.EqualTo(LeaseId),
                "A delayed prior-controller error must not be stamped with the newer live controller's ID.");
            Assert.That(StreamingInput.HasControl, Is.True);
            Assert.That(StreamingInput.Keyboard.aKey.isPressed, Is.True);
            Assert.That(StreamingInput.Mouse.leftButton.isPressed, Is.True);
            Assert.That(input.LastAppliedSequence, Is.EqualTo(1));
        }

        [Test]
        public void PacketArrivingAfterRevokeIsRejectedWithItsOldLeaseIdentity()
        {
            input.Revoke();
            Send(Packet(2, "reset"));
            InputSystem.Update();
            var rejected = replies.Single(reply => reply.type == "input.error");
            Assert.That(rejected.reason, Is.EqualTo("unauthorized_input"));
            Assert.That(rejected.leaseId, Is.EqualTo(LeaseId));
            Assert.That(StreamingInput.HasControl, Is.False);
            AssertReleased();
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        [TestCase("invalid\nlease")]
        public void UnauthorizedErrorDoesNotInventAnAssociationForAnInvalidPacketLease(string invalidId)
        {
            var packet = Packet(2, "reset");
            packet.leaseId = invalidId;
            Send(packet);
            InputSystem.Update();
            Assert.That(replies.Single(reply => reply.type == "input.error").leaseId, Is.Null);
            Assert.That(StreamingInput.HasControl, Is.True);
        }

        [Test]
        public void UnauthorizedErrorDoesNotEchoAnOversizedLeaseIdentifier()
        {
            var packet = Packet(2, "reset");
            packet.leaseId = new string('x', 129);
            Send(packet);
            InputSystem.Update();
            Assert.That(replies.Single(reply => reply.type == "input.error").leaseId, Is.Null);
        }

        [TestCase("session")]
        [TestCase("instance")]
        [TestCase("lease")]
        [TestCase("token")]
        public void WrongIdentityOrLeaseCannotChangeHeldInput(string invalidField)
        {
            HoldWAndButtons();
            var unauthorized = Packet(3, "reset");
            switch (invalidField)
            {
                case "session": unauthorized.sessionId = "other-session"; break;
                case "instance": unauthorized.instanceId = "other-instance"; break;
                case "lease": unauthorized.leaseId = "other-lease"; break;
                case "token": unauthorized.leaseToken = "wrong-lease-secret"; break;
            }
            Send(unauthorized);
            InputSystem.Update();

            AssertError("unauthorized_input", 3);
            Assert.That(replies.Any(reply => reply.type == "input.ack"), Is.False);
            Assert.That(StreamingInput.Keyboard.wKey.isPressed, Is.True);
            Assert.That(StreamingInput.Mouse.leftButton.isPressed, Is.True);
            Assert.That(StreamingInput.HasControl, Is.True);

            Send(Packet(3, "reset"));
            InputSystem.Update();
            Assert.That(Acknowledgment(3).duplicate, Is.False);
            AssertReleased();
        }

        [Test]
        public void SequenceGapNeutralizesAlreadyQueuedPressAndRequiresRecoverySnapshot()
        {
            var key = Packet(2, "key");
            key.code = "KeyW";
            key.down = true;
            Send(key);
            var skipped = Packet(4, "pointer");
            skipped.buttons = 1;
            Send(skipped);
            InputSystem.Update();

            AssertError("sequence_gap", 3);
            Assert.That(replies.Any(reply => reply.type == "input.ack"), Is.False,
                "The interrupted update must not acknowledge queued input discarded during recovery.");
            AssertReleased();
            replies.Clear();

            key.seq = 3;
            Send(key);
            InputSystem.Update();
            AssertError("state_required", 3);
            AssertReleased();
            replies.Clear();

            var snapshot = Packet(5, "state");
            snapshot.keys = new[] { "KeyA" };
            snapshot.buttons = 2;
            Send(snapshot);
            InputSystem.Update();

            Assert.That(Acknowledgment(5).duplicate, Is.False);
            Assert.That(StreamingInput.Keyboard.aKey.isPressed, Is.True);
            Assert.That(StreamingInput.Keyboard.wKey.isPressed, Is.False);
            Assert.That(StreamingInput.Mouse.rightButton.isPressed, Is.True);
            Assert.That(StreamingInput.Mouse.leftButton.isPressed, Is.False);
        }

        [Test]
        public void RevokeReleasesDevicesAndDiscardsPendingPackets()
        {
            HoldWAndButtons();
            var queued = Packet(3, "state");
            queued.keys = new[] { "KeyA" };
            queued.buttons = 31;
            Send(queued);

            input.Revoke();
            Assert.That(StreamingInput.HasControl, Is.False);
            AssertReleased();
            InputSystem.Update();

            AssertReleased();
            Assert.That(StreamingInput.Keyboard.aKey.isPressed, Is.False);
            Assert.That(replies.Any(reply => reply.type == "input.ack"), Is.False);
            Assert.That(input.LastAppliedSequence, Is.Zero);
        }

        [Test]
        public void ReleaseAcknowledgmentWaitsForAnInputUpdateAndDiscardsQueuedPackets()
        {
            var performed = ObserveWAction();
            HoldWAndButtons();
            Assert.That(performed(), Is.EqualTo(1));
            Assert.That(action.IsPressed(), Is.True);
            var queued = Packet(3, "state");
            queued.keys = new[] { "KeyA" };
            queued.buttons = 31;
            Send(queued);
            var released = new List<string>();
            var callbackPhase = InputUpdateType.None;
            var keyboardEventTime = -1d;
            var mouseEventTime = -1d;
            var bothEventsConsumedAtCallback = false;
            input.RevokeAndAcknowledge(LeaseId, id =>
            {
                AssertReleased();
                callbackPhase = InputState.currentUpdateType;
                bothEventsConsumedAtCallback = keyboardEventTime >= 0 && mouseEventTime >= 0
                    && StreamingInput.Keyboard.lastUpdateTime + 1e-9 >= keyboardEventTime
                    && StreamingInput.Mouse.lastUpdateTime + 1e-9 >= mouseEventTime;
                released.Add(id);
            });

            Assert.That(StreamingInput.HasControl, Is.False);
            AssertReleased();
            Assert.That(action.IsPressed(), Is.False, "Revocation immediately cancels the held InputAction.");
            Assert.That(released, Is.Empty, "ResetDevice alone cannot acknowledge queued neutral events.");
            Action<InputEventPtr, InputDevice> observeNeutralEvents = (eventPtr, device) =>
            {
                if (!eventPtr.IsA<StateEvent>()) return;
                if (device == StreamingInput.Keyboard) keyboardEventTime = eventPtr.time;
                if (device == StreamingInput.Mouse) mouseEventTime = eventPtr.time;
            };
            InputSystem.onEvent += observeNeutralEvents;
            try { InputSystem.Update(); }
            finally { InputSystem.onEvent -= observeNeutralEvents; }

            Assert.That(released, Is.EqualTo(new[] { LeaseId }));
            Assert.That(callbackPhase, Is.EqualTo(InputUpdateType.Manual));
            Assert.That(bothEventsConsumedAtCallback, Is.True,
                "Both queued neutral state events must have reached their devices before signaling release.");
            AssertReleased();
            Assert.That(StreamingInput.Keyboard.aKey.isPressed, Is.False);
            Assert.That(action.IsPressed(), Is.False);
            Assert.That(replies.Any(reply => reply.type == "input.ack"), Is.False);
            InputSystem.Update();
            Assert.That(released, Has.Count.EqualTo(1), "The release callback runs once.");
        }

        [Test]
        public void ReleaseAcknowledgmentFollowsNeutralTailEventsAfterAQueuedPress()
        {
            var pressed = Packet(2, "state");
            pressed.keys = new[] { "KeyW" };
            pressed.buttons = 31;
            Send(pressed);
            var released = new List<string>();
            var revokeRequested = false;
            var insideBeforeUpdate = false;
            beforeUpdate = () =>
            {
                if (revokeRequested) return;
                revokeRequested = true;
                insideBeforeUpdate = true;
                // RuntimeInput has already queued the press in its earlier handler.
                input.RevokeAndAcknowledge(LeaseId, id =>
                {
                    if (insideBeforeUpdate) return;
                    AssertReleased();
                    released.Add(id);
                });
                Assert.That(released, Is.Empty);
                insideBeforeUpdate = false;
            };
            InputSystem.onBeforeUpdate += beforeUpdate;

            InputSystem.Update();

            Assert.That(revokeRequested, Is.True);
            Assert.That(released, Is.EqualTo(new[] { LeaseId }));
            AssertReleased();
            Assert.That(StreamingInput.HasControl, Is.False);
            Assert.That(replies.Any(reply => reply.type == "input.ack"), Is.False,
                "Discarded press events cannot receive normal sequence acknowledgments.");
        }

        [Test]
        public void DelayedOldReleaseCannotRevokeOrAcknowledgeANewerLiveLease()
        {
            Assert.That(input.Grant(new SignalMessage
            {
                leaseId = "new-lease", leaseToken = "new-lease-secret", timeoutMs = 1500,
                expiresUtc = DateTimeOffset.UtcNow.AddSeconds(5).ToString("O")
            }), Is.True);
            var state = Packet(1, "state");
            state.leaseId = "new-lease";
            state.leaseToken = "new-lease-secret";
            state.keys = new[] { "KeyA" };
            state.buttons = 1;
            Send(state);
            InputSystem.Update();
            var released = new List<string>();

            input.RevokeAndAcknowledge(LeaseId, released.Add);
            InputSystem.Update();

            Assert.That(released, Is.Empty);
            Assert.That(StreamingInput.HasControl, Is.True);
            Assert.That(StreamingInput.Keyboard.aKey.isPressed, Is.True);
            Assert.That(StreamingInput.Mouse.leftButton.isPressed, Is.True);
            Assert.That(input.LastAppliedSequence, Is.EqualTo(1));
        }

        [Test]
        public void RepeatedReleaseRequestsCoalesceAndCompletedRepeatUsesANewBarrier()
        {
            var released = new List<string>();
            input.RevokeAndAcknowledge(LeaseId, released.Add);
            input.RevokeAndAcknowledge(LeaseId, released.Add);
            Assert.That(released, Is.Empty);
            InputSystem.Update();
            InputSystem.Update();
            Assert.That(released, Is.EqualTo(new[] { LeaseId }));

            input.RevokeAndAcknowledge(LeaseId, released.Add);
            Assert.That(released, Has.Count.EqualTo(1));
            InputSystem.Update();
            Assert.That(released, Is.EqualTo(new[] { LeaseId, LeaseId }));
            AssertReleased();
        }

        [TestCase(1)]
        [TestCase(3)]
        public void OrdinaryDisconnectRevokesPreserveThePendingReleaseBarrier(int cleanupRevokes)
        {
            HoldWAndButtons();
            var released = new List<string>();
            var latestKeyboardEvent = -1d;
            var latestMouseEvent = -1d;
            var consumedAtCallback = false;
            input.RevokeAndAcknowledge(LeaseId, id =>
            {
                AssertReleased();
                consumedAtCallback = latestKeyboardEvent >= 0 && latestMouseEvent >= 0
                    && StreamingInput.Keyboard.lastUpdateTime + 1e-9 >= latestKeyboardEvent
                    && StreamingInput.Mouse.lastUpdateTime + 1e-9 >= latestMouseEvent;
                released.Add(id);
            });
            // Model viewer.closed / OnChannelClose / PeerDisconnected following
            // control.revoked before the next player Input System update.
            for (var count = 0; count < cleanupRevokes; count++) input.Revoke();
            Assert.That(released, Is.Empty);
            Assert.That(StreamingInput.HasControl, Is.False);
            AssertReleased();

            Action<InputEventPtr, InputDevice> observeTailEvents = (eventPtr, device) =>
            {
                if (!eventPtr.IsA<StateEvent>()) return;
                if (device == StreamingInput.Keyboard) latestKeyboardEvent = eventPtr.time;
                if (device == StreamingInput.Mouse) latestMouseEvent = eventPtr.time;
            };
            InputSystem.onEvent += observeTailEvents;
            try { InputSystem.Update(); }
            finally { InputSystem.onEvent -= observeTailEvents; }

            Assert.That(released, Is.EqualTo(new[] { LeaseId }),
                "Uncorrelated disconnect cleanup must not strand the correlated release barrier.");
            Assert.That(consumedAtCallback, Is.True);
            input.Revoke();
            InputSystem.Update();
            Assert.That(released, Has.Count.EqualTo(1), "Later ordinary cleanup cannot emit another acknowledgment.");
            AssertReleased();
        }

        [Test]
        public void PreviouslyRevokedKnownLeaseStillRequiresAnAppliedNeutralBarrier()
        {
            HoldWAndButtons();
            input.Revoke();
            var released = new List<string>();
            input.RevokeAndAcknowledge(LeaseId, released.Add);
            Assert.That(released, Is.Empty);
            InputSystem.Update();
            Assert.That(released, Is.EqualTo(new[] { LeaseId }));
            AssertReleased();
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        [TestCase("bad\nlease")]
        public void InvalidReleaseIdentifierFailsClosedWithoutAcknowledging(string invalidId)
        {
            HoldWAndButtons();
            var released = new List<string>();
            input.RevokeAndAcknowledge(invalidId, released.Add);
            Assert.That(StreamingInput.HasControl, Is.False);
            AssertReleased();
            InputSystem.Update();
            Assert.That(released, Is.Empty);
        }

        [Test]
        public void UnknownReleaseIdentifierCannotBeAcknowledgedAfterRevoke()
        {
            input.Revoke();
            var released = new List<string>();
            input.RevokeAndAcknowledge("never-granted", released.Add);
            InputSystem.Update();
            Assert.That(released, Is.Empty);
            Assert.That(StreamingInput.HasControl, Is.False);
        }

        [Test]
        public void NewGrantCancelsAPendingOldReleaseAcknowledgment()
        {
            var released = new List<string>();
            input.RevokeAndAcknowledge(LeaseId, released.Add);
            Assert.That(input.Grant(new SignalMessage
            {
                leaseId = "new-lease", leaseToken = "new-lease-secret", timeoutMs = 1500,
                expiresUtc = DateTimeOffset.UtcNow.AddSeconds(5).ToString("O")
            }), Is.True);
            InputSystem.Update();
            Assert.That(released, Is.Empty);
            Assert.That(StreamingInput.HasControl, Is.True);
        }

        [Test]
        public void DisposalCancelsAPendingReleaseWithoutFalseAcknowledgment()
        {
            HoldWAndButtons();
            var released = new List<string>();
            input.RevokeAndAcknowledge(LeaseId, released.Add);
            input.Dispose();
            InputSystem.Update();
            input.RevokeAndAcknowledge(LeaseId, released.Add);
            Assert.That(released, Is.Empty);
            Assert.That(StreamingInput.Keyboard, Is.Null);
            Assert.That(StreamingInput.Mouse, Is.Null);
            Assert.That(StreamingInput.HasControl, Is.False);
        }

        [Test]
        public void InputSilenceWatchdogReleasesHeldInputAndDiscardsQueuedPacketsDespiteLeaseRenewal()
        {
            Assert.That(input.Grant(new SignalMessage
            {
                leaseId = LeaseId, leaseToken = LeaseToken, timeoutMs = 250,
                expiresUtc = DateTimeOffset.UtcNow.AddSeconds(5).ToString("O")
            }), Is.True);
            HoldWAndButtons(1);
            var queued = Packet(2, "state");
            queued.keys = new[] { "KeyA" };
            queued.buttons = 31;
            Send(queued);

            Thread.Sleep(350);
            // Signaling remains healthy, but renewal cannot substitute for processed input.
            input.Renew(new SignalMessage
            {
                leaseId = LeaseId,
                expiresUtc = DateTimeOffset.UtcNow.AddSeconds(5).ToString("O")
            });
            input.Tick();

            AssertError("input_timeout", 2);
            Assert.That(StreamingInput.HasControl, Is.True, "The live lease remains available for snapshot recovery.");
            AssertReleased();
            InputSystem.Update();
            InputSystem.Update();

            AssertReleased();
            Assert.That(StreamingInput.Keyboard.aKey.isPressed, Is.False);
            Assert.That(input.LastAppliedSequence, Is.EqualTo(1));
            Assert.That(replies, Has.Count.EqualTo(1), "Cleared packets must produce neither acknowledgments nor stale-input errors.");
            Assert.That(replies.Any(reply => reply.type == "input.ack"), Is.False);
        }

        [Test]
        public void LeaseExpiryRevokesControlReleasesHeldInputAndDiscardsQueuedPackets()
        {
            HoldWAndButtons();
            // Start the short lease deadline only after the held input has been applied.
            // The existing 1500 ms watchdog keeps this case specific to lease expiry.
            input.Renew(new SignalMessage
            {
                leaseId = LeaseId,
                expiresUtc = DateTimeOffset.UtcNow.AddMilliseconds(250).ToString("O")
            });
            var queued = Packet(3, "state");
            queued.keys = new[] { "KeyA" };
            queued.buttons = 31;
            Send(queued);

            Thread.Sleep(350);
            input.Tick();

            AssertError("lease_expired", 0);
            Assert.That(replies.Single(reply => reply.type == "input.error").leaseId, Is.EqualTo(LeaseId),
                "Revoking the lease must not erase the identity of its expiry error.");
            Assert.That(StreamingInput.HasControl, Is.False);
            AssertReleased();
            InputSystem.Update();
            InputSystem.Update();

            AssertReleased();
            Assert.That(StreamingInput.Keyboard.aKey.isPressed, Is.False);
            Assert.That(input.LastAppliedSequence, Is.Zero);
            Assert.That(replies, Has.Count.EqualTo(1), "Packets queued before lease expiry must be discarded.");
            Assert.That(replies.Any(reply => reply.type == "input.ack"), Is.False);
        }

        [Test]
        public void RevokeAfterEventsAreQueuedCannotLeaveAKeyHeldOrAcknowledgeIt()
        {
            var key = Packet(2, "key");
            key.code = "KeyW";
            key.down = true;
            Send(key);
            // RuntimeInput's earlier onBeforeUpdate handler queues the press first.
            beforeUpdate = () => input.Revoke();
            InputSystem.onBeforeUpdate += beforeUpdate;

            InputSystem.Update();

            AssertReleased();
            Assert.That(replies.Any(reply => reply.type == "input.ack"), Is.False);
            Assert.That(StreamingInput.HasControl, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DisabledInputDeviceRevokesControlWithoutAcknowledgment(bool disableAfterQueueing)
        {
            var key = Packet(2, "key");
            key.code = "KeyW";
            key.down = true;
            Send(key);
            if (disableAfterQueueing)
            {
                beforeUpdate = () => InputSystem.DisableDevice(StreamingInput.Mouse);
                InputSystem.onBeforeUpdate += beforeUpdate;
            }
            else InputSystem.DisableDevice(StreamingInput.Keyboard);

            InputSystem.Update();

            Assert.That(replies.Any(reply => reply.type == "input.ack"), Is.False);
            AssertError("input_devices_disabled", 0);
            Assert.That(StreamingInput.HasControl, Is.False);
            AssertReleased();
        }

        [Test]
        public void VirtualDevicesAdvertiseBackgroundSupportAndRemoteUsage()
        {
            foreach (var device in new InputDevice[] { StreamingInput.Keyboard, StreamingInput.Mouse })
            {
                Assert.That(device.native, Is.False, "Remote input must use virtual devices.");
                // Editor focus policy overrides the device property; verify the runtime layout capability.
                Assert.That(InputSystem.LoadLayout(device.layout).canRunInBackground, Is.True);
                Assert.That(device.usages, Does.Contain(new InternedString("PlaytestOpsRemote")));
            }
        }

        [Test]
        public void QueueOverflowFailsClosedAndReleasesHeldInput()
        {
            HoldWAndButtons();
            for (var sequence = 3; sequence <= 259; sequence++) Send(Packet(sequence, "ping"));

            InputSystem.Update();

            AssertError("queue_overflow", 0);
            Assert.That(replies.Single(reply => reply.type == "input.error").leaseId, Is.EqualTo(LeaseId),
                "A local safety revoke must retain the lease ID for its error reply.");
            Assert.That(replies.Any(reply => reply.type == "input.ack"), Is.False);
            Assert.That(StreamingInput.HasControl, Is.False);
            AssertReleased();
        }

        [Test]
        public void InputErrorsDoNotEchoCredentialsOrUntrustedPacketText()
        {
            const string secret = "do-not-echo-this-secret";
            var packet = Packet(2, "key");
            packet.leaseToken = secret;
            packet.code = secret;
            Send(packet);
            InputSystem.Update();

            AssertError("unauthorized_input", 2);
            foreach (var reply in replies)
            {
                var json = JsonUtility.ToJson(reply);
                Assert.That(json, Does.Not.Contain(secret));
                Assert.That(json, Does.Not.Contain(LeaseToken));
            }
        }

        private Func<int> ObserveWAction()
        {
            var performed = 0;
            action = new InputAction("remote-w", InputActionType.Button, "<Keyboard>{PlaytestOpsRemote}/w");
            action.performed += _ => performed++;
            action.Enable();
            return () => performed;
        }

        private void HoldWAndButtons(long sequence = 2)
        {
            var state = Packet(sequence, "state");
            state.keys = new[] { "KeyW" };
            state.buttons = 31;
            Send(state);
            InputSystem.Update();
            Assert.That(Acknowledgment(sequence).duplicate, Is.False);
            Assert.That(StreamingInput.Keyboard.wKey.isPressed, Is.True);
            Assert.That(StreamingInput.Mouse.leftButton.isPressed, Is.True);
            replies.Clear();
        }

        private static void AssertReleased()
        {
            Assert.That(StreamingInput.Keyboard.anyKey.isPressed, Is.False);
            Assert.That(StreamingInput.Mouse.leftButton.isPressed, Is.False);
            Assert.That(StreamingInput.Mouse.rightButton.isPressed, Is.False);
            Assert.That(StreamingInput.Mouse.middleButton.isPressed, Is.False);
            Assert.That(StreamingInput.Mouse.backButton.isPressed, Is.False);
            Assert.That(StreamingInput.Mouse.forwardButton.isPressed, Is.False);
        }

        private void AssertError(string reason, long expectedSequence)
        {
            Assert.That(replies.Any(reply => reply.type == "input.error" && reply.reason == reason
                && reply.expectedSeq == expectedSequence), Is.True, "Expected input.error: " + reason);
        }

        private InputReply Acknowledgment(long sequence) => replies.Single(reply => reply.type == "input.ack" && reply.seq == sequence);

        private InputMessage Packet(long sequence, string kind) => new InputMessage
        {
            type = "input", sessionId = config.sessionId, instanceId = config.instanceId,
            leaseId = LeaseId, leaseToken = LeaseToken, seq = sequence, kind = kind
        };

        private void Send(InputMessage message) => input.Receive(Encoding.UTF8.GetBytes(JsonUtility.ToJson(message)));
    }
}
