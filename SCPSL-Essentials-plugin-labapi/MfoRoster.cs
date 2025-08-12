// Mfo/Roster/MfoRoster.cs
using System.Collections.Generic;
using LabApi.Features.Wrappers; // Player
using PlayerRoles;              // RoleTypeId

namespace ScpslEssentialsPlugin.Mfo.Roster
{
    /// <summary>
    /// Roster MFO:
    ///  - _members: kto nale¿y do danej formacji,
    ///  - _assigned: jaka rola zosta³a nadana temu graczowi w chwili "spawn".
    /// Dziêki _assigned mo¿na rozpoznaæ naturalne wycofanie, gdy nikt nie jest
    /// ju¿ ¿ywy w nadanej roli (np. wszyscy zginêli albo zmienili rolê).
    /// </summary>
    public static class MfoRoster
    {
        private static readonly Dictionary<string, HashSet<Player>> _members
            = new Dictionary<string, HashSet<Player>>();

        private static readonly Dictionary<string, Dictionary<Player, RoleTypeId>> _assigned
            = new Dictionary<string, Dictionary<Player, RoleTypeId>>();

        public static void Add(string formationId, Player pl)
        {
            if (string.IsNullOrWhiteSpace(formationId) || pl is null) return;

            if (!_members.TryGetValue(formationId, out var set))
            {
                set = new HashSet<Player>();
                _members[formationId] = set;
            }
            set.Add(pl);
        }

        public static void Remove(string formationId, Player pl)
        {
            if (string.IsNullOrWhiteSpace(formationId) || pl is null) return;

            if (_members.TryGetValue(formationId, out var set)) set.Remove(pl);
            if (_assigned.TryGetValue(formationId, out var map)) map.Remove(pl);
        }

        public static void Clear(string formationId)
        {
            if (string.IsNullOrWhiteSpace(formationId)) return;
            _members.Remove(formationId);
            _assigned.Remove(formationId);
        }

        /// <summary>Czy roster ma jakichœ cz³onków?</summary>
        public static bool HasAny(string formationId)
            => _members.TryGetValue(formationId, out var set) && set != null && set.Count > 0;

        /// <summary>Iteracja po obecnych cz³onkach formacji.</summary>
        public static IEnumerable<Player> GetPlayers(string formationId)
        {
            if (!_members.TryGetValue(formationId, out var set) || set == null) yield break;
            foreach (var p in set)
                if (p != null) yield return p;
        }

        /// <summary>Zapisz rolê nadan¹ graczowi w momencie wezwania.</summary>
        public static void NoteAssignedRole(string formationId, Player pl, RoleTypeId role)
        {
            if (string.IsNullOrWhiteSpace(formationId) || pl is null) return;

            if (!_assigned.TryGetValue(formationId, out var map))
            {
                map = new Dictionary<Player, RoleTypeId>();
                _assigned[formationId] = map;
            }
            map[pl] = role;
        }

        /// <summary>
        /// Czy ktokolwiek z tej formacji jest nadal ¿ywy w swojej
        /// PIERWOTNIE NADANEJ roli (NTF Captain/Sergeant/Private)?
        /// </summary>
        public static bool AnyAliveInAssignedRole(string formationId)
        {
            if (!_assigned.TryGetValue(formationId, out var map) || map.Count == 0)
                return false;

            foreach (var kv in map)
            {
                var p = kv.Key;
                var roleAtSpawn = kv.Value;

                // Je¿eli gracz ¿yje nadal w tej samej roli – formacja nie jest w 100% wycofana.
                if (p != null && p.Role == roleAtSpawn)
                    return true;
            }
            return false;
        }
    }
}
