"use client";

import React, { useEffect, useState, useCallback } from "react";

interface DeviceEndpointDto {
  endpointType: string;
  address: string;
  port: number;
}

interface DeviceSummaryDto {
  id: string;
  organizationId: string;
  organizationName: string;
  siteId: string;
  siteName: string;
  hostname: string;
  agentVersion: string;
  status: string;
  isOnline: boolean;
  lastSeenAt: string;
  endpoints: DeviceEndpointDto[];
}

interface OrganizationDto {
  id: string;
  name: string;
  status: string;
}

interface SiteDto {
  id: string;
  organizationId: string;
  name: string;
  status: string;
}

interface DashboardOverviewDto {
  totalDevices: number;
  onlineDevices: number;
  offlineDevices: number;
  revokedDevices: number;
  activeSessions: number;
  maxConcurrentSessions: number;
  organizations: OrganizationDto[];
  sites: SiteDto[];
}

interface ActiveSessionDto {
  sessionId: string;
  deviceId: string;
  deviceHostname: string;
  operatorUserId: string;
  slotIndex: number;
  transportType: string;
  status: string;
  startedAt: string;
  leaseExpiresAt: string;
}

interface AuditLogDto {
  id: string;
  organizationId: string;
  actorUserId: string | null;
  deviceId: string | null;
  sessionId: string | null;
  action: string;
  result: string;
  sourceIp: string;
  createdAt: string;
}

const DEFAULT_API_URL = process.env.NEXT_PUBLIC_API_URL || "http://localhost:5100";
const DEFAULT_OPERATOR_ID = "33333333-3333-3333-3333-333333333331";

export default function OverviewPage() {
  const [apiUrl, setApiUrl] = useState(DEFAULT_API_URL);
  const [activeTab, setActiveTab] = useState<"DEVICES" | "SESSIONS" | "AUDIT">("DEVICES");
  const [overview, setOverview] = useState<DashboardOverviewDto | null>(null);
  const [devices, setDevices] = useState<DeviceSummaryDto[]>([]);
  const [sessions, setSessions] = useState<ActiveSessionDto[]>([]);
  const [auditLogs, setAuditLogs] = useState<AuditLogDto[]>([]);
  const [statusFilter, setStatusFilter] = useState("ALL");
  const [searchQuery, setSearchQuery] = useState("");
  const [bannerMessage, setBannerMessage] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  const fetchDashboardData = useCallback(async () => {
    setLoading(true);
    try {
      const params = new URLSearchParams();
      if (statusFilter !== "ALL") params.set("status", statusFilter);
      if (searchQuery.trim()) params.set("search", searchQuery.trim());

      const [ovRes, devRes, sesRes, audRes] = await Promise.all([
        fetch(`${apiUrl}/api/overview`),
        fetch(`${apiUrl}/api/devices?${params.toString()}`),
        fetch(`${apiUrl}/api/sessions/active`),
        fetch(`${apiUrl}/api/audit-logs?limit=50`),
      ]);

      if (ovRes.ok) setOverview(await ovRes.json());
      if (devRes.ok) setDevices(await devRes.json());
      if (sesRes.ok) setSessions(await sesRes.json());
      if (audRes.ok) setAuditLogs(await audRes.json());
    } catch {
      // Backend API may not be running during static build or offline state
    } finally {
      setLoading(false);
    }
  }, [apiUrl, statusFilter, searchQuery]);

  useEffect(() => {
    fetchDashboardData();
    const timer = setInterval(fetchDashboardData, 5000);
    return () => clearInterval(timer);
  }, [fetchDashboardData]);

  async function handleRevokeOrDisable(deviceId: string, permanentRevoke: boolean) {
    const endpoint = permanentRevoke ? "revoke" : "disable";
    try {
      const res = await fetch(`${apiUrl}/api/devices/${deviceId}/${endpoint}`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          actorUserId: DEFAULT_OPERATOR_ID,
          reason: permanentRevoke ? "Revoked via Web Dashboard" : "Disabled via Web Dashboard",
        }),
      });
      if (res.ok) {
        setBannerMessage(`Device ${deviceId.slice(0, 8)} ${permanentRevoke ? "revoked" : "disabled"} successfully.`);
        await fetchDashboardData();
      }
    } catch (err) {
      setBannerMessage(`Failed to update device status: ${String(err)}`);
    }
  }

  async function handleStartSession(device: DeviceSummaryDto) {
    try {
      const res = await fetch(`${apiUrl}/api/sessions/request`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          organizationId: device.organizationId,
          deviceId: device.id,
          operatorUserId: DEFAULT_OPERATOR_ID,
          operatorDisplayName: "Pilot Operator Admin",
          allowInputControl: true,
          idempotencyKey: `${device.id}-${Date.now()}`,
        }),
      });
      const data = await res.json();
      if (res.ok) {
        setBannerMessage(
          `Session Reserved (Slot #${data.reservedSlotIndex + 1}/5) for ${device.hostname} -> Direct LAN ${data.connectionInfo.directAddress}:${data.connectionInfo.directPort}`
        );
        await fetchDashboardData();
      } else {
        setBannerMessage(`Session Denied: ${data.message || data.error}`);
      }
    } catch (err) {
      setBannerMessage(`Error requesting session: ${String(err)}`);
    }
  }

  async function handleEndSession(sessionId: string) {
    try {
      const res = await fetch(`${apiUrl}/api/sessions/${sessionId}/end`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          actorUserId: DEFAULT_OPERATOR_ID,
          reason: "Terminated by Operator via Dashboard",
        }),
      });
      if (res.ok) {
        setBannerMessage(`Session ${sessionId.slice(0, 8)} ended and slot released.`);
        await fetchDashboardData();
      }
    } catch (err) {
      setBannerMessage(`Failed to end session: ${String(err)}`);
    }
  }

  return (
    <main style={{ maxWidth: "1140px", margin: "0 auto" }}>
      <header
        style={{
          marginBottom: "24px",
          borderBottom: "1px solid #334155",
          paddingBottom: "16px",
          display: "flex",
          justifyContent: "space-between",
          alignItems: "center",
          flexWrap: "wrap",
          gap: "12px",
        }}
      >
        <div>
          <h1 style={{ margin: 0, fontSize: "24px" }}>RemoteDesk V1 — LAN Management Dashboard</h1>
          <p style={{ margin: "6px 0 0", color: "#94a3b8", fontSize: "14px" }}>
            Hybrid LAN (Direct + Relay) • Atomic 5-Session Coordinator • RBAC & Audit Trail
          </p>
        </div>

        <div style={{ display: "flex", gap: "8px", alignItems: "center" }}>
          <input
            value={apiUrl}
            onChange={(e) => setApiUrl(e.target.value)}
            style={{
              padding: "8px 12px",
              borderRadius: "6px",
              border: "1px solid #475569",
              backgroundColor: "#1e293b",
              color: "#f8fafc",
              width: "220px",
            }}
          />
          <button
            onClick={fetchDashboardData}
            style={{
              padding: "8px 14px",
              borderRadius: "6px",
              border: "none",
              backgroundColor: "#0284c7",
              color: "#fff",
              cursor: "pointer",
              fontWeight: 600,
            }}
          >
            {loading ? "Refreshing..." : "Refresh"}
          </button>
        </div>
      </header>

      {bannerMessage && (
        <div
          style={{
            padding: "12px 16px",
            backgroundColor: "#0c4a6e",
            border: "1px solid #0284c7",
            borderRadius: "8px",
            marginBottom: "20px",
            display: "flex",
            justifyContent: "space-between",
            alignItems: "center",
          }}
        >
          <span>{bannerMessage}</span>
          <button
            onClick={() => setBannerMessage(null)}
            style={{ background: "transparent", border: "none", color: "#bae6fd", cursor: "pointer" }}
          >
            Dismiss
          </button>
        </div>
      )}

      {/* Overview Cards */}
      <section
        style={{
          display: "grid",
          gridTemplateColumns: "repeat(4, 1fr)",
          gap: "16px",
          marginBottom: "24px",
        }}
      >
        <div style={{ padding: "16px", backgroundColor: "#1e293b", borderRadius: "8px" }}>
          <div style={{ color: "#94a3b8", fontSize: "13px" }}>Total Enrolled Devices</div>
          <div style={{ fontSize: "28px", fontWeight: 700, marginTop: "4px" }}>
            {overview?.totalDevices ?? devices.length}
          </div>
        </div>
        <div style={{ padding: "16px", backgroundColor: "#1e293b", borderRadius: "8px" }}>
          <div style={{ color: "#94a3b8", fontSize: "13px" }}>Online Devices</div>
          <div style={{ fontSize: "28px", fontWeight: 700, marginTop: "4px", color: "#22c55e" }}>
            {overview?.onlineDevices ?? devices.filter((d) => d.isOnline).length}
          </div>
        </div>
        <div style={{ padding: "16px", backgroundColor: "#1e293b", borderRadius: "8px" }}>
          <div style={{ color: "#94a3b8", fontSize: "13px" }}>Offline / Revoked</div>
          <div style={{ fontSize: "28px", fontWeight: 700, marginTop: "4px", color: "#f59e0b" }}>
            {(overview?.offlineDevices ?? 0) + (overview?.revokedDevices ?? 0)}
          </div>
        </div>
        <div style={{ padding: "16px", backgroundColor: "#1e293b", borderRadius: "8px" }}>
          <div style={{ color: "#94a3b8", fontSize: "13px" }}>Active Sessions (Atomic Limit)</div>
          <div style={{ fontSize: "28px", fontWeight: 700, marginTop: "4px", color: "#38bdf8" }}>
            {overview?.activeSessions ?? sessions.length} / {overview?.maxConcurrentSessions ?? 5}
          </div>
        </div>
      </section>

      {/* Navigation Tabs */}
      <div style={{ display: "flex", gap: "8px", marginBottom: "16px" }}>
        {(["DEVICES", "SESSIONS", "AUDIT"] as const).map((tab) => (
          <button
            key={tab}
            onClick={() => setActiveTab(tab)}
            style={{
              padding: "10px 18px",
              borderRadius: "8px",
              border: "none",
              backgroundColor: activeTab === tab ? "#0284c7" : "#1e293b",
              color: activeTab === tab ? "#fff" : "#94a3b8",
              fontWeight: 600,
              cursor: "pointer",
            }}
          >
            {tab === "DEVICES"
              ? `Devices (${devices.length})`
              : tab === "SESSIONS"
              ? `Active Sessions (${sessions.length}/5)`
              : `Audit Logs (${auditLogs.length})`}
          </button>
        ))}
      </div>

      {activeTab === "DEVICES" && (
        <>
          <section
            style={{
              backgroundColor: "#1e293b",
              padding: "16px",
              borderRadius: "8px",
              marginBottom: "16px",
              display: "flex",
              gap: "12px",
              alignItems: "center",
              flexWrap: "wrap",
            }}
          >
            <input
              placeholder="Search hostname (e.g., PC-A, PC-B)..."
              value={searchQuery}
              onChange={(e) => setSearchQuery(e.target.value)}
              style={{
                padding: "8px 12px",
                borderRadius: "6px",
                border: "1px solid #475569",
                backgroundColor: "#0f172a",
                color: "#f8fafc",
                flex: 1,
                minWidth: "240px",
              }}
            />
            <select
              value={statusFilter}
              onChange={(e) => setStatusFilter(e.target.value)}
              style={{
                padding: "8px 12px",
                borderRadius: "6px",
                border: "1px solid #475569",
                backgroundColor: "#0f172a",
                color: "#f8fafc",
              }}
            >
              <option value="ALL">All Statuses</option>
              <option value="Online">Online</option>
              <option value="Offline">Offline</option>
              <option value="Disabled">Disabled</option>
              <option value="Revoked">Revoked</option>
            </select>
          </section>

          <section style={{ backgroundColor: "#1e293b", borderRadius: "8px", overflow: "hidden" }}>
            <table style={{ width: "100%", borderCollapse: "collapse", textAlign: "left", fontSize: "14px" }}>
              <thead>
                <tr style={{ backgroundColor: "#0f172a", color: "#94a3b8", borderBottom: "1px solid #334155" }}>
                  <th style={{ padding: "12px 16px" }}>Hostname / Device UUID</th>
                  <th style={{ padding: "12px 16px" }}>Site / Branch</th>
                  <th style={{ padding: "12px 16px" }}>Status</th>
                  <th style={{ padding: "12px 16px" }}>Agent Version</th>
                  <th style={{ padding: "12px 16px" }}>LAN Endpoint</th>
                  <th style={{ padding: "12px 16px" }}>Actions</th>
                </tr>
              </thead>
              <tbody>
                {devices.length === 0 ? (
                  <tr>
                    <td colSpan={6} style={{ padding: "24px", textAlign: "center", color: "#64748b" }}>
                      No enrolled devices found. Start RemoteDesk.Agent.Service to enroll PC-A / PC-B.
                    </td>
                  </tr>
                ) : (
                  devices.map((device) => (
                    <tr key={device.id} style={{ borderBottom: "1px solid #334155" }}>
                      <td style={{ padding: "12px 16px" }}>
                        <div style={{ fontWeight: 600 }}>{device.hostname}</div>
                        <div style={{ fontSize: "12px", color: "#64748b", fontFamily: "monospace" }}>{device.id}</div>
                      </td>
                      <td style={{ padding: "12px 16px" }}>
                        <div>{device.siteName}</div>
                        <div style={{ fontSize: "12px", color: "#64748b" }}>{device.organizationName}</div>
                      </td>
                      <td style={{ padding: "12px 16px" }}>
                        <span
                          style={{
                            padding: "4px 10px",
                            borderRadius: "999px",
                            fontSize: "12px",
                            fontWeight: 600,
                            backgroundColor:
                              device.status === "Online"
                                ? "#14532d"
                                : device.status === "Revoked"
                                ? "#7f1d1d"
                                : "#334155",
                            color:
                              device.status === "Online"
                                ? "#4ade80"
                                : device.status === "Revoked"
                                ? "#fca5a5"
                                : "#cbd5e1",
                          }}
                        >
                          {device.status}
                        </span>
                      </td>
                      <td style={{ padding: "12px 16px" }}>{device.agentVersion}</td>
                      <td style={{ padding: "12px 16px", fontFamily: "monospace", fontSize: "13px" }}>
                        {device.endpoints.length > 0
                          ? `${device.endpoints[0].address}:${device.endpoints[0].port}`
                          : "N/A"}
                      </td>
                      <td style={{ padding: "12px 16px", display: "flex", gap: "8px" }}>
                        <button
                          disabled={!device.isOnline}
                          onClick={() => handleStartSession(device)}
                          style={{
                            padding: "6px 10px",
                            borderRadius: "6px",
                            border: "none",
                            backgroundColor: device.isOnline ? "#0284c7" : "#334155",
                            color: "#fff",
                            cursor: device.isOnline ? "pointer" : "not-allowed",
                            fontSize: "12px",
                          }}
                        >
                          Connect LAN
                        </button>
                        {(device.status === "Revoked" || device.status === "Disabled") && (
                          <button
                            onClick={async () => {
                              await fetch(`${apiUrl}/api/devices/${device.id}/enable`, { method: "POST" });
                              setBannerMessage(`Device ${device.hostname} re-enabled (Online).`);
                              await fetchDashboardData();
                            }}
                            style={{
                              padding: "6px 10px",
                              borderRadius: "6px",
                              border: "1px solid #22c55e",
                              backgroundColor: "transparent",
                              color: "#4ade80",
                              cursor: "pointer",
                              fontSize: "12px",
                            }}
                          >
                            Enable
                          </button>
                        )}
                        <button
                          disabled={device.status === "Disabled" || device.status === "Revoked"}
                          onClick={() => handleRevokeOrDisable(device.id, false)}
                          style={{
                            padding: "6px 10px",
                            borderRadius: "6px",
                            border: "1px solid #f59e0b",
                            backgroundColor: "transparent",
                            color: "#fbbf24",
                            cursor: "pointer",
                            fontSize: "12px",
                          }}
                        >
                          Disable
                        </button>
                        <button
                          disabled={device.status === "Revoked"}
                          onClick={() => handleRevokeOrDisable(device.id, true)}
                          style={{
                            padding: "6px 10px",
                            borderRadius: "6px",
                            border: "1px solid #ef4444",
                            backgroundColor: "transparent",
                            color: "#f87171",
                            cursor: "pointer",
                            fontSize: "12px",
                          }}
                        >
                          Revoke
                        </button>
                      </td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </section>
        </>
      )}

      {activeTab === "SESSIONS" && (
        <section style={{ backgroundColor: "#1e293b", borderRadius: "8px", overflow: "hidden" }}>
          <table style={{ width: "100%", borderCollapse: "collapse", textAlign: "left", fontSize: "14px" }}>
            <thead>
              <tr style={{ backgroundColor: "#0f172a", color: "#94a3b8", borderBottom: "1px solid #334155" }}>
                <th style={{ padding: "12px 16px" }}>Slot</th>
                <th style={{ padding: "12px 16px" }}>Device</th>
                <th style={{ padding: "12px 16px" }}>Transport</th>
                <th style={{ padding: "12px 16px" }}>Started At</th>
                <th style={{ padding: "12px 16px" }}>Lease Expires</th>
                <th style={{ padding: "12px 16px" }}>Actions</th>
              </tr>
            </thead>
            <tbody>
              {sessions.length === 0 ? (
                <tr>
                  <td colSpan={6} style={{ padding: "24px", textAlign: "center", color: "#64748b" }}>
                    No active remote sessions. All 5 concurrent slots are available.
                  </td>
                </tr>
              ) : (
                sessions.map((s) => (
                  <tr key={s.sessionId} style={{ borderBottom: "1px solid #334155" }}>
                    <td style={{ padding: "12px 16px", fontWeight: 700, color: "#38bdf8" }}>
                      Slot #{s.slotIndex + 1} / 5
                    </td>
                    <td style={{ padding: "12px 16px" }}>
                      <div style={{ fontWeight: 600 }}>{s.deviceHostname}</div>
                      <div style={{ fontSize: "12px", color: "#64748b", fontFamily: "monospace" }}>{s.sessionId}</div>
                    </td>
                    <td style={{ padding: "12px 16px" }}>{s.transportType}</td>
                    <td style={{ padding: "12px 16px" }}>{new Date(s.startedAt).toLocaleTimeString()}</td>
                    <td style={{ padding: "12px 16px" }}>{new Date(s.leaseExpiresAt).toLocaleTimeString()}</td>
                    <td style={{ padding: "12px 16px" }}>
                      <button
                        onClick={() => handleEndSession(s.sessionId)}
                        style={{
                          padding: "6px 10px",
                          borderRadius: "6px",
                          border: "1px solid #ef4444",
                          backgroundColor: "transparent",
                          color: "#f87171",
                          cursor: "pointer",
                          fontSize: "12px",
                        }}
                      >
                        End Session
                      </button>
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </section>
      )}

      {activeTab === "AUDIT" && (
        <section style={{ backgroundColor: "#1e293b", borderRadius: "8px", overflow: "hidden" }}>
          <table style={{ width: "100%", borderCollapse: "collapse", textAlign: "left", fontSize: "14px" }}>
            <thead>
              <tr style={{ backgroundColor: "#0f172a", color: "#94a3b8", borderBottom: "1px solid #334155" }}>
                <th style={{ padding: "12px 16px" }}>Timestamp</th>
                <th style={{ padding: "12px 16px" }}>Action</th>
                <th style={{ padding: "12px 16px" }}>Result</th>
                <th style={{ padding: "12px 16px" }}>Source IP</th>
                <th style={{ padding: "12px 16px" }}>Device / Session ID</th>
              </tr>
            </thead>
            <tbody>
              {auditLogs.length === 0 ? (
                <tr>
                  <td colSpan={5} style={{ padding: "24px", textAlign: "center", color: "#64748b" }}>
                    No audit log entries recorded yet.
                  </td>
                </tr>
              ) : (
                auditLogs.map((log) => (
                  <tr key={log.id} style={{ borderBottom: "1px solid #334155" }}>
                    <td style={{ padding: "12px 16px", fontSize: "13px", color: "#94a3b8" }}>
                      {new Date(log.createdAt).toLocaleString()}
                    </td>
                    <td style={{ padding: "12px 16px", fontWeight: 600, color: "#e2e8f0" }}>{log.action}</td>
                    <td style={{ padding: "12px 16px" }}>{log.result}</td>
                    <td style={{ padding: "12px 16px", fontFamily: "monospace", fontSize: "13px" }}>{log.sourceIp}</td>
                    <td style={{ padding: "12px 16px", fontFamily: "monospace", fontSize: "12px", color: "#64748b" }}>
                      {log.deviceId ? `Dev: ${log.deviceId.slice(0, 8)}` : ""}
                      {log.sessionId ? ` Ses: ${log.sessionId.slice(0, 8)}` : ""}
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </section>
      )}
    </main>
  );
}
