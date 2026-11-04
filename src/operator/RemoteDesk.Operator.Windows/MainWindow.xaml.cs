using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
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
    private string? _lastSyncedClipboardText;
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
        await ReloadDevicesListAsync();
    }

    private async Task ReloadDevicesListAsync(Guid? selectDeviceId = null)
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
            if (selectDeviceId.HasValue)
            {
                var match = items.FirstOrDefault(x => x.Device.Id == selectDeviceId.Value);
                if (match is not null)
                {
                    DevicesComboBox.SelectedItem = match;
                }
            }
            else if (items.Count > 0)
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

    private async void AddDeviceBtn_Click(object sender, RoutedEventArgs e)
    {
        var input = PromptAddDeviceDialog();
        if (input is null)
        {
            return;
        }

        try
        {
            var baseUrl = ApiUrlTextBox.Text.Trim().TrimEnd('/');
            var req = new AddManualDeviceRequest(input.Value.Name, input.Value.IpAddress, input.Value.Port, DefaultPilotOrgId, null);
            var resp = await _httpClient.PostAsJsonAsync($"{baseUrl}/api/devices/manual", req);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync();
                MessageBox.Show($"ไม่สามารถเพิ่มอุปกรณ์ได้: {err}", "Add Device Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var added = await resp.Content.ReadFromJsonAsync<DeviceSummaryDto>();
            await ReloadDevicesListAsync(added?.Id);
            StatusTextBlock.Text = $"เพิ่มอุปกรณ์ '{input.Value.Name}' ({input.Value.IpAddress}:{input.Value.Port}) เรียบร้อยแล้ว";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Add device error: {ex.Message}";
        }
    }

    private async void RenameDeviceBtn_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesComboBox.SelectedItem is not DeviceComboItem selected)
        {
            MessageBox.Show("กรุณาเลือกอุปกรณ์ที่ต้องการเปลี่ยนชื่อก่อน", "Rename Device", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var newName = PromptTextDialog("แก้ไขชื่ออุปกรณ์ (Rename Device)", "ชื่ออุปกรณ์ใหม่:", selected.Device.Hostname);
        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == selected.Device.Hostname)
        {
            return;
        }

        try
        {
            var baseUrl = ApiUrlTextBox.Text.Trim().TrimEnd('/');
            var resp = await _httpClient.PutAsJsonAsync($"{baseUrl}/api/devices/{selected.Device.Id}/rename", new RenameDeviceRequest(newName.Trim()));
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync();
                MessageBox.Show($"เปลี่ยนชื่อไม่สำเร็จ: {err}", "Rename Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await ReloadDevicesListAsync(selected.Device.Id);
            StatusTextBlock.Text = $"เปลี่ยนชื่อเป็น '{newName.Trim()}' เรียบร้อยแล้ว";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Rename error: {ex.Message}";
        }
    }

    private async void DeleteDeviceBtn_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesComboBox.SelectedItem is not DeviceComboItem selected)
        {
            MessageBox.Show("กรุณาเลือกอุปกรณ์ที่ต้องการลบก่อน", "Delete Device", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"ยืนยันการลบอุปกรณ์ '{selected.Device.Hostname}' ({selected.Device.Id.ToString()[..8]}) ออกจากระบบ?",
            "Confirm Delete Device",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var baseUrl = ApiUrlTextBox.Text.Trim().TrimEnd('/');
            var resp = await _httpClient.DeleteAsync($"{baseUrl}/api/devices/{selected.Device.Id}");
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync();
                MessageBox.Show($"ลบอุปกรณ์ไม่สำเร็จ: {err}", "Delete Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await ReloadDevicesListAsync();
            StatusTextBlock.Text = $"ลบอุปกรณ์ '{selected.Device.Hostname}' เรียบร้อยแล้ว";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Delete error: {ex.Message}";
        }
    }

    private (string Name, string IpAddress, int Port)? PromptAddDeviceDialog()
    {
        var dlg = new Window
        {
            Title = "เพิ่มอุปกรณ์ใหม่ (Add Manual LAN Device)",
            Width = 380,
            Height = 250,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
            Foreground = Brushes.White
        };

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "ชื่ออุปกรณ์ (Device Name):", Margin = new Thickness(0, 0, 0, 4) });
        var nameBox = new TextBox { Text = "Remote-PC", Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(nameBox);

        panel.Children.Add(new TextBlock { Text = "IP Address ของเครื่องปลายทาง (และ Port):", Margin = new Thickness(0, 0, 0, 4) });
        var ipRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        var ipBox = new TextBox { Text = "192.168.1.", Width = 230, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 8, 0) };
        var portBox = new TextBox { Text = "52000", Width = 95, Padding = new Thickness(6, 4, 6, 4) };
        ipRow.Children.Add(ipBox);
        ipRow.Children.Add(portBox);
        panel.Children.Add(ipRow);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var okBtn = new Button { Content = "บันทึก (Add)", Width = 95, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancelBtn = new Button { Content = "ยกเลิก", Width = 80, Padding = new Thickness(8, 5, 8, 5), IsCancel = true };
        okBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text) || string.IsNullOrWhiteSpace(ipBox.Text))
            {
                MessageBox.Show("กรุณากรอกชื่อเครื่องและ IP Address", "Add Device", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            dlg.DialogResult = true;
        };
        btnRow.Children.Add(okBtn);
        btnRow.Children.Add(cancelBtn);
        panel.Children.Add(btnRow);

        dlg.Content = panel;
        if (dlg.ShowDialog() == true)
        {
            var port = int.TryParse(portBox.Text.Trim(), out var p) && p is > 0 and <= 65535 ? p : 52000;
            return (nameBox.Text.Trim(), ipBox.Text.Trim(), port);
        }

        return null;
    }

    private string? PromptTextDialog(string title, string label, string defaultValue)
    {
        var dlg = new Window
        {
            Title = title,
            Width = 360,
            Height = 175,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
            Foreground = Brushes.White
        };

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
        var textBox = new TextBox { Text = defaultValue, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 0, 14) };
        panel.Children.Add(textBox);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var okBtn = new Button { Content = "บันทึก", Width = 85, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancelBtn = new Button { Content = "ยกเลิก", Width = 80, Padding = new Thickness(8, 5, 8, 5), IsCancel = true };
        okBtn.Click += (_, _) => dlg.DialogResult = true;
        btnRow.Children.Add(okBtn);
        btnRow.Children.Add(cancelBtn);
        panel.Children.Add(btnRow);

        dlg.Content = panel;
        textBox.Focus();
        textBox.SelectAll();
        return dlg.ShowDialog() == true ? textBox.Text.Trim() : null;
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
            var orchestrator = new HybridLanTransportOrchestrator(
                new DirectLanTransport(),
                new LocalRelayTransport());
            orchestrator.FileTransferAckReceived += OnFileTransferAckReceived;
            orchestrator.ClipboardSyncReceived += OnClipboardSyncReceived;
            _activeTransport = orchestrator;
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
            SendFileBtn.IsEnabled = _allowInputControl;
            SendClipboardBtn.IsEnabled = _allowInputControl;
            AllowInputCheckBox.IsEnabled = false;
            _connectedHostname = selected.Device.Hostname;
            PlaceholderText.Visibility = Visibility.Collapsed;
            var controlMode = _allowInputControl ? "View + Control" : "View Only";
            TransportBadgeText.Text = $"Mode: {connectResult.ActiveTransportType} ({controlMode})";
            StatusTextBlock.Text = $"Connected to {selected.Device.Hostname} (Slot #{sessionResp.ReservedSlotIndex + 1}/5)";
            RemoteScreenImage.Focus();
            _telemetryTimer.Start();
            await ApplyQualityProfileAsync(GetSelectedQualityProfile());
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

    private async void SendFileBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_allowInputControl || _activeTransport is not IFileTransferTransport || !_activeSessionId.HasValue)
        {
            return;
        }

        var dlg = new OpenFileDialog
        {
            Title = "Select a file to send to the Remote PC (Desktop\\RemoteDesk_ReceivedFiles)",
            CheckFileExists = true
        };

        if (dlg.ShowDialog(this) == true)
        {
            await SendFileOverTransportAsync(dlg.FileName);
        }
    }

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (_allowInputControl && _activeTransport is IFileTransferTransport && e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            DragDropOverlayBorder.Visibility = Visibility.Visible;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void Window_PreviewDragLeave(object sender, DragEventArgs e)
    {
        DragDropOverlayBorder.Visibility = Visibility.Collapsed;
    }

    private async void RemoteScreen_Drop(object sender, DragEventArgs e)
    {
        DragDropOverlayBorder.Visibility = Visibility.Collapsed;
        e.Handled = true;

        if (!_allowInputControl || _activeTransport is not IFileTransferTransport || !_activeSessionId.HasValue)
        {
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            files.Length > 0)
        {
            foreach (var file in files)
            {
                if (File.Exists(file))
                {
                    await SendFileOverTransportAsync(file);
                }
            }
        }
    }

    private async Task SendFileOverTransportAsync(string filePath)
    {
        if (_activeTransport is not IFileTransferTransport fileTransport || !_activeSessionId.HasValue)
        {
            return;
        }

        try
        {
            var info = new FileInfo(filePath);
            var transferId = Guid.NewGuid();
            const int chunkSize = 128 * 1024; // 128 KB chunks for smooth concurrent video streaming
            int totalChunks = (int)Math.Max(1, (info.Length + chunkSize - 1) / chunkSize);

            SendFileBtn.IsEnabled = false;
            StatusTextBlock.Text = $"Sending '{info.Name}' ({info.Length / 1024.0:F1} KB)...";

            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, chunkSize, useAsync: true);
            var buffer = new byte[chunkSize];
            int chunkIndex = 0;

            while (true)
            {
                int read = await fs.ReadAsync(buffer);
                if (read == 0 && chunkIndex > 0)
                {
                    break;
                }

                var slice = read > 0 ? buffer[..read] : [];
                var pkt = new FileTransferPacket(
                    _activeSessionId.Value,
                    transferId,
                    info.Name,
                    info.Length,
                    chunkIndex,
                    totalChunks,
                    slice);

                await fileTransport.SendFileChunkAsync(pkt, CancellationToken.None);
                chunkIndex++;
                if (read == 0 || chunkIndex >= totalChunks)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"File transfer failed: {ex.Message}";
        }
        finally
        {
            SendFileBtn.IsEnabled = _allowInputControl && _activeTransport is not null;
        }
    }

    private void OnFileTransferAckReceived(FileTransferAckPacket ack)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (!ack.Accepted)
            {
                StatusTextBlock.Text = $"File transfer rejected: {ack.SavedPathOrError}";
                return;
            }

            if (ack.Completed)
            {
                StatusTextBlock.Text = $"File '{ack.FileName}' saved on {_connectedHostname} (Desktop\\RemoteDesk_ReceivedFiles + Ready to Ctrl+V)";
            }
            else
            {
                int pct = (int)Math.Round(((ack.ChunkIndex + 1) * 100.0) / Math.Max(1, ack.TotalChunks));
                StatusTextBlock.Text = $"Sending '{ack.FileName}'... {pct}% ({ack.ChunkIndex + 1}/{ack.TotalChunks})";
            }
        });
    }

    private void OnClipboardSyncReceived(ClipboardSyncPacket clip)
    {
        if (string.IsNullOrEmpty(clip.Text))
        {
            return;
        }

        Dispatcher.InvokeAsync(() =>
        {
            try
            {
                _lastSyncedClipboardText = clip.Text;
                Clipboard.SetText(clip.Text);
                StatusTextBlock.Text = $"Clipboard synced from {_connectedHostname} ({clip.Text.Length} chars)";
            }
            catch
            {
                // Ignore transient clipboard lock
            }
        });
    }

    private void QualityComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RemoteScreenImage is null)
        {
            return;
        }

        var profile = GetSelectedQualityProfile();
        RenderOptions.SetBitmapScalingMode(
            RemoteScreenImage,
            profile == StreamQualityProfile.Fast ? BitmapScalingMode.LowQuality : BitmapScalingMode.HighQuality);

        _ = ApplyQualityProfileAsync(profile);
    }

    private StreamQualityProfile GetSelectedQualityProfile() => QualityComboBox.SelectedIndex switch
    {
        1 => StreamQualityProfile.Sharp,
        2 => StreamQualityProfile.Balanced,
        3 => StreamQualityProfile.Fast,
        _ => StreamQualityProfile.Ultra
    };

    private async Task ApplyQualityProfileAsync(StreamQualityProfile profile)
    {
        if (_activeTransport is not IStreamQualityControl qualityControl)
        {
            return;
        }

        try
        {
            await qualityControl.SetQualityProfileAsync(profile, CancellationToken.None);
            StatusTextBlock.Text = $"Quality: {profile} — {_connectedHostname}";
        }
        catch
        {
            // Ignore if the session just closed
        }
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
        if (!_allowInputControl || _activeTransport is not IFileTransferTransport fileTransport || !_activeSessionId.HasValue)
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

        _lastSyncedClipboardText = text;
        await fileTransport.SendClipboardSyncAsync(
            new ClipboardSyncPacket(_activeSessionId.Value, text, TriggerPaste: true),
            CancellationToken.None);

        StatusTextBlock.Text = $"Pasted {text.Length} character(s) from clipboard to {_connectedHostname}";
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
        if ((now - _lastMouseMoveSent).TotalMilliseconds < 8)
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

        // Intercept Ctrl+V: push local Unicode clipboard (Thai/English/multiline) to Remote PC and trigger paste
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
            e.Key == Key.V &&
            _activeTransport is IFileTransferTransport fileTransport &&
            Clipboard.ContainsText())
        {
            try
            {
                var text = Clipboard.GetText();
                if (!string.IsNullOrEmpty(text))
                {
                    e.Handled = true;
                    _lastSyncedClipboardText = text;
                    await fileTransport.SendClipboardSyncAsync(
                        new ClipboardSyncPacket(_activeSessionId.Value, text, TriggerPaste: true),
                        CancellationToken.None);
                    StatusTextBlock.Text = $"Ctrl+V: Pasted {text.Length} character(s) to {_connectedHostname}";
                    return;
                }
            }
            catch
            {
                // Fall back to standard key injection if clipboard is busy
            }
        }

        var actualKey = e.Key == Key.System ? e.SystemKey : e.Key;
        var vk = KeyInterop.VirtualKeyFromKey(actualKey);
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

        var actualKey = e.Key == Key.System ? e.SystemKey : e.Key;
        var vk = KeyInterop.VirtualKeyFromKey(actualKey);
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

        // Decode on the network thread (frozen bitmaps are thread-safe), then hand only the
        // LATEST frame to the UI. Stale frames are dropped instead of queuing up => no growing lag.
        BitmapImage bitmap;
        try
        {
            using var ms = new MemoryStream(frame.Payload, writable: false);
            bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
        }
        catch
        {
            // Skip non-image synthetic frames during unit/headless tests
            return;
        }

        Interlocked.Exchange(ref _pendingFrame, bitmap);
        if (Interlocked.CompareExchange(ref _renderScheduled, 1, 0) == 0)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(RenderLatestFrame));
        }
    }

    private BitmapImage? _pendingFrame;
    private int _renderScheduled;

    private void RenderLatestFrame()
    {
        Interlocked.Exchange(ref _renderScheduled, 0);
        var latest = Interlocked.Exchange(ref _pendingFrame, null);
        if (latest is not null && _activeTransport is not null)
        {
            RemoteScreenImage.Source = latest;
        }
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

        // Auto-sync Operator clipboard changes to the Remote PC in the background
        if (_allowInputControl &&
            _activeSessionId.HasValue &&
            _activeTransport is IFileTransferTransport fileTransport)
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    var currentText = Clipboard.GetText();
                    if (!string.IsNullOrEmpty(currentText) &&
                        currentText.Length <= 65536 &&
                        !string.Equals(currentText, _lastSyncedClipboardText, StringComparison.Ordinal))
                    {
                        _lastSyncedClipboardText = currentText;
                        _ = fileTransport.SendClipboardSyncAsync(
                            new ClipboardSyncPacket(_activeSessionId.Value, currentText, TriggerPaste: false),
                            CancellationToken.None);
                    }
                }
            }
            catch
            {
                // Ignore transient clipboard access contention
            }
        }
    }

    private async Task DisconnectCurrentSessionAsync()
    {
        _telemetryTimer.Stop();

        if (_activeTransport is not null)
        {
            if (_activeTransport is IFileTransferTransport ft)
            {
                ft.FileTransferAckReceived -= OnFileTransferAckReceived;
                ft.ClipboardSyncReceived -= OnClipboardSyncReceived;
            }
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
        SendFileBtn.IsEnabled = false;
        SendClipboardBtn.IsEnabled = false;
        AllowInputCheckBox.IsEnabled = true;
        _lastFrameBytes = null;
        PlaceholderText.Visibility = Visibility.Visible;
        RemoteScreenImage.Source = null;
        TransportBadgeText.Text = "Mode: Disconnected";
        StatusTextBlock.Text = "Disconnected";
    }
}
