using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteDesk.Application.Security;

public sealed record SessionTicketClaims(
    Guid SessionId,
    Guid DeviceId,
    Guid OperatorUserId,
    bool AllowInputControl,
    long ExpiresAtUnixSeconds);

public interface ISessionTicketService
{
    string IssueTicket(
        Guid sessionId,
        Guid deviceId,
        Guid operatorUserId,
        bool allowInputControl,
        DateTimeOffset expiresAt);

    bool TryValidateTicket(
        string ticket,
        Guid expectedSessionId,
        Guid expectedDeviceId,
        out SessionTicketClaims? claims,
        out string? failureReason);
}

public sealed class SessionTicketService : ISessionTicketService
{
    private readonly byte[] _signingKey;

    public SessionTicketService(string? secretKey = null)
    {
        var keyMaterial = string.IsNullOrWhiteSpace(secretKey)
            ? "RemoteDesk-V1-LAN-Pilot-Default-HMAC-Signing-Key-2026"
            : secretKey;
        _signingKey = SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial));
    }

    public string IssueTicket(
        Guid sessionId,
        Guid deviceId,
        Guid operatorUserId,
        bool allowInputControl,
        DateTimeOffset expiresAt)
    {
        var claims = new SessionTicketClaims(
            sessionId,
            deviceId,
            operatorUserId,
            allowInputControl,
            expiresAt.ToUnixTimeSeconds());

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(claims);
        var payloadB64 = Convert.ToBase64String(payloadBytes);
        var signatureBytes = HMACSHA256.HashData(_signingKey, Encoding.ASCII.GetBytes(payloadB64));
        var signatureB64 = Convert.ToBase64String(signatureBytes);

        return $"{payloadB64}.{signatureB64}";
    }

    public bool TryValidateTicket(
        string ticket,
        Guid expectedSessionId,
        Guid expectedDeviceId,
        out SessionTicketClaims? claims,
        out string? failureReason)
    {
        claims = null;
        failureReason = null;

        if (string.IsNullOrWhiteSpace(ticket))
        {
            failureReason = "Session ticket is empty.";
            return false;
        }

        var parts = ticket.Split('.');
        if (parts.Length != 2)
        {
            failureReason = "Malformed session ticket format.";
            return false;
        }

        var payloadB64 = parts[0];
        byte[] providedSig;
        try
        {
            providedSig = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException)
        {
            failureReason = "Invalid ticket signature encoding.";
            return false;
        }

        var expectedSig = HMACSHA256.HashData(_signingKey, Encoding.ASCII.GetBytes(payloadB64));
        if (!CryptographicOperations.FixedTimeEquals(providedSig, expectedSig))
        {
            failureReason = "Session ticket signature verification failed.";
            return false;
        }

        try
        {
            var payloadBytes = Convert.FromBase64String(payloadB64);
            claims = JsonSerializer.Deserialize<SessionTicketClaims>(payloadBytes);
        }
        catch (Exception)
        {
            failureReason = "Invalid session ticket payload.";
            return false;
        }

        if (claims is null)
        {
            failureReason = "Empty session ticket claims.";
            return false;
        }

        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (claims.ExpiresAtUnixSeconds < nowUnix)
        {
            failureReason = "Session ticket has expired.";
            return false;
        }

        if (claims.SessionId != expectedSessionId || claims.DeviceId != expectedDeviceId)
        {
            failureReason = "Session ticket does not match target session or device.";
            return false;
        }

        return true;
    }
}

