using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using LabApi.Loader.Features.Plugins;

namespace ScpslEssentialsPlugin
{
    public class ScpslEssentialsPlugin : Plugin
    {
        public override string Name => "SCPSL-Essentials-plugin";
        public override string Author => "ttk0721";
        public override string Description => "Przykładowy szkielet pluginu LabAPI z komunikacją";
        public override Version Version => new Version(1, 0, 0);
        public override Version RequiredApiVersion => new Version(1, 0, 0);
        public bool Enabled { get; set; } = true;

        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private HttpClient _client = new HttpClient();
        private Config _config = new Config();

        public override void Enable()
        {
            _config = Config.Load("config.yml");
            PlayerEvents.Joined += OnPlayerJoined;
            StartServer();
            Logger.Info("Plugin LabAPI aktywowany!");
        }

        public override void Disable()
        {
            PlayerEvents.Joined -= OnPlayerJoined;
            StopServer();
            Logger.Info("Plugin LabAPI wyłączony.");
        }

        private void OnPlayerJoined(PlayerJoinedEventArgs ev)
        {
            Logger.Info($"Gracz {ev.Player.DisplayName} dołączył do serwera.");
            ev.Player.SendBroadcast("Witaj na serwerze!", 5);
            _ = SendEventAsync(new
            {
                type = "player_joined",
                payload = new { id = ev.Player.UserId, name = ev.Player.DisplayName }
            });
        }

        private void StartServer()
        {
            if (string.IsNullOrEmpty(_config.PanelUrl))
            {
                Logger.Warning("Brak konfiguracji panelu - pomijam uruchomienie serwera.");
                return;
            }

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://*:{_config.ListenPort}/");
            _listener.Start();
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => HandleRequests(_cts.Token));
            Logger.Info($"HTTP listener uruchomiony na porcie {_config.ListenPort}");
        }

        private void StopServer()
        {
            try
            {
                _cts?.Cancel();
                _listener?.Stop();
            }
            catch (Exception ex)
            {
                Logger.Error($"Błąd zatrzymywania serwera: {ex.Message}");
            }
        }

        private async Task HandleRequests(CancellationToken token)
        {
            if (_listener == null)
                return;

            while (!token.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (HttpListenerException)
                {
                    break;
                }

                _ = Task.Run(() => ProcessRequestAsync(ctx));
            }
        }

        private async Task ProcessRequestAsync(HttpListenerContext ctx)
        {
            try
            {
                if (!ValidateToken(ctx))
                {
                    ctx.Response.StatusCode = 401;
                    ctx.Response.Close();
                    return;
                }

                switch (ctx.Request.Url?.AbsolutePath)
                {
                    case "/api/mfo/send":
                        Logger.Info("Przyjęto polecenie wysłania MFO");
                        break;
                    default:
                        ctx.Response.StatusCode = 404;
                        ctx.Response.Close();
                        return;
                }

                ctx.Response.StatusCode = 200;
            }
            catch (Exception ex)
            {
                Logger.Error($"Błąd podczas obsługi żądania: {ex.Message}");
                ctx.Response.StatusCode = 500;
            }
            finally
            {
                ctx.Response.Close();
            }
        }

        private bool ValidateToken(HttpListenerContext ctx)
        {
            return ctx.Request.Headers["X-Server-Token"] == _config.ApiToken;
        }

        private async Task SendEventAsync(object data)
        {
            if (string.IsNullOrEmpty(_config.PanelUrl))
                return;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.PanelUrl}/api/plugin/events")
                {
                    Content = JsonContent.Create(data)
                };
                request.Headers.Add("X-Server-ID", _config.ServerId);
                request.Headers.Add("X-Server-Token", _config.ApiToken);

                await _client.SendAsync(request);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Nie udało się wysłać zdarzenia: {ex.Message}");
            }
        }
    }

    public class Config
    {
        public string PanelUrl { get; set; } = string.Empty;
        public string ServerId { get; set; } = string.Empty;
        public string ApiToken { get; set; } = string.Empty;
        public int ListenPort { get; set; } = 7878;

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
