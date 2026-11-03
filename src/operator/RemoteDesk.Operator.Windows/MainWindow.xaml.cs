using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RemoteDesk.Contracts.Sessions;
using RemoteDesk.Transport.Abstractions;
using RemoteDesk.Transport.DirectLan;
using RemoteDesk.Transport.LocalRelay;

namespace RemoteDesk.Operator.Windows;

public sealed record DeviceComboItem(DeviceSummaryDto Device)
{
    public string DisplayLabel => $"{Device.Hostname} ({Device.Status}) [{Device.Id.ToString()[..8]}]";
}

public partial class MainWindow : Window
{
    private static readonly Guid DefaultPilotOrgId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DefaultOperatorUserId = Guid.Parse("33333333-3333-3333-3333-333333333331");

    private readonly HttpClient _httpClient = new();
    private readonly DispatcherTimer _telemetryTimer;
    private IRemoteTransport? _activeTransport;
    private Guid? _activeSessionId;
    private bool _allowInputControl;
    private byte[]? _lastFrameBytes;
    private string _connectedHostname = "RemotePC";
    private DateTimeOffset _lastMouseMoveSent = DateTimeOffset.MinValue;

    public MainWindow()
    {
        InitializeComponent();

        _telemetryTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _telemetryTimer.Tick += TelemetryTimer_Tick;
    }

    private async void RefreshDevicesBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var baseUrl = ApiUrlTextBox.Text.Trim().TrimEnd('/');
            StatusTextBlock.Text = "Loading devices...";
            var devices = await _httpClient.GetFromJsonAsync<List<DeviceSummaryDto>>($"{baseUrl}/api/devices");
            var items = (devices ?? [])
                .OrderByDescending(d => d.IsOnline)
                .ThenByDescending(d => d.LastSeenAt)
                .Select(d => new DeviceComboItem(d))
                .ToList();
            DevicesComboBox.ItemsSource = items;
            if (items.Count > 0)
            {
                DevicesComboBox.SelectedIndex = 0;
            }

            StatusTextBlock.Text = $"Loaded {items.Count} device(s)";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"API Error: {ex.Message}";
        }
    }

    private async void ConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesComboBox.SelectedItem is not DeviceComboItem selected)
        {
            MessageBox.Show("Please select a device first.", "RemoteDesk Operator", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var baseUrl = ApiUrlTextBox.Text.Trim().TrimEnd('/');
            _allowInputControl = AllowInputCheckBox.IsChecked == true;
            StatusTextBlock.Text = $"Requesting session ticket for {selected.Device.Hostname}...";

            var req = new StartSessionRequest(
                DefaultPilotOrgId,
                selected.Device.Id,
                DefaultOperatorUserId,
                "Pilot Operator Admin",
                AllowInputControl: _allowInputControl,
                IdempotencyKey: Guid.NewGuid().ToString("N"));

            var resp = await _httpClient.PostAsJsonAsync($"{baseUrl}/api/sessions/request", req);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync();
                StatusTextBlock.Text = $"Session denied: {err}";
                return;
            }

            var sessionResp = await resp.Content.ReadFromJsonAsync<StartSessionResponse>();
            if (sessionResp is null)
            {
                return;
            }

            _activeSessionId = sessionResp.ConnectionInfo.SessionId;
            _activeTransport = new HybridLanTransportOrchestrator(
                new DirectLanTransport(),
                new LocalRelayTransport());
            _activeTransport.FrameReceived += OnFrameReceived;
            _activeTransport.InputAckReceived += OnInputAckReceived;
            _activeTransport.RemoteDisconnected += OnRemoteDisconnected;

            var connectResult = await _activeTransport.ConnectAsync(sessionResp.ConnectionInfo, CancellationToken.None);
            if (!connectResult.IsConnected)
            {
                StatusTextBlock.Text = $"Connection failed: {connectResult.FailureReason}";
                await _activeTransport.DisposeAsync();
                _activeTransport = null;
                return;
            }

            ConnectBtn.IsEnabled = false;
            DisconnectBtn.IsEnabled = true;
            SnapshotBtn.IsEnabled = true;
            SendClipboardBtn.IsEnabled = _allowInputControl;
            AllowInputCheckBox.IsEnabled = false;
            _connectedHostname = selected.Device.Hostname;
            PlaceholderText.Visibility = Visibility.Collapsed;
            var controlMode = _allowInputControl ? "View + Control" : "View Only";
            TransportBadgeText.Text = $"Mode: {connectResult.ActiveTransportType} ({controlMode})";
            StatusTextBlock.Text = $"Connected to {selected.Device.Hostname} (Slot #{sessionResp.ReservedSlotIndex + 1}/5)";
            RemoteScreenImage.Focus();
            _telemetryTimer.Start();
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Connection error: {ex.Message}";
        }
    }

    private async void DisconnectBtn_Click(object sender, RoutedEventArgs e)
    {
        await DisconnectCurrentSessionAsync();
    }

    private void QualityComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RemoteScreenImage is null)
        {
            return;
        }

        var mode = QualityComboBox.SelectedIndex == 0
            ? BitmapScalingMode.HighQuality
            : BitmapScalingMode.LowQuality;
        RenderOptions.SetBitmapScalingMode(RemoteScreenImage, mode);
    }

    private void SnapshotBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_lastFrameBytes is null || _lastFrameBytes.Length == 0)
        {
            StatusTextBlock.Text = "No active frame to snapshot.";
            return;
        }

        try
        {
            var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var snapDir = Path.Combine(desktopDir, "RemoteDesk_Snapshots");
            Directory.CreateDirectory(snapDir);
            var fileName = $"Snapshot_{_connectedHostname}_{DateTime.Now:yyyyMMdd_HHmmss}.jpg";
            var fullPath = Path.Combine(snapDir, fileName);
            File.WriteAllBytes(fullPath, _lastFrameBytes);
            StatusTextBlock.Text = $"Saved snapshot: Desktop\\RemoteDesk_Snapshots\\{fileName}";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Snapshot failed: {ex.Message}";
        }
    }

    private async void SendClipboardBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_allowInputControl || _activeTransport is null || !_activeSessionId.HasValue)
        {
            return;
        }

        if (!Clipboard.ContainsText())
        {
            StatusTextBlock.Text = "Clipboard does not contain text.";
            return;
        }

        var text = Clipboard.GetText();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var clipped = text.Length > 120 ? text[..120] : text;
        int sent = 0;
        foreach (var ch in clipped.ToUpperInvariant())
        {
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9' or ' ')
            {
                int vk = ch;
                var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await SendInputSafeAsync(new RemoteInputEventPacket(_activeSessionId.Value, RemoteInputEventType.KeyDown, 0, 0, MouseButtonType.None, 0, vk, ts));
                await SendInputSafeAsync(new RemoteInputEventPacket(_activeSessionId.Value, RemoteInputEventType.KeyUp, 0, 0, MouseButtonType.None, 0, vk, ts));
                sent++;
            }
        }

        StatusTextBlock.Text = $"Sent {sent} character(s) from clipboard to {_connectedHostname}";
    }

    private void FullScreenBtn_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized && WindowStyle == WindowStyle.None)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = WindowState.Normal;
            FullScreenBtn.Content = "Full Screen";
        }
        else
        {
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            FullScreenBtn.Content = "Exit Full";
        }
    }

    private async void RemoteScreenImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_allowInputControl || _activeTransport is null || !_activeSessionId.HasValue)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if ((now - _lastMouseMoveSent).TotalMilliseconds < 35)
        {
            return;
        }

        _lastMouseMoveSent = now;
        var (nx, ny) = GetNormalizedPosition(e);
        await SendInputSafeAsync(new RemoteInputEventPacket(
            _activeSessionId.Value,
            RemoteInputEventType.MouseMove,
            nx,
            ny,
            MouseButtonType.None,
            0,
            0,
            now.ToUnixTimeMilliseconds()));
    }

    private async void RemoteScreenImage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        RemoteScreenImage.Focus();
        if (!_allowInputControl || _activeTransport is null || !_activeSessionId.HasValue)
        {
            return;
        }

        var (nx, ny) = GetNormalizedPosition(e);
        await SendInputSafeAsync(new RemoteInputEventPacket(
            _activeSessionId.Value,
            RemoteInputEventType.MouseDown,
            nx,
            ny,
            MapMouseButton(e.ChangedButton),
            0,
            0,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    private async void RemoteScreenImage_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_allowInputControl || _activeTransport is null || !_activeSessionId.HasValue)
        {
            return;
        }

        var (nx, ny) = GetNormalizedPosition(e);
        await SendInputSafeAsync(new RemoteInputEventPacket(
            _activeSessionId.Value,
            RemoteInputEventType.MouseUp,
            nx,
            ny,
            MapMouseButton(e.ChangedButton),
            0,
            0,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    private async void RemoteScreenImage_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_allowInputControl || _activeTransport is null || !_activeSessionId.HasValue)
        {
            return;
        }

        var (nx, ny) = GetNormalizedPosition(e);
        await SendInputSafeAsync(new RemoteInputEventPacket(
            _activeSessionId.Value,
            RemoteInputEventType.MouseWheel,
            nx,
            ny,
            MouseButtonType.None,
            e.Delta,
            0,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_allowInputControl || _activeTransport is null || !_activeSessionId.HasValue)
        {
            return;
        }

        var vk = KeyInterop.VirtualKeyFromKey(e.Key);
        await SendInputSafeAsync(new RemoteInputEventPacket(
            _activeSessionId.Value,
            RemoteInputEventType.KeyDown,
            0,
            0,
            MouseButtonType.None,
            0,
            vk,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    private async void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_allowInputControl || _activeTransport is null || !_activeSessionId.HasValue)
        {
            return;
        }

        var vk = KeyInterop.VirtualKeyFromKey(e.Key);
        await SendInputSafeAsync(new RemoteInputEventPacket(
            _activeSessionId.Value,
            RemoteInputEventType.KeyUp,
            0,
            0,
            MouseButtonType.None,
            0,
            vk,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    private (double Nx, double Ny) GetNormalizedPosition(MouseEventArgs e)
    {
        var w = Math.Max(1.0, RemoteScreenImage.ActualWidth);
        var h = Math.Max(1.0, RemoteScreenImage.ActualHeight);
        var pt = e.GetPosition(RemoteScreenImage);
        return (Math.Clamp(pt.X / w, 0.0, 1.0), Math.Clamp(pt.Y / h, 0.0, 1.0));
    }

    private static MouseButtonType MapMouseButton(MouseButton btn) => btn switch
    {
        MouseButton.Right => MouseButtonType.Right,
        MouseButton.Middle => MouseButtonType.Middle,
        _ => MouseButtonType.Left
    };

    private async Task SendInputSafeAsync(RemoteInputEventPacket packet)
    {
        try
        {
            if (_activeTransport is not null)
            {
                await _activeTransport.SendInputEventAsync(packet, CancellationToken.None);
            }
        }
        catch
        {
            // Ignore transient input send failure
        }
    }

    private void OnInputAckReceived(InputControlAckPacket ack)
    {
        Dispatcher.InvokeAsync(() =>
        {
            TelemetryInputText.Text = ack.Accepted
                ? $"Input Events: {ack.ProcessedEventsCount} OK"
                : $"Input Blocked (View-Only): {ack.BlockedEventsCount}";
        });
    }

    private void OnRemoteDisconnected(SessionDisconnectPacket disc)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            await DisconnectCurrentSessionAsync();
            StatusTextBlock.Text = $"Disconnected by {disc.InitiatedBy}: {disc.Reason}";
        });
    }

    private void OnFrameReceived(ScreenFramePacket frame)
    {
        if (frame.Payload is null || frame.Payload.Length == 0)
        {
            return;
        }

        _lastFrameBytes = frame.Payload;

        Dispatcher.InvokeAsync(() =>
        {
            try
            {
                using var ms = new MemoryStream(frame.Payload);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
                bitmap.Freeze();
                RemoteScreenImage.Source = bitmap;
            }
            catch
            {
                // Skip non-image synthetic frames during unit/headless tests
            }
        });
    }

    private void TelemetryTimer_Tick(object? sender, EventArgs e)
    {
        if (_activeTransport is null)
        {
            return;
        }

        var snapshot = _activeTransport.GetTelemetrySnapshot();
        TelemetryFpsText.Text = $"FPS: {snapshot.CurrentFps:F1}";
        TelemetryLatencyText.Text = $"Latency: {snapshot.AverageLatencyMs:F1} ms";
        TelemetryBandwidthText.Text = $"Bandwidth: {snapshot.BandwidthKbps:F1} kbps";
        TelemetryFramesText.Text = $"Frames: {snapshot.TotalFramesReceived}";
    }

    private async Task DisconnectCurrentSessionAsync()
    {
        _telemetryTimer.Stop();

        if (_activeTransport is not null)
        {
            _activeTransport.FrameReceived -= OnFrameReceived;
            _activeTransport.InputAckReceived -= OnInputAckReceived;
            _activeTransport.RemoteDisconnected -= OnRemoteDisconnected;
            await _activeTransport.DisconnectAsync(CancellationToken.None);
            await _activeTransport.DisposeAsync();
            _activeTransport = null;
        }

        if (_activeSessionId.HasValue)
        {
            try
            {
                var baseUrl = ApiUrlTextBox.Text.Trim().TrimEnd('/');
                await _httpClient.PostAsJsonAsync(
                    $"{baseUrl}/api/sessions/{_activeSessionId.Value}/end",
                    new EndSessionRequest(DefaultOperatorUserId, "Operator disconnected"));
            }
            catch
            {
                // Ignore API unreachable during disconnect
            }

            _activeSessionId = null;
        }

        ConnectBtn.IsEnabled = true;
        DisconnectBtn.IsEnabled = false;
        SnapshotBtn.IsEnabled = false;
        SendClipboardBtn.IsEnabled = false;
        AllowInputCheckBox.IsEnabled = true;
        _lastFrameBytes = null;
        PlaceholderText.Visibility = Visibility.Visible;
        RemoteScreenImage.Source = null;
        TransportBadgeText.Text = "Mode: Disconnected";
        StatusTextBlock.Text = "Disconnected";
    }
}
