using System;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using LabApi.Loader.Features.Plugins;
using System.Reflection;
using LabApi.Features.Wrappers;
using ScpslEssentialsPlugin;
using ScpslEssentialsPlugin.Internal;
using ScpslEssentialsPlugin.Mfo;

namespace ScpslEssentialsPlugin
{

    /// <summary>
    /// Wersja wtyczki komunikująca się z panelem za pomocą WebSocket.
    /// Zamiast lokalnego serwera HTTP utrzymuje jedno połączenie wychodzące,
    /// dzięki czemu działa również za NAT/CGNAT i reaguje w czasie rzeczywistym.
    /// </summary>
    public class ScpslEssentialsPlugin : Plugin
    {
        public override string Name => "SCPSL-Essentials-plugin";
        public override string Author => "ttk0721";
        public override string Description => "Przykładowy szkielet pluginu LabAPI z komunikacją WebSocket";
        public override Version Version => new Version(1, 1, 0);
        public override Version RequiredApiVersion => new Version(1, 0, 0);

        /// <summary>
        /// Flaga włączająca/wyłączająca działanie wtyczki.
        /// Używana do kontrolowania pętli reconnect.
        /// </summary>
        public bool Enabled { get; set; } = true;

        private ClientWebSocket? _webSocket;
        private CancellationTokenSource? _wsCts;
        private readonly HttpClient _client = new HttpClient();
        private Config _config = new Config();

        // Handler for Mobile Forces commands (dispatch/retreat)
        private MfoCommandHandler? _mfoHandler;

        public override void Enable()
        {
            MainThread.Init();

            // 1) spróbuj znaleźć folder wtyczki
            string pluginDir = AppDomain.CurrentDomain.BaseDirectory;
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var loc = asm.Location;
                if (!string.IsNullOrWhiteSpace(loc))
                    pluginDir = Path.GetDirectoryName(loc) ?? pluginDir;
                else if (asm.CodeBase != null)
                    pluginDir = Path.GetDirectoryName(new Uri(asm.CodeBase).LocalPath) ?? pluginDir;
            }
            catch { /* fallback zostaje */ }

            // 2) rozwiąż faktyczną ścieżkę configu wg kolejności/candydatów
            var cfgPath = ResolveConfigPath(pluginDir);
            Logger.Info($"[Essentials] Czytam config: {cfgPath}");
            _config = Config.Load(cfgPath);

            // walidacja adresu WS
            if (string.IsNullOrWhiteSpace(_config.PanelUrl) ||
                (!(_config.PanelUrl.StartsWith("ws://") || _config.PanelUrl.StartsWith("wss://"))))
            {
                Logger.Warn($"panelUrl musi zaczynać się od ws:// lub wss://. Obecnie: '{_config.PanelUrl}'");
            }

            PlayerEvents.Joined += OnPlayerJoined;
            if (!string.IsNullOrEmpty(_config.PanelUrl))
                _ = ConnectAsync();

            _mfoHandler = new MfoCommandHandler(this);
            Logger.Info("Plugin LabAPI aktywowany!");
        }

        private static string ResolveConfigPath(string pluginDir)
        {
            // 0) twarde nadpisanie przez zmienną środowiskową (najwygodniejsze na hostingu)
            var env = Environment.GetEnvironmentVariable("SCPSL_ESS_CONFIG")
                   ?? Environment.GetEnvironmentVariable("SCPSL_ESSENTIALS_CONFIG");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
                return env;

            // 1) obok DLL-a (gdy Location działa)
            var p1 = SafeCombine(pluginDir, "config.yml");
            if (File.Exists(p1)) return p1;

            // 2) obok pliku wykonywalnego serwera
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var p2 = SafeCombine(baseDir, "config.yml");
            if (File.Exists(p2)) return p2;

            // 3) ./plugins/scpsl-essentials/config.yml (sensowne miejsce domyślne w katalogu serwera)
            var p3 = SafeCombine(baseDir, "plugins", "scpsl-essentials", "config.yml");
            if (File.Exists(p3)) return p3;

            // 4) ./config/config.yml (czasem tak się organizuje pliki)
            var p4 = SafeCombine(baseDir, "config", "config.yml");
            if (File.Exists(p4)) return p4;

            // 5) ~/.scpsl-essentials/config.yml (ostatnia deska ratunku na linuxie)
            try
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(home))
                {
                    var p5 = SafeCombine(home, ".scpsl-essentials", "config.yml");
                    if (File.Exists(p5)) return p5;
                }
            }
            catch { /* ignoruj */ }

            // 6) NIE znaleziono — utwórz domyślny w ./plugins/scpsl-essentials/config.yml
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(p3)!);
                if (!File.Exists(p3))
                    File.WriteAllText(p3, DefaultConfigTemplate());
            }
            catch { /* jeśli nie wyjdzie — trudno, zwrócimy p3 i Load() da puste wartości */ }

            return p3;
        }

        private static string SafeCombine(params string[] parts)
        {
            try { return Path.Combine(parts); } catch { return string.Join("/", parts); }
        }

        private static string DefaultConfigTemplate() =>
        @"# SCPSL Essentials plugin (config.yml)
        # UZUPEŁNIJ te pola i zrestartuj serwer.
        #Adres websocketu huba/panelu:
        panelUrl: ws://127.0.0.1:8080/ws
        # Id serwera w panelu:
        serverId: Community_ban
        # Token API z panelu:
        apiToken: PASTE_YOUR_TOKEN_HERE
        # Nieużywane w trybie WS, zostaw dla kompatybilności:
        listenPort: 7878
        ";


        public override void Disable()
        {
            Enabled = false;
            PlayerEvents.Joined -= OnPlayerJoined;
            // zatrzymujemy pętlę WebSocket
            _ = DisconnectAsync();
            Logger.Info("Plugin LabAPI wyłączony.");
        }

        private void OnPlayerJoined(PlayerJoinedEventArgs ev)
        {
            Logger.Info($"Gracz {ev.Player.DisplayName} dołączył do serwera.");
            ev.Player.SendBroadcast("Witaj na serwerze!", 5);
            // wysyłamy zdarzenie do panelu
            _ = SendEventAsync(new
            {
                type = "player_joined",
                payload = new { id = ev.Player.UserId, name = ev.Player.DisplayName }
            });
        }

        /// <summary>
        /// Łączy się z panelem po WebSocket i uruchamia pętle odbioru/heartbeat.
        /// W razie rozłączenia próbuje ponownie co 5 sekund.
        /// </summary>
        private async Task ConnectAsync()
        {
            while (Enabled)
            {
                try
                {
                    var wsUri = new Uri(_config.PanelUrl);
                    _webSocket = new ClientWebSocket();
                    _wsCts = new CancellationTokenSource();
                    await _webSocket.ConnectAsync(wsUri, CancellationToken.None);
                    Logger.Info($"Połączono z panelem {_config.PanelUrl}");
                    // handshake
                    await SendJsonAsync(new
                    {
                        type = "handshake",
                        server_id = _config.ServerId,
                        token = _config.ApiToken,
                        version = Version.ToString()
                    });
                    // start loops
                    _ = Task.Run(() => ReceiveLoopAsync(_wsCts.Token));
                    _ = Task.Run(() => HeartbeatLoopAsync(_wsCts.Token));
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Nie udało się połączyć z panelem: {ex.Message}. Ponawiam za 5s.");
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
            }
        }

        /// <summary>
        /// Rozłącza istniejące połączenie.
        /// </summary>
        private async Task DisconnectAsync()
        {
            try
            {
                if (_webSocket != null && _webSocket.State == WebSocketState.Open)
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Plugin disabled", CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Błąd podczas rozłączania: {ex.Message}");
            }
            finally
            {
                try
                {
                    _wsCts?.Cancel();
                }
                catch { /* ignorujemy */ }
            }
        }

        /// <summary>
        /// Pętla odbioru wiadomości z WebSocket.
        /// Po rozłączeniu próbuje ponownie nawiązać połączenie.
        /// </summary>
        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            if (_webSocket == null)
                return;

            var buffer = new ArraySegment<byte>(new byte[4096]);
            while (!token.IsCancellationRequested && _webSocket.State == WebSocketState.Open)
            {
                try
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _webSocket.ReceiveAsync(buffer, token);
                        ms.Write(buffer.Array!, buffer.Offset, result.Count);
                    } while (!result.EndOfMessage);

                    ms.Seek(0, SeekOrigin.Begin);
                    var message = Encoding.UTF8.GetString(ms.ToArray());
                    ProcessWebSocketMessage(message);
                }
                catch (OperationCanceledException)
                {
                    // anulowano – wychodzimy
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Błąd odbierania z WebSocket: {ex.Message}. Zrywam połączenie.");
                    break;
                }
            }
            // jeśli tu dotarliśmy, połączenie zostało zerwane lub anulowane
            await DisconnectAsync();
            if (Enabled)
            {
                // spróbujemy ponownie
                await ConnectAsync();
            }
        }

        /// <summary>
        /// Wysyła heartbeat ping co 30 sekund.
        /// </summary>
        private async Task HeartbeatLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), token);
                    await SendJsonAsync(new { type = "ping" });
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Błąd wysyłania pingu: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Obsługuje przychodzącą wiadomość WebSocket.
        /// </summary>
        /// <param name="message">Pełny JSON otrzymany od panelu</param>
        private void ProcessWebSocketMessage(string message)
        {
            try
            {
                var root = JObject.Parse(message);
                var type = (string?)root["type"] ?? string.Empty;

                switch (type)
                {
                    case "command":
                        {
                            var commandId = (string?)root["command_id"];
                            var action = (string?)root["action"];
                            var data = root["data"] ?? new JObject();
                            HandleCommand(commandId, action, data);
                            break;
                        }
                    case "pong":
                        // heartbeat response; nothing further
                        break;
                    default:
                        Logger.Warn($"Nieznany typ wiadomości: {type}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Błąd przetwarzania wiadomości: {ex.Message}");
            }
        }

        /// <summary>
        /// Wykonuje komendę otrzymaną od panelu.
        /// Na koniec odsyła ACK, żeby panel wiedział, że komenda została wykonana.
        /// </summary>
        /// <param name="commandId">Identyfikator komendy</param>
        /// <param name="action">Rodzaj akcji</param>
        /// <param name="data">Dane komendy</param>
        private void HandleCommand(string? commandId, string? action, JToken data)
        {
            var status = "unknown";
            try
            {
                if (string.IsNullOrEmpty(action))
                    throw new InvalidOperationException("Brak akcji w komendzie.");

                switch (action)
                {
                    case "mfo.dispatch":
                        {
                            var mfoId = data.Value<int?>("mfo_id");
                            var immediate = data.Value<bool?>("immediate") ?? false;
                            var formationId = data.Value<string>("formation_id");
                            if (string.IsNullOrWhiteSpace(formationId))
                                formationId = MfoCassie.GetFormationId(mfoId ?? 0); // mapowanie 1->"Alpha-1", 4->"Eta-10", itd.

                            if (mfoId.HasValue && _mfoHandler != null)
                            {
                                _ = _mfoHandler.DispatchMfo(mfoId.Value, immediate, formationId);
                                status = "accepted";
                            }
                            else status = "failed";
                            break;
                        }
                    case "mfo.retreat":
                        {
                            var mfoId = data.Value<int?>("mfo_id");
                            var immediate = data.Value<bool?>("immediate") ?? false;
                            var formationId = data.Value<string>("formation_id");
                            if (string.IsNullOrWhiteSpace(formationId))
                                formationId = MfoCassie.GetFormationId(mfoId ?? 0);

                            if (mfoId.HasValue && _mfoHandler != null)
                            {
                                _ = _mfoHandler.RetreatMfo(mfoId.Value, immediate, formationId);
                                status = "accepted";
                            }
                            else status = "failed";
                            break;
                        }

                    default:
                        {
                            Logger.Warn($"Otrzymano nieznaną akcję: {action}");
                            status = "unsupported";
                            break;
                        }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Błąd podczas wykonywania komendy {action}: {ex.Message}");
                status = "error";
            }
            finally
            {
                // Odsyłamy ACK z wynikiem
                if (commandId != null)
                {
                    _ = SendJsonAsync(new
                    {
                        type = "ack",
                        command_id = commandId,
                        status
                    });
                }
            }
        }

        /// <summary>
        /// Wysyła dowolny obiekt jako JSON po WebSocket.
        /// </summary>
        private async Task SendJsonAsync(object obj)
        {
            try
            {
                if (_webSocket == null || _webSocket.State != WebSocketState.Open)
                    return;
                var json = JsonConvert.SerializeObject(obj);
                var bytes = Encoding.UTF8.GetBytes(json);
                var segment = new ArraySegment<byte>(bytes);
                await _webSocket.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Błąd wysyłania wiadomości po WebSocket: {ex.Message}");
            }
        }

        /// <summary>
        /// Wysyła zdarzenie (np. dołączenie gracza) do panelu.
        /// Jeśli WebSocket jest otwarty, robi to po nim; inaczej próbuje HTTP POST.
        /// </summary>
        private async Task SendEventAsync(object data)
        {
            // jeśli mamy otwarty WebSocket, używamy go
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                await SendJsonAsync(data);
                return;
            }

            // w ostateczności spróbuj HTTP POST (fallback)
            if (string.IsNullOrEmpty(_config.PanelUrl))
                return;

            try
            {
                // Convert ws(s) URL to http(s) and strip trailing /ws
                var httpBase = _config.PanelUrl
                    .Replace("wss://", "https://")
                    .Replace("ws://", "http://")
                    .TrimEnd('/');
                if (httpBase.EndsWith("/ws", StringComparison.OrdinalIgnoreCase))
                {
                    httpBase = httpBase.Substring(0, httpBase.Length - 3);
                }
                var url = $"{httpBase}/api/plugin/events";
                var json = JsonConvert.SerializeObject(data);

                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                request.Headers.Add("X-Server-ID", _config.ServerId);
                request.Headers.Add("X-Server-Token", _config.ApiToken);

                await _client.SendAsync(request);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Nie udało się wysłać zdarzenia (HTTP fallback): {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Klasa konfiguracji wtyczki.
    /// Czyta plik YAML config.yml i przechowuje ustawienia.
    /// </summary>
    public class Config
    {
        /// <summary>
        /// Adres endpointu WebSocket panelu (np. wss://panel.example.com/ws).
        /// </summary>
        public string PanelUrl { get; set; } = string.Empty;

        /// <summary>
        /// Id serwera wysyłany w handshake i nagłówkach.
        /// </summary>
        public string ServerId { get; set; } = string.Empty;

        /// <summary>
        /// Token API dla autoryzacji połączenia.
        /// </summary>
        public string ApiToken { get; set; } = string.Empty;

        /// <summary>
        /// Port nasłuchu – obecnie nieużywany w wersji z WebSocket,
        /// pozostawiony dla wstecznej kompatybilności.
        /// </summary>
        public int ListenPort { get; set; } = 7878;

        /// <summary>
        /// Ładuje konfigurację z pliku YAML. Format:
        /// panelUrl: wss://...
        /// serverId: <GUID>
        /// apiToken: <TOKEN>
        /// listenPort: 7878
        /// </summary>
        public static Config Load(string path)
        {
            var cfg = new Config();

            if (!File.Exists(path))
                return cfg;

            foreach (var line in File.ReadAllLines(path))
            {
                var parts = line.Split(':', 2);
                if (parts.Length != 2)
                    continue;

                var key = parts[0].Trim();
                var value = parts[1].Trim().Trim('"');

                switch (key)
                {
                    case "panelUrl":
                        cfg.PanelUrl = value;
                        break;
                    case "serverId":
                        cfg.ServerId = value;
                        break;
                    case "apiToken":
                        cfg.ApiToken = value;
                        break;
                    case "listenPort":
                        if (int.TryParse(value, out var port))
                            cfg.ListenPort = port;
                        break;
                }
            }

            return cfg;
        }
    }
}