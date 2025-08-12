// Internal/MainThread.cs
using System;
using System.Collections.Concurrent;
using System.Collections;
using UnityEngine;
using ConsoleLogger = LabApi.Features.Console.Logger;

namespace ScpslEssentialsPlugin.Internal
{
    /// <summary>
    /// Wykonuje akcje na w¹tku g³ównym Unity (bezpiecznie dla ról/Cassie/broadcastów).
    /// </summary>
    public sealed class MainThread : MonoBehaviour
    {
        private static MainThread _inst;
        private static readonly ConcurrentQueue<Action> _q = new();

        public static void Init()
        {
            if (_inst != null) return;
            var go = new GameObject("SCPSL-Essentials-MainThread");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            _inst = go.AddComponent<MainThread>();
        }

        public static void Run(Action a)
        {
            if (a == null) return;
            _q.Enqueue(a);
        }

        public static void Delay(float seconds, Action a)
        {
            if (a == null) return;
            if (_inst == null) Init();
            _inst.StartCoroutine(_inst.CoDelay(seconds, a));
        }

        private IEnumerator CoDelay(float seconds, Action a)
        {
            if (seconds > 0f) yield return new WaitForSeconds(seconds);
            try { a?.Invoke(); }
            catch (Exception ex) { ConsoleLogger.Warn($"[MainThread] delayed error: {ex.Message}"); }
        }

        private void Update()
        {
            while (_q.TryDequeue(out var a))
            {
                try { a(); }
                catch (Exception ex) { ConsoleLogger.Warn($"[MainThread] action error: {ex.Message}"); }
            }
        }
    }
}
