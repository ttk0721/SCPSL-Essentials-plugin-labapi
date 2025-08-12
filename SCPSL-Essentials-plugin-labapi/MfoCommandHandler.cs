// Mfo/MfoCommandHandler.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LabApi.Features.Wrappers;     // Player, Cassie
using PlayerRoles;                  // RoleTypeId
using ConsoleLogger = LabApi.Features.Console.Logger;
using ScpslEssentialsPlugin.Internal;
using ScpslEssentialsPlugin.Mfo.Roster;
using ScpslEssentialsPlugin.Mfo;

namespace ScpslEssentialsPlugin
{
    public sealed class MfoCommandHandler
    {
        private readonly ScpslEssentialsPlugin _plugin;

        // none|incoming|active|retreating
        private readonly Dictionary<int, string> _status = new();

        public MfoCommandHandler(ScpslEssentialsPlugin plugin) => _plugin = plugin;

        private static (int max, int cap, int sgt) LimitsFor(int mfoId) => mfoId switch
        {
            1 => (6, 1, 2),  // Alpha-1
            7 => (8, 1, 2),  // Nu-7
            11 => (4, 1, 2),  // Omega-1
            _ => (99, 1, 2),
        };

        // ======= PUBLIC API ===================================================

        public async Task DispatchMfo(int mfoId, bool immediate, string formationId)
        {
            if (_status.TryGetValue(mfoId, out var st) && (st == "incoming" || st == "active"))
            {
                ConsoleLogger.Info($"[MFO] {mfoId} już w drodze/aktywne.");
                return;
            }

            if (!TryCollectMembersForSpawn(formationId, mfoId, out var selected, out var blockReason))
            {
                ConsoleLogger.Info($"[MFO] Blokada wezwania '{formationId}': {blockReason}");
                return;
            }

            _status[mfoId] = "incoming";

            if (!immediate)
            {
                MainThread.Run(() => { MfoCassie.SayDeployed(mfoId); });
                MainThread.Run(() => BroadcastToFormation(formationId, $"{MfoCassie.GetPlName(mfoId)} – w drodze.", 5));
                await Task.Delay(TimeSpan.FromSeconds(15));
            }

            // Spawn + wpisanie do rosteru i ustawienie statusu – wszystko na głównym wątku
            MainThread.Run(() =>
            {
                try
                {
                    SpawnSelected(selected, mfoId, formationId); // <-- teraz z formationId
                    _status[mfoId] = "active";
                    MfoCassie.SayEntered(mfoId); // GLOBAL
                    BroadcastToFormation(formationId, $"{MfoCassie.GetPlName(mfoId)} wkroczyła do placówki.", 6);
                }
                catch (Exception ex)
                {
                    ConsoleLogger.Error($"[MFO] Spawn crash-guard: {ex.Message}");
                    _status.Remove(mfoId);
                }
            });
        }

        public async Task RetreatMfo(int mfoId, bool immediate, string formationId)
        {
            // Natychmiastowe wycofanie – nie blokujemy na statusie, zrób to zawsze
            if (immediate)
            {
                MfoCassie.SayRetreat(mfoId, urgent: true);                 // CASSIE globalnie
                BroadcastToFormation(formationId, "[PILNE] Natychmiastowa ewakuacja!", 6); // tylko do tej formacji

                var members = MfoRoster.GetPlayers(formationId)
                                       .Where(p => p != null)
                                       .ToList();

                MainThread.Run(() =>
                {
                    foreach (var p in members)
                    {
                        try { TrySetRoleSpectator(p); }
                        catch (Exception ex) { ConsoleLogger.Warn($"[MFO] Spectator (instant) error: {ex.Message}"); }
                    }
                });

                MfoRoster.Clear(formationId);
                _status.Remove(mfoId);
                return;
            }

            // Standardowe wycofanie – tu już wymagamy, by było aktywne i nie „incoming”
            if (!_status.TryGetValue(mfoId, out var st) || st is "incoming")
            {
                ConsoleLogger.Info($"[MFO] MFO {mfoId} nie jest aktywne.");
                return;
            }

            _status[mfoId] = "retreating";

            MfoCassie.SayRetreat(mfoId, urgent: false); // globalnie
            BroadcastToFormation(formationId, "[ROZKAZ] Wycofać się do Escape_primary.", 6);

            await Task.Delay(TimeSpan.FromSeconds(60));
            MfoCassie.SayRetreat(mfoId, urgent: true);
            BroadcastToFormation(formationId, "[PILNE] Natychmiast wycofać się!", 6);
            await Task.Delay(TimeSpan.FromSeconds(60));

            // Sankcja/zmiana ról na głównym wątku
            var after = MfoRoster.GetPlayers(formationId).Where(p => p != null).ToList();
            MainThread.Run(() =>
            {
                foreach (var p in after)
                {
                    if (!IsInEscapePrimary(p))
                    {
                        p.SendBroadcast("NIE OPUSZCZONO OBIEKTU – sankcja (trucizna).", 6);
                        ConsoleLogger.Info($"[MFO] Sankcja (poison) na: {p.Nickname}.");
                    }
                    else
                    {
                        TrySetRoleSpectator(p);
                    }
                }
            });

            _status.Remove(mfoId);
        }


        // ======= IMPLEMENTACJA ===============================================

        /// <summary>Dobiera członków: najpierw roster, jeśli pusty – spectatorzy. Dba o limity.</summary>
        private bool TryCollectMembersForSpawn(string formationId, int mfoId, out List<Player> selected, out string reason)
        {
            selected = new List<Player>();
            reason = "";

            var (max, _, _) = LimitsFor(mfoId);

            // 1) roster
            selected = MfoRoster.GetPlayers(formationId)
                                .Where(p => p != null)
                                .Distinct()
                                .Take(max)
                                .ToList();

            // 2) fallback: spectatorzy (dla testów)
            if (selected.Count == 0)
            {
                try
                {
                    selected = Player.GetAll()
                                     .Where(p => p != null && p.Role == RoleTypeId.Spectator)
                                     .Take(max)
                                     .ToList();
                }
                catch
                {
                    // jeśli Twoje API nie ma GetAll, usuń fallback i polegaj wyłącznie na rosterze
                }
            }

            if (selected.Count == 0)
            {
                reason = "formacja jest pusta (brak w rosterze i brak spectatorów)";
                return false;
            }

            return true;
        }

        /// <summary>Właściwy spawn – role + ewentualny teleport (tu zbędny, RoleSpawnFlags już respawnuje).</summary>
        private void SpawnSelected(List<Player> players, int mfoId, string formationId)
        {
            var (_, capMax, sgtMax) = LimitsFor(mfoId);
            int cap = 0, sgt = 0;

            // Zastępujemy roster tej formacji dokładnie wybraną grupą
            MfoRoster.Clear(formationId);

            foreach (var p in players)
            {
                try
                {
                    if (cap < capMax) { p.Role = RoleTypeId.NtfCaptain; cap++; }
                    else if (sgt < sgtMax) { p.Role = RoleTypeId.NtfSergeant; sgt++; }
                    else { p.Role = RoleTypeId.NtfPrivate; }

                    // osobisty broadcast
                    p.SendBroadcast($"Przydzielono do {MfoCassie.GetPlName(mfoId)}.", 5);

                    // WAŻNE: dodaj do rosteru tej formacji – retreat później kogoś „widzi”
                    MfoRoster.Add(formationId, p);
                }
                catch (Exception ex)
                {
                    ConsoleLogger.Warn($"[MFO] Spawn error for {p?.Nickname}: {ex.Message}");
                }
            }

            ConsoleLogger.Info($"[MFO] Przydzielono {players.Count} osób do {MfoCassie.GetPlName(mfoId)}.");
        }

        private void ApplyRetreatConsequences(string formationId)
        {
            foreach (var p in MfoRoster.GetPlayers(formationId))
            {
                if (p == null) continue;

                if (!IsInEscapePrimary(p))
                {
                    p.SendBroadcast("NIE OPUSZCZONO OBIEKTU – sankcja (trucizna).", (ushort)6);
                    // TODO: faktyczna kara/efekt DOT – w zależności od Twojego API
                    ConsoleLogger.Info($"[MFO] Sankcja (poison) na: {p.Nickname}.");
                }
                else
                {
                    TrySetRoleSpectator(p);
                }
            }
        }

        private static void TrySetRoleSpectator(Player p)
        {
            try
            {
                p.Role = RoleTypeId.Spectator;
                ConsoleLogger.Info($"[MFO] {p.Nickname} -> Spectator");
            }
            catch (Exception ex)
            {
                ConsoleLogger.Warn($"[MFO] Spectator role error: {ex.Message}");
            }
        }

        private static void BroadcastToFormation(string formationId, string text, int seconds)
        {
            var sec = (ushort)Math.Clamp(seconds, 1, 60);
            foreach (var p in MfoRoster.GetPlayers(formationId))
                p?.SendBroadcast(text, sec);
        }

        private static bool IsInEscapePrimary(Player p)
        {
            // Room.Name to enum – zamieniamy na string ostrożnie.
            var name = p?.Room?.Name.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(name)) return false;

            return name.Equals("Escape_primary", StringComparison.OrdinalIgnoreCase)
                || name.Equals("ESCAPE_PRIMARY", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Escape Primary", StringComparison.OrdinalIgnoreCase);
        }
    }
}
