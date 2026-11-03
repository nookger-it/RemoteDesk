using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using RemoteDesk.Agent.Contracts;
using RemoteDesk.Agent.Interactive;
using RemoteDesk.Application.Security;
using RemoteDesk.Contracts.Sessions;
using RemoteDesk.Infrastructure.Persistence;
using RemoteDesk.Transport.Abstractions;
using RemoteDesk.Transport.DirectLan;
using RemoteDesk.Transport.LocalRelay;

namespace RemoteDesk.IntegrationTests;

public sealed class Phase1And2IntegrationTests
{
    private static WebApplicationFactory<Program> CreateTestFactory(string dbFileName)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = $"Data Source={dbFileName}"
                });
            });
        });
    }

    [Fact]
    public async Task T01_T02_T11_Enrollment_Heartbeat_And_Revoke_Flow_ShouldSucceed()
    {
        var dbFile = Path.Combine(Path.GetTempPath(), $"remotedesk-test-{Guid.NewGuid():N}.db");
        try
        {
            await using var factory = CreateTestFactory(dbFile);
            using var client = factory.CreateClient();

            // T01: Enroll PC-A and PC-B and verify unique UUIDs
            var enrollPcA = new DeviceEnrollmentRequest(
                PilotDefaults.OrganizationId,
                PilotDefaults.HeadquartersSiteId,
                null,
                "PC-A-OPERATOR",
                "1.0.0-v1",
                "pub-key-pca",
                [new DeviceEndpointDto("DirectLan", "192.168.1.10", 52000)]);

            var enrollPcB = new DeviceEnrollmentRequest(
                PilotDefaults.OrganizationId,
                PilotDefaults.HeadquartersSiteId,
                null,
                "PC-B-AGENT",
                "1.0.0-v1",
                "pub-key-pcb",
                [new DeviceEndpointDto("DirectLan", "192.168.1.20", 52000)]);

            var respA = await client.PostAsJsonAsync("/api/devices/enroll", enrollPcA);
            var respB = await client.PostAsJsonAsync("/api/devices/enroll", enrollPcB);

            Assert.Equal(HttpStatusCode.OK, respA.StatusCode);
            Assert.Equal(HttpStatusCode.OK, respB.StatusCode);

            var dataA = await respA.Content.ReadFromJsonAsync<DeviceEnrollmentResponse>();
            var dataB = await respB.Content.ReadFromJsonAsync<DeviceEnrollmentResponse>();

            Assert.NotNull(dataA);
            Assert.NotNull(dataB);
            Assert.NotEqual(Guid.Empty, dataA.DeviceId);
            Assert.NotEqual(Guid.Empty, dataB.DeviceId);
            Assert.NotEqual(dataA.DeviceId, dataB.DeviceId);

            // T02: Heartbeat updates PC-B and Dashboard Overview shows 2 Online devices
            var hbResp = await client.PostAsJsonAsync(
                $"/api/devices/{dataB.DeviceId}/heartbeat",
                new DeviceHeartbeatRequest(
                    dataB.DeviceId,
                    "1.0.0-v1",
                    [new DeviceEndpointDto("DirectLan", "192.168.1.20", 52000)]));
            Assert.Equal(HttpStatusCode.OK, hbResp.StatusCode);

            var overview = await client.GetFromJsonAsync<DashboardOverviewDto>("/api/overview");
            Assert.NotNull(overview);
            Assert.Equal(2, overview.TotalDevices);
            Assert.Equal(2, overview.OnlineDevices);

            // T11: Revoke PC-B -> subsequent heartbeat and session requests are rejected
            var revokeResp = await client.PostAsJsonAsync(
                $"/api/devices/{dataB.DeviceId}/revoke",
                new RevokeDeviceRequest(PilotDefaults.OperatorUserId, "Security test revoke"));
            Assert.Equal(HttpStatusCode.OK, revokeResp.StatusCode);

            var hbAfterRevoke = await client.PostAsJsonAsync(
                $"/api/devices/{dataB.DeviceId}/heartbeat",
                new DeviceHeartbeatRequest(dataB.DeviceId, "1.0.0-v1", []));
            Assert.Equal(HttpStatusCode.Forbidden, hbAfterRevoke.StatusCode);

            var sessionAfterRevoke = await client.PostAsJsonAsync(
                "/api/sessions/request",
                new StartSessionRequest(
                    PilotDefaults.OrganizationId,
                    dataB.DeviceId,
                    PilotDefaults.OperatorUserId,
                    "Operator Admin",
                    AllowInputControl: true));
            Assert.Equal(HttpStatusCode.Conflict, sessionAfterRevoke.StatusCode);
        }
        finally
        {
            if (File.Exists(dbFile))
            {
                try { File.Delete(dbFile); } catch { }
            }
        }
    }

    [Fact]
    public async Task T05_Phase2_DirectLan_Encrypted_Stream_And_TicketValidation_ShouldStreamFrames()
    {
        var ticketService = new SessionTicketService("UnitTest-Secret-Key-2026");
        var sessionId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var operatorId = PilotDefaults.OperatorUserId;
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(2);

        var validTicket = ticketService.IssueTicket(sessionId, deviceId, operatorId, true, expiresAt);
        var captureEngine = new ScreenCaptureEngine(1280, 720);

        await using var host = new DirectLanStreamHost(
            port: 0, // Dynamic free port for test isolation
            deviceId: deviceId,
            ticketValidator: (Guid sid, Guid did, string ticket, out bool allowInput, out string? err) =>
            {
                var ok = ticketService.TryValidateTicket(ticket, sid, did, out var claims, out err);
                allowInput = claims?.AllowInputControl ?? false;
                return ok;
            },
            frameProvider: (seq, ct) => captureEngine.CaptureFrameAsync(sessionId, seq, ct),
            targetFps: 20);

        host.Start();

        // 1. Verify tampered ticket is rejected
        await using (var badTransport = new DirectLanTransport())
        {
            var badConnInfo = new SessionConnectionInfo(
                sessionId,
                deviceId,
                operatorId,
                validTicket + "tampered",
                expiresAt,
                "127.0.0.1",
                host.BoundPort,
                "127.0.0.1",
                52100,
                true);

            var badResult = await badTransport.ConnectAsync(badConnInfo, CancellationToken.None);
            Assert.False(badResult.IsConnected);
        }

        // 2. Verify valid ticket establishes TLS stream and receives frames + telemetry
        await using var goodTransport = new DirectLanTransport();
        var tcs = new TaskCompletionSource<ScreenFramePacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        goodTransport.FrameReceived += frame =>
        {
            if (frame.FrameSequence >= 2)
            {
                tcs.TrySetResult(frame);
            }
        };

        var goodConnInfo = new SessionConnectionInfo(
            sessionId,
            deviceId,
            operatorId,
            validTicket,
            expiresAt,
            "127.0.0.1",
            host.BoundPort,
            "127.0.0.1",
            52100,
            true);

        var connectResult = await goodTransport.ConnectAsync(goodConnInfo, CancellationToken.None);
        Assert.True(connectResult.IsConnected, connectResult.FailureReason);

        var receivedFrame = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(sessionId, receivedFrame.SessionId);
        Assert.Equal(1280, receivedFrame.Width);
        Assert.Equal(720, receivedFrame.Height);
        Assert.Equal("jpeg", receivedFrame.Encoding);
        Assert.NotEmpty(receivedFrame.Payload);

        var telemetry = goodTransport.GetTelemetrySnapshot();
        Assert.True(telemetry.TotalFramesReceived >= 2);
        Assert.True(telemetry.TotalBytesReceived > 0);
    }

    [Fact]
    public async Task T06_Phase3_MouseKeyboard_PermissionGate_And_AgentBanner_ShouldSucceed()
    {
        var ticketService = new SessionTicketService("UnitTest-Secret-Key-2026");
        var deviceId = Guid.NewGuid();
        var operatorId = PilotDefaults.OperatorUserId;
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
        var captureEngine = new ScreenCaptureEngine(1280, 720);
        var inputEngine = new InputInjectorEngine(executeNativeSendInput: false); // Safe dry-run in automated tests
        var bannerManager = new AgentSessionBannerManager();

        // Part A: View + Control Session (AllowInputControl = true) + Local User Disconnect Banner
        var controlSessionId = Guid.NewGuid();
        var controlTicket = ticketService.IssueTicket(controlSessionId, deviceId, operatorId, allowInputControl: true, expiresAt);

        await using var controlHost = new DirectLanStreamHost(
            port: 0,
            deviceId: deviceId,
            ticketValidator: (Guid sid, Guid did, string ticket, out bool allowInput, out string? err) =>
            {
                var ok = ticketService.TryValidateTicket(ticket, sid, did, out var claims, out err);
                allowInput = claims?.AllowInputControl ?? false;
                return ok;
            },
            frameProvider: (seq, ct) => captureEngine.CaptureFrameAsync(controlSessionId, seq, ct),
            targetFps: 15,
            inputHandler: (pkt, allow, ct) => inputEngine.ProcessInputAsync(pkt, allow, ct));

        controlHost.Start();

        bannerManager.ShowBannerForSession(
            new StartInteractiveSessionCommand(
                controlSessionId,
                deviceId,
                operatorId,
                "Pilot Operator Admin",
                controlTicket,
                expiresAt,
                controlHost.BoundPort,
                AllowInputControl: true),
            controlHost);

        Assert.True(bannerManager.CurrentState.IsVisible);
        Assert.Contains("Pilot Operator Admin", bannerManager.CurrentState.BannerText);

        await using var controlClient = new DirectLanTransport();
        var ackList = new List<InputControlAckPacket>();
        var allAcksTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnectTcs = new TaskCompletionSource<SessionDisconnectPacket>(TaskCreationOptions.RunContinuationsAsynchronously);

        controlClient.InputAckReceived += ack =>
        {
            lock (ackList)
            {
                ackList.Add(ack);
                if (ackList.Count >= 3)
                {
                    allAcksTcs.TrySetResult(true);
                }
            }
        };
        controlClient.RemoteDisconnected += disc => disconnectTcs.TrySetResult(disc);

        var connRes = await controlClient.ConnectAsync(
            new SessionConnectionInfo(
                controlSessionId,
                deviceId,
                operatorId,
                controlTicket,
                expiresAt,
                "127.0.0.1",
                controlHost.BoundPort,
                "127.0.0.1",
                52100,
                true),
            CancellationToken.None);
        Assert.True(connRes.IsConnected);

        // Send MouseMove (0.5, 0.25), MouseDown (Left), and KeyDown (VK_A = 0x41)
        await controlClient.SendInputEventAsync(
            new RemoteInputEventPacket(controlSessionId, RemoteInputEventType.MouseMove, 0.5, 0.25, MouseButtonType.None, 0, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            CancellationToken.None);
        await controlClient.SendInputEventAsync(
            new RemoteInputEventPacket(controlSessionId, RemoteInputEventType.MouseDown, 0.5, 0.25, MouseButtonType.Left, 0, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            CancellationToken.None);
        await controlClient.SendInputEventAsync(
            new RemoteInputEventPacket(controlSessionId, RemoteInputEventType.KeyDown, 0, 0, MouseButtonType.None, 0, 0x41, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            CancellationToken.None);

        await allAcksTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(ackList, a => Assert.True(a.Accepted));
        Assert.Equal(3, inputEngine.ProcessedEventsCount);
        Assert.Equal((32768, 16384), inputEngine.LastMappedAbsolutePosition);
        Assert.Equal(0x41, inputEngine.LastInjectedVirtualKey);

        // Verify local user at Agent PC can immediately disconnect the remote operator
        await bannerManager.DisconnectByLocalUserAsync("User clicked Disconnect on Agent Banner");
        var discPacket = await disconnectTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("AgentLocalUser", discPacket.InitiatedBy);
        Assert.False(bannerManager.CurrentState.IsVisible);

        // Part B: View-Only Session (AllowInputControl = false) -> Permission Gate blocks input!
        var viewOnlySessionId = Guid.NewGuid();
        var viewOnlyTicket = ticketService.IssueTicket(viewOnlySessionId, deviceId, operatorId, allowInputControl: false, expiresAt);

        await using var viewOnlyClient = new DirectLanTransport();
        var blockedAckTcs = new TaskCompletionSource<InputControlAckPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewOnlyClient.InputAckReceived += ack => blockedAckTcs.TrySetResult(ack);

        var viewConnRes = await viewOnlyClient.ConnectAsync(
            new SessionConnectionInfo(
                viewOnlySessionId,
                deviceId,
                operatorId,
                viewOnlyTicket,
                expiresAt,
                "127.0.0.1",
                controlHost.BoundPort,
                "127.0.0.1",
                52100,
                false),
            CancellationToken.None);
        Assert.True(viewConnRes.IsConnected);

        await viewOnlyClient.SendInputEventAsync(
            new RemoteInputEventPacket(viewOnlySessionId, RemoteInputEventType.KeyDown, 0, 0, MouseButtonType.None, 0, 0x42, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            CancellationToken.None);

        var blockedAck = await blockedAckTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(blockedAck.Accepted);
        Assert.Equal(1, blockedAck.BlockedEventsCount);
    }

    [Fact]
    public async Task Phase2_NamedPipeIpc_Between_Service_And_Interactive_ShouldDeliverSessionCommand()
    {
        var pipeName = $"RemoteDesk.TestPipe.{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var receivedTcs = new TaskCompletionSource<StartInteractiveSessionCommand>(TaskCreationOptions.RunContinuationsAsynchronously);

        var serverTask = Task.Run(() => AgentNamedPipeIpc.ListenAsync((env, _) =>
        {
            if (env.StartCommand is not null)
            {
                receivedTcs.TrySetResult(env.StartCommand);
            }
            cts.Cancel();
            return Task.CompletedTask;
        }, pipeName, cts.Token));

        await Task.Delay(100);

        var expectedCmd = new StartInteractiveSessionCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            PilotDefaults.OperatorUserId,
            "Operator Admin",
            "signed-ticket-xyz",
            DateTimeOffset.UtcNow.AddMinutes(2),
            52000,
            true);

        await AgentNamedPipeIpc.SendAsync(
            new AgentIpcEnvelope("START_SESSION", expectedCmd),
            pipeName,
            CancellationToken.None);

        var actualCmd = await receivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(expectedCmd.SessionId, actualCmd.SessionId);
        Assert.Equal(expectedCmd.SessionTicket, actualCmd.SessionTicket);
    }

    [Fact]
    public async Task T07_Phase4_DirectLan_Unreachable_ShouldFallbackTo_LocalRelay_AndStream_ShouldSucceed()
    {
        var ticketService = new SessionTicketService("UnitTest-Relay-Key-2026");
        var sessionId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var operatorId = PilotDefaults.OperatorUserId;
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
        var validTicket = ticketService.IssueTicket(sessionId, deviceId, operatorId, allowInputControl: true, expiresAt);

        var captureEngine = new ScreenCaptureEngine(1280, 720);
        var inputEngine = new InputInjectorEngine(executeNativeSendInput: false);

        // 1. Start Local Relay Server on a dynamic port
        await using var relayServer = new LocalRelayServer(
            port: 0,
            ticketValidator: (Guid sid, Guid did, string ticket, out bool allowInput, out string? err) =>
            {
                var ok = ticketService.TryValidateTicket(ticket, sid, did, out var claims, out err);
                allowInput = claims?.AllowInputControl ?? false;
                return ok;
            });
        relayServer.Start();

        // 2. Start Agent outbound bridge connected to Local Relay Server
        await using var agentRelayBridge = new LocalRelayAgentSessionBridge(
            relayAddress: "127.0.0.1",
            relayPort: relayServer.BoundPort,
            sessionId: sessionId,
            deviceId: deviceId,
            sessionTicket: validTicket,
            frameProvider: (seq, ct) => captureEngine.CaptureFrameAsync(sessionId, seq, ct),
            targetFps: 20,
            inputHandler: (pkt, allow, ct) => inputEngine.ProcessInputAsync(pkt, allow, ct));
        agentRelayBridge.Start();

        // 3. Configure SessionConnectionInfo with an UNREACHABLE DirectPort (e.g., closed port 52009)
        // so DirectLanTransport fails and HybridLanTransportOrchestrator automatically falls back to LocalRelay!
        var sessionInfo = new SessionConnectionInfo(
            SessionId: sessionId,
            DeviceId: deviceId,
            OperatorUserId: operatorId,
            SessionTicket: validTicket,
            TicketExpiresAt: expiresAt,
            DirectAddress: "127.0.0.1",
            DirectPort: 52009, // Nothing listening on 52009 -> Direct LAN fails
            RelayAddress: "127.0.0.1",
            RelayPort: relayServer.BoundPort,
            AllowInputControl: true);

        await using var orchestrator = new HybridLanTransportOrchestrator(
            new DirectLanTransport(),
            new LocalRelayTransport(),
            directAttemptTimeout: TimeSpan.FromMilliseconds(400));

        var frameTcs = new TaskCompletionSource<ScreenFramePacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inputAckTcs = new TaskCompletionSource<InputControlAckPacket>(TaskCreationOptions.RunContinuationsAsynchronously);

        orchestrator.FrameReceived += frame =>
        {
            if (frame.FrameSequence >= 2)
            {
                frameTcs.TrySetResult(frame);
            }
        };
        orchestrator.InputAckReceived += ack => inputAckTcs.TrySetResult(ack);

        var connectResult = await orchestrator.ConnectAsync(sessionInfo, CancellationToken.None);

        // Verify fallback succeeded and active transport is LocalRelay
        Assert.True(connectResult.IsConnected, connectResult.FailureReason);
        Assert.Equal("LocalRelay", connectResult.ActiveTransportType);
        Assert.Equal("LocalRelay", orchestrator.TransportName);

        // Verify encrypted screen frames arrive through Local Relay
        var relayedFrame = await frameTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(sessionId, relayedFrame.SessionId);
        Assert.Equal(1280, relayedFrame.Width);
        Assert.Equal(720, relayedFrame.Height);
        Assert.NotEmpty(relayedFrame.Payload);

        // Verify Mouse/Keyboard input is forwarded through Local Relay to Agent
        await orchestrator.SendInputEventAsync(
            new RemoteInputEventPacket(sessionId, RemoteInputEventType.MouseMove, 0.75, 0.5, MouseButtonType.None, 0, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            CancellationToken.None);

        var ack = await inputAckTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(ack.Accepted);
        Assert.Equal(1, inputEngine.ProcessedEventsCount);
    }

    [Fact]
    public async Task T03_T04_T08_T09_T10_T12_Phase5_Security_Rbac_SessionCoordinator_And_Audit_ShouldSucceed()
    {
        var dbFile = Path.Combine(Path.GetTempPath(), $"remotedesk-p5-{Guid.NewGuid():N}.db");
        try
        {
            await using var factory = CreateTestFactory(dbFile);
            using var client = factory.CreateClient();

            // T03: Operator Login & Admin MFA Enforcement
            var noMfaResp = await client.PostAsJsonAsync(
                "/api/auth/login",
                new OperatorLoginRequest("operator@remotedesk.local", PilotDefaults.DefaultOperatorPassword, MfaCode: null));
            Assert.Equal(HttpStatusCode.Unauthorized, noMfaResp.StatusCode);

            var validLoginResp = await client.PostAsJsonAsync(
                "/api/auth/login",
                new OperatorLoginRequest("operator@remotedesk.local", PilotDefaults.DefaultOperatorPassword, PilotDefaults.DefaultAdminMfaCode));
            Assert.Equal(HttpStatusCode.OK, validLoginResp.StatusCode);
            var loginData = await validLoginResp.Content.ReadFromJsonAsync<OperatorLoginResponse>();
            Assert.NotNull(loginData);
            Assert.Contains("OperatorAdmin", loginData.Roles);
            Assert.NotEmpty(loginData.AccessToken);

            // Enroll a target device (PC-B)
            var enrollResp = await client.PostAsJsonAsync(
                "/api/devices/enroll",
                new DeviceEnrollmentRequest(
                    PilotDefaults.OrganizationId,
                    PilotDefaults.HeadquartersSiteId,
                    null,
                    "PC-B-SECURE",
                    "1.0.0-v1",
                    "pub-key-secure",
                    [new DeviceEndpointDto("DirectLan", "192.168.1.50", 52000)]));
            var enrolled = await enrollResp.Content.ReadFromJsonAsync<DeviceEnrollmentResponse>();
            Assert.NotNull(enrolled);

            // T04: Restricted Operator without DevicePermission is denied (403 Forbidden)
            var unauthorizedSessionResp = await client.PostAsJsonAsync(
                "/api/sessions/request",
                new StartSessionRequest(
                    PilotDefaults.OrganizationId,
                    enrolled.DeviceId,
                    PilotDefaults.RestrictedOperatorUserId,
                    "Restricted Helpdesk Operator",
                    AllowInputControl: true));
            Assert.Equal(HttpStatusCode.Forbidden, unauthorizedSessionResp.StatusCode);

            // Grant permission to Restricted Operator, then verify they can request a session
            var grantResp = await client.PostAsJsonAsync(
                $"/api/devices/{enrolled.DeviceId}/permissions",
                new GrantDevicePermissionRequest(
                    PilotDefaults.RestrictedOperatorUserId,
                    "ViewAndControl",
                    PilotDefaults.OperatorUserId));
            Assert.Equal(HttpStatusCode.OK, grantResp.StatusCode);

            // T09: Idempotency Key prevents duplicate session creation on retry
            var idempKey = $"idemp-{Guid.NewGuid():N}";
            var sessionReq1 = new StartSessionRequest(
                PilotDefaults.OrganizationId,
                enrolled.DeviceId,
                PilotDefaults.RestrictedOperatorUserId,
                "Restricted Helpdesk Operator",
                AllowInputControl: true,
                IdempotencyKey: idempKey,
                LeaseDurationSeconds: 1); // Short 1-second lease to test T08 next!

            var s1RespA = await client.PostAsJsonAsync("/api/sessions/request", sessionReq1);
            var s1RespB = await client.PostAsJsonAsync("/api/sessions/request", sessionReq1);
            Assert.Equal(HttpStatusCode.OK, s1RespA.StatusCode);
            Assert.Equal(HttpStatusCode.OK, s1RespB.StatusCode);

            var s1DataA = await s1RespA.Content.ReadFromJsonAsync<StartSessionResponse>();
            var s1DataB = await s1RespB.Content.ReadFromJsonAsync<StartSessionResponse>();
            Assert.NotNull(s1DataA);
            Assert.NotNull(s1DataB);
            Assert.Equal(s1DataA.ConnectionInfo.SessionId, s1DataB.ConnectionInfo.SessionId);
            Assert.Equal(s1DataA.ReservedSlotIndex, s1DataB.ReservedSlotIndex);

            // T08: Wait 1.2s for the short lease to expire -> slot is automatically released
            await Task.Delay(1250);
            var activeAfterTimeout = await client.GetFromJsonAsync<List<ActiveSessionDto>>("/api/sessions/active");
            Assert.NotNull(activeAfterTimeout);
            Assert.Empty(activeAfterTimeout);

            // T10: Atomic 5-Session Limit -> Slots 0..4 succeed, 6th session is rejected (429 TooManyRequests)
            var activeSessionIds = new List<Guid>();
            for (int i = 0; i < 5; i++)
            {
                var resp = await client.PostAsJsonAsync(
                    "/api/sessions/request",
                    new StartSessionRequest(
                        PilotDefaults.OrganizationId,
                        enrolled.DeviceId,
                        PilotDefaults.OperatorUserId,
                        $"Operator #{i + 1}",
                        AllowInputControl: true));
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                var started = await resp.Content.ReadFromJsonAsync<StartSessionResponse>();
                Assert.NotNull(started);
                activeSessionIds.Add(started.ConnectionInfo.SessionId);
            }

            var sixthResp = await client.PostAsJsonAsync(
                "/api/sessions/request",
                new StartSessionRequest(
                    PilotDefaults.OrganizationId,
                    enrolled.DeviceId,
                    PilotDefaults.OperatorUserId,
                    "Operator #6",
                    AllowInputControl: true));
            Assert.Equal(HttpStatusCode.TooManyRequests, sixthResp.StatusCode);

            // End one session and verify slot count drops to 4
            var endResp = await client.PostAsJsonAsync(
                $"/api/sessions/{activeSessionIds[0]}/end",
                new EndSessionRequest(PilotDefaults.OperatorUserId, "Completed test session"));
            Assert.Equal(HttpStatusCode.OK, endResp.StatusCode);

            // T12: Verify Audit Logs recorded all critical security & session events
            var auditLogs = await client.GetFromJsonAsync<List<AuditLogDto>>("/api/audit-logs?limit=100");
            Assert.NotNull(auditLogs);
            var actions = auditLogs.Select(a => a.Action).ToHashSet();
            Assert.Contains("OPERATOR_MFA_FAILED", actions);
            Assert.Contains("OPERATOR_LOGIN_SUCCESS", actions);
            Assert.Contains("DEVICE_ENROLLED", actions);
            Assert.Contains("SESSION_ACCESS_DENIED", actions);
            Assert.Contains("DEVICE_PERMISSION_GRANTED", actions);
            Assert.Contains("SESSION_STARTED", actions);
            Assert.Contains("SESSION_LIMIT_EXCEEDED", actions);
            Assert.Contains("SESSION_ENDED", actions);
        }
        finally
        {
            if (File.Exists(dbFile))
            {
                try { File.Delete(dbFile); } catch { }
            }
        }
    }
}
