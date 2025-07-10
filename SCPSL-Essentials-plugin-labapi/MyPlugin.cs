using System;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using LabApi.Loader.Features.Plugins;

namespace MyLabApiPlugin
{
    public class SkeletonPlugin : Plugin
    {
        public override string Name => "SkeletonPlugin";
        public override string Author => "ttk0721";
        public override string Description => "Przykładowy szkielet pluginu LabAPI";
        public override Version Version => new Version(1, 0, 0);
        public override Version RequiredApiVersion => new Version(1, 0, 0);
        public bool Enabled { get; set; } = true;

        public override void Enable()
        {
            PlayerEvents.Joined += OnPlayerJoined;
            Logger.Info("Plugin LabAPI aktywowany!");
        }

        public override void Disable()
        {
            PlayerEvents.Joined -= OnPlayerJoined;
            Logger.Info("Plugin LabAPI wyłączony.");
        }

        private static void OnPlayerJoined(PlayerJoinedEventArgs ev)
        {
            Logger.Info($"Gracz {ev.Player.DisplayName} dołączył do serwera.");
            ev.Player.SendBroadcast("Witaj na serwerze!", 5);
        }
    }
}
