using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CinemaTogether.Core;
using CinemaTogether.Infrastructure;
using CinemaTogether.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace CinemaTogether.Desktop;

public partial class MainWindow : Window
{
    private readonly string dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CinemaTogether");
    private readonly SettingsStore store;
    private readonly MediaLibrary library;
    private readonly ObservableCollection<MediaItem> media = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource lifetime = new();
    private AppSettings settings = new();
    private WebApplication? server;
    private string hostSecret = "";
    private bool initialized, importing, transitioning, closing, closed;
    private Task? importTask;
    private long lastBytes;
    private DateTime lastSample = DateTime.UtcNow;

    public MainWindow()
    {
        InitializeComponent(); store = new(dataDirectory); library = new(dataDirectory);
        LibraryGrid.ItemsSource = media;
        timer.Tick += (_, _) => UpdateStatus();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        settings = await store.LoadAsync(); await library.InitializeAsync();
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools");
        if (settings.FfmpegPath == "ffmpeg" && File.Exists(Path.Combine(bundled, "ffmpeg.exe"))) settings.FfmpegPath = Path.Combine(bundled, "ffmpeg.exe");
        if (settings.FfprobePath == "ffprobe" && File.Exists(Path.Combine(bundled, "ffprobe.exe"))) settings.FfprobePath = Path.Combine(bundled, "ffprobe.exe");
        FillSettings(); await RefreshLibrary(); initialized = true; timer.Start();
        if (string.IsNullOrEmpty(settings.PasswordHash)) { ShowPanel("Settings"); StatusText.Text = "Задайте пароль комнаты перед первым запуском."; }
    });

    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            try { Directory.CreateDirectory(Path.Combine(dataDirectory, "logs")); await File.AppendAllTextAsync(Path.Combine(dataDirectory, "logs", "desktop.log"), $"{DateTimeOffset.UtcNow:O} {ex}\n"); } catch (IOException) { }
            MessageBox.Show(this, ex.Message, "Cinema Together", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void FillSettings()
    {
        RoomNameInput.Text = settings.RoomName; HomeRoomName.Text = settings.RoomName;
        AddressInput.Text = settings.ListenAddress; PortInput.Text = settings.Port.ToString();
        FfmpegInput.Text = settings.FfmpegPath; FfprobeInput.Text = settings.FfprobePath;
        EncoderInput.Text = settings.HardwareEncoder; CertificateInput.Text = settings.CertificatePath ?? "";
    }

    private async Task SaveSettings()
    {
        if (server is not null || transitioning || importing) throw new InvalidOperationException("Остановите сервер и дождитесь импорта перед изменением настроек.");
        if (!IPAddress.TryParse(AddressInput.Text.Trim(), out _) || !int.TryParse(PortInput.Text, out var port) || port is < 1 or > 65535)
            throw new ArgumentException("Укажите корректный IP и порт от 1 до 65535.");
        if (string.IsNullOrWhiteSpace(RoomNameInput.Text) || string.IsNullOrWhiteSpace(FfmpegInput.Text) || string.IsNullOrWhiteSpace(FfprobeInput.Text))
            throw new ArgumentException("Заполните название комнаты и пути к FFmpeg/FFprobe.");
        if (EncoderInput.Text is not ("libx264" or "h264_nvenc" or "h264_qsv" or "h264_amf")) throw new ArgumentException("Неизвестный видеокодировщик.");
        var hash = string.IsNullOrEmpty(PasswordInput.Password) ? settings.PasswordHash : SettingsStore.HashPassword(PasswordInput.Password);
        if (string.IsNullOrEmpty(hash)) throw new ArgumentException("Задайте пароль комнаты.");
        settings = new()
        {
            ListenAddress = AddressInput.Text.Trim(), Port = port, RoomName = RoomNameInput.Text.Trim(), PasswordHash = hash,
            FfmpegPath = FfmpegInput.Text.Trim(), FfprobePath = FfprobeInput.Text.Trim(), HardwareEncoder = EncoderInput.Text,
            CertificatePath = string.IsNullOrWhiteSpace(CertificateInput.Text) ? null : Path.GetFullPath(CertificateInput.Text.Trim()),
            CertificatePassword = CertificatePasswordInput.Password, MaxParticipants = settings.MaxParticipants
        };
        await store.SaveAsync(settings); PasswordInput.Clear(); HomeRoomName.Text = settings.RoomName; StatusText.Text = "Настройки сохранены.";
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e) => await Guard(SaveSettings);
    private void Navigate_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: string name }) ShowPanel(name); }
    private void ShowPanel(string name)
    {
        HomePanel.Visibility = name == "Home" ? Visibility.Visible : Visibility.Collapsed;
        LibraryPanel.Visibility = name == "Library" ? Visibility.Visible : Visibility.Collapsed;
        RoomPanel.Visibility = name == "Room" ? Visibility.Visible : Visibility.Collapsed;
        PeoplePanel.Visibility = name == "People" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = name == "Settings" ? Visibility.Visible : Visibility.Collapsed;
    }

    private string LocalUrl => $"{(string.IsNullOrEmpty(settings.CertificatePath) ? "http" : "https")}://127.0.0.1:{settings.Port}";
    private IEnumerable<string> Links()
    {
        var scheme = string.IsNullOrEmpty(settings.CertificatePath) ? "http" : "https";
        if (settings.ListenAddress is not ("0.0.0.0" or "::")) return [$"{scheme}://{(settings.ListenAddress.Contains(':') ? "[" + settings.ListenAddress + "]" : settings.ListenAddress)}:{settings.Port}"];
        return Dns.GetHostAddresses(Dns.GetHostName()).Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)).Select(a => $"{scheme}://{a}:{settings.Port}").DefaultIfEmpty(LocalUrl);
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (!initialized || server is not null || transitioning) return;
        await SaveSettings(); transitioning = true; StartButton.IsEnabled = false;
        WebApplication? candidate = null;
        try
        {
            hostSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            candidate = ServerHost.Build(settings, dataDirectory, hostSecret);
            await candidate.Services.GetRequiredService<MediaLibrary>().InitializeAsync();
            await candidate.StartAsync(lifetime.Token); server = candidate;
            lastBytes = 0; lastSample = DateTime.UtcNow; StopButton.IsEnabled = true;
            ConnectionLinks.Text = string.Join(Environment.NewLine, Links());
            StatusText.Text = "Сервер запущен. Выберите видео в медиатеке и откройте кинозал.";
        }
        catch { if (candidate is not null) await candidate.DisposeAsync(); throw; }
        finally { transitioning = false; StartButton.IsEnabled = server is null; }
    });

    private async Task StopServer()
    {
        if (server is null || transitioning) return;
        transitioning = true; StopButton.IsEnabled = false;
        try
        {
            if (Browser.CoreWebView2 is not null) Browser.CoreWebView2.Navigate("about:blank");
            var previous = server; server = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await previous.StopAsync(timeout.Token); } finally { await previous.DisposeAsync(); }
            BrowserHint.Visibility = Visibility.Visible; StatusText.Text = "Сервер остановлен.";
        }
        finally { transitioning = false; StartButton.IsEnabled = true; UpdateStatus(); }
    }
    private async void Stop_Click(object sender, RoutedEventArgs e) => await Guard(StopServer);

    private async Task OpenRoom()
    {
        if (server is null) throw new InvalidOperationException("Сначала запустите сервер.");
        ShowPanel("Room");
        try
        {
            if (Browser.CoreWebView2 is null)
            {
                var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(dataDirectory, "webview"));
                await Browser.EnsureCoreWebView2Async(environment);
                Browser.CoreWebView2!.Settings.AreDevToolsEnabled = false;
                Browser.CoreWebView2.NavigationStarting += (_, args) =>
                {
                    if (args.Uri != "about:blank" && (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var target) || target.GetLeftPart(UriPartial.Authority) != LocalUrl)) args.Cancel = true;
                };
                Browser.CoreWebView2.NewWindowRequested += (_, args) => args.Handled = true;
                Browser.CoreWebView2.PermissionRequested += (_, args) => args.State = args.PermissionKind == CoreWebView2PermissionKind.Microphone ? CoreWebView2PermissionState.Default : CoreWebView2PermissionState.Deny;
            }
            BrowserHint.Visibility = Visibility.Collapsed;
            Browser.CoreWebView2.Navigate(LocalUrl + "/#host=" + hostSecret);
        }
        catch (WebView2RuntimeNotFoundException) { BrowserHint.Text = "Установите Microsoft Edge WebView2 Runtime и перезапустите приложение."; BrowserHint.Visibility = Visibility.Visible; throw; }
    }
    private async void OpenRoom_Click(object sender, RoutedEventArgs e) => await Guard(OpenRoom);
    private void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (server is null) { StatusText.Text = "Сначала запустите сервер."; return; }
        Clipboard.SetText(Uri.TryCreate(ConnectionLinks.SelectedText, UriKind.Absolute, out var selected) ? selected.ToString() : Links().First()); StatusText.Text = "Ссылка скопирована. Для VPN выделите нужный адрес в списке и скопируйте. Передайте пароль отдельно.";
    }

    private async Task RefreshLibrary() { var items = await library.ListAsync(); media.Clear(); foreach (var item in items) media.Add(item); MediaCount.Text = media.Count.ToString(); }
    private async Task Import(IEnumerable<string> files)
    {
        if (importing) return; importing = true;
        try
        {
            await using var processor = new MediaProcessor(settings, dataDirectory);
            var count = 0; List<string> failures = [];
            foreach (var path in files)
            {
                lifetime.Token.ThrowIfCancellationRequested(); ImportStatus.Text = "Проверяем: " + Path.GetFileName(path);
                try { await library.AddAsync(await processor.ProbeAsync(path, lifetime.Token)); count++; }
                catch (Exception ex) when (ex is IOException or ArgumentException or System.ComponentModel.Win32Exception or OperationCanceledException) { failures.Add(Path.GetFileName(path) + ": " + ex.Message); }
            }
            await RefreshLibrary(); ImportStatus.Text = $"Добавлено: {count}. Ошибок: {failures.Count}.";
            if (failures.Count > 0) MessageBox.Show(this, string.Join(Environment.NewLine, failures.Take(5)), "Не все файлы добавлены");
        }
        finally { importing = false; }
    }
    private async void AddFiles_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (importing) return;
        var dialog = new OpenFileDialog { Filter = "Видео|*.mp4;*.webm;*.mkv;*.avi;*.mov;*.m4v", Multiselect = true };
        if (dialog.ShowDialog(this) == true) { importTask = Import(dialog.FileNames); await importTask; }
    });
    private async void AddFolder_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (importing) return; var dialog = new OpenFolderDialog { Title = "Папка с видео" };
        if (dialog.ShowDialog(this) != true) return;
        var paths = await Task.Run(() => Directory.EnumerateFiles(dialog.FolderName, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Where(p => MediaProcessor.Extensions.Contains(Path.GetExtension(p).ToLowerInvariant())).ToArray(), lifetime.Token);
        importTask = Import(paths); await importTask;
    });
    private RoomCoordinator Coordinator => server?.Services.GetRequiredService<RoomCoordinator>() ?? throw new InvalidOperationException("Сначала запустите сервер.");
    private MediaItem SelectedMedia => LibraryGrid.SelectedItem as MediaItem ?? throw new InvalidOperationException("Выберите видео в списке.");
    private async void SelectMedia_Click(object sender, RoutedEventArgs e) => await Guard(async () => { await Coordinator.SelectAsync(SelectedMedia.Id); await OpenRoom(); });
    private async void QueueMedia_Click(object sender, RoutedEventArgs e) => await Guard(async () => { Coordinator.Room.Enqueue(SelectedMedia.Id); await Coordinator.BroadcastAsync(); StatusText.Text = "Видео добавлено в очередь."; });
    private async void RemoveMedia_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var selected = SelectedMedia;
        if (server?.Services.GetRequiredService<Room>().Snapshot().Playback.VideoId == selected.Id) throw new InvalidOperationException("Сначала переключите текущий фильм.");
        await library.RemoveAsync(selected.Id); await RefreshLibrary(); StatusText.Text = "Убрано из медиатеки. Файл на диске сохранён.";
    });
    private async void Grant_Click(object sender, RoutedEventArgs e) => await Guard(async () => { if (PeopleGrid.SelectedItem is Participant participant) await Coordinator.GrantAsync(participant.Id, (sender as Button)?.Tag as string == "allow"); });
    private async void Kick_Click(object sender, RoutedEventArgs e) => await Guard(async () => { if (PeopleGrid.SelectedItem is Participant participant) await Coordinator.KickAsync(participant.Id); });
    private void UpdateStatus()
    {
        SidebarStatus.Text = server is null ? "● Сервер остановлен" : "● Сервер работает";
        if (server is null) { PeopleCount.Text = "0"; PeopleGrid.ItemsSource = null; TrafficRate.Text = "0 Мбит/с"; return; }
        var coordinator = server.Services.GetRequiredService<RoomCoordinator>(); var people = coordinator.Room.Snapshot().Participants;
        var selected = (PeopleGrid.SelectedItem as Participant)?.Id;
        PeopleCount.Text = people.Count.ToString(); PeopleGrid.ItemsSource = people; PeopleGrid.SelectedItem = people.FirstOrDefault(p => p.Id == selected);
        var bytes = Interlocked.Read(ref coordinator.BytesSent); var elapsed = (DateTime.UtcNow - lastSample).TotalSeconds;
        Traffic.Text = $"{bytes / 1024d / 1024:F1} МБ";
        TrafficRate.Text = $"{(bytes - lastBytes) * 8 / Math.Max(.01, elapsed) / 1_000_000:F1} Мбит/с";
        lastBytes = bytes; lastSample = DateTime.UtcNow;
    }
    private async void Diagnostics_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        DiagnosticsText.Text = "Проверяем инструменты…"; List<string> lines = [];
        foreach (var (name, path) in new[] { ("FFmpeg", FfmpegInput.Text), ("FFprobe", FfprobeInput.Text) })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { var result = await MediaProcessor.RunAsync(path, ["-version"], timeout.Token); lines.Add("✓ " + result.Split('\n')[0]); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or System.ComponentModel.Win32Exception) { lines.Add("✕ " + name + ": не найден или не запускается"); }
        }
        if (server is not null)
        {
            try { using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) }; var response = await client.GetAsync(LocalUrl + "/api/health"); lines.Add(response.IsSuccessStatusCode ? "✓ Порт доступен на этом компьютере" : "✕ Сервер не отвечает"); }
            catch (HttpRequestException) { lines.Add("✕ Проверьте сертификат HTTPS и адрес сервера"); }
        }
        else lines.Add("Сервер остановлен. Проверка порта выполняется после запуска.");
        lines.Add("Проверку со стороны интернета выполните с другого устройства. При CGNAT используйте VPN.");
        DiagnosticsText.Text = string.Join(Environment.NewLine + Environment.NewLine, lines);
    });
    private void OpenLogs_Click(object sender, RoutedEventArgs e) { var path = Path.Combine(dataDirectory, "logs"); Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closed) return; e.Cancel = true; if (closing) return; closing = true;
        timer.Stop(); await lifetime.CancelAsync();
        await Guard(async () => { try { if (importTask is not null) await importTask; } catch (OperationCanceledException) { } finally { await StopServer(); } });
        Browser.Dispose(); lifetime.Dispose(); closed = true; Close();
    }
}
