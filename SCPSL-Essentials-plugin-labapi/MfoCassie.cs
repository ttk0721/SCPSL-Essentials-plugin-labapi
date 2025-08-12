// Mfo/MfoCassie.cs
using LabApi.Features.Wrappers;          // Cassie
using ConsoleLogger = LabApi.Features.Console.Logger;

namespace ScpslEssentialsPlugin
{
    /// <summary>
    /// Pomocnik do komunikatów Cassie + mapowania MFO.
    /// </summary>
    public static class MfoCassie
    {
        /// <summary>
        /// Zwraca identyfikator formacji u¿ywany w rosterze (np. "Eta-10", "Alpha-1"...).
        /// </summary>
        public static string GetFormationId(int mfoId) => mfoId switch
        {
            1 => "Alpha-1",
            4 => "Eta-10",
            7 => "Nu-7",
            11 => "Omega-1",
            _ => $"MFO-{mfoId}"
        };

        /// <summary>
        /// £adna nazwa do wyœwietlania (PL) – np. „MFO Eta-10”.
        /// </summary>
        public static string GetPlName(int mfoId) => mfoId switch
        {
            1 => "MFO Alpha-1",
            4 => "MFO Eta-10",
            7 => "MFO Nu-7",
            11 => "MFO Omega-1",
            _ => $"MFO {mfoId}"
        };

        // Uwaga: Cassie rozumie ograniczony zestaw s³ów/angielski.
        // Poni¿sze s¹ bezpieczne – krótkie, proste, bez polskich znaków.

        public static void SayDeployed(int mfoId)
        {
            // Przyk³ad neutralny: "MOBILE TASK FORCE ETA 10 DEPLOYED"
            var code = mfoId switch
            {
                1 => "ALPHA 1",
                4 => "ETA 10",
                7 => "NU 7",
                11 => "OMEGA 1",
                _ => $"UNIT {mfoId}"
            };

            TryCassie($"MOBILE TASK FORCE {code} DEPLOYED");
        }

        public static void SayEntered(int mfoId)
        {
            var code = mfoId switch
            {
                1 => "ALPHA 1",
                4 => "ETA 10",
                7 => "NU 7",
                11 => "OMEGA 1",
                _ => $"UNIT {mfoId}"
            };

            TryCassie($"MOBILE TASK FORCE {code} ENTERED FACILITY");
        }

        public static void SayRetreat(int mfoId, bool urgent)
        {
            var code = mfoId switch
            {
                1 => "ALPHA 1",
                4 => "ETA 10",
                7 => "NU 7",
                11 => "OMEGA 1",
                _ => $"UNIT {mfoId}"
            };

            if (urgent)
                TryCassie($"MOBILE TASK FORCE {code} IMMEDIATE EVACUATION");
            else
                TryCassie($"MOBILE TASK FORCE {code} PROCEED TO EVACUATION");
        }

        private static void TryCassie(string text)
        {
            try { Cassie.Message(text); }
            catch (System.Exception ex)
            {
                ConsoleLogger.Warn($"[Cassie] error: {ex.Message}");
            }
        }
    }
}
