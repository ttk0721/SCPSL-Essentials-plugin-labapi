using LabApi.Loader;
using LabApi.Loader.Features.Plugins;
using LabApi.Common.API;
using System;

namespace MyLabApiPlugin
{
    public class MyPlugin : Plugin
    {
        public override string Name => "MyPlugin";
        public override string Author => "ttk0721";
        public override string Description => "Przykładowy plugin LabAPI";
        public override Version Version => new Version(1, 0, 0);
        public override Version RequiredApiVersion => new Version(1, 0, 0);
        public bool Enabled { get; set; } = true;

        public override void Enable()
        {
            Log.Info("Plugin LabAPI aktywowany!");
        }

        public override void Disable()
        {
            Log.Info("Plugin LabAPI wyłączony.");
        }
    }
}
