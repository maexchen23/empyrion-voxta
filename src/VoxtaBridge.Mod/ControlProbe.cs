using System;
using System.Collections.Generic;
using System.Linq;
using Eleon.Modding;

namespace VoxtaBridge
{
    /// <summary>
    /// Control probe (part of the combat probe, CombatProbe=true; read-only). Playfield server, every snapshot, per player:
    /// - what the game reports about the seat the player is in (CurrentStructure, IsPilot, DrivingEntity, the structure's
    ///   Pilot and passengers), logged when it changes - to find out whether a manned turret is visible to a server mod;
    /// - the signals of the player's current structure (control panel + block signals with state and sender block), the
    ///   custom device names, logged when they change;
    /// - IStructure.SignalChanged of every structure a player was in (name, new state, triggering entity).
    /// </summary>
    public partial class BridgeMod
    {
        static readonly Dictionary<int, string> probeSeat = new Dictionary<int, string>();
        static readonly Dictionary<int, string> probeSignals = new Dictionary<int, string>();
        static readonly HashSet<int> probeSignalHooked = new HashSet<int>();

        static void ProbeControl(IPlayfield pf)
        {
            if (!ProbeOn) return;
            foreach (var p in pf.Players.Values.ToList())
            {
                IStructure s = null;
                string seat;
                try
                {
                    s = p.CurrentStructure;
                    var drv = p.DrivingEntity;
                    string pilot = "-", pass = "-";
                    try { pilot = s?.Pilot == null ? "none" : $"player#{s.Pilot.Id}"; } catch (Exception e) { pilot = "(" + e.GetType().Name + ")"; }
                    try { pass = s == null ? "-" : string.Join(",", s.GetPassengers()?.Select(x => "player#" + x.Id) ?? new string[0]); } catch (Exception e) { pass = "(" + e.GetType().Name + ")"; }
                    seat = $"structure {(s == null ? "none" : $"#{s.Id} '{s.Entity?.Name}' {s.Entity?.Type}")}, IsPilot {p.IsPilot}, " +
                           $"DrivingEntity {(drv == null ? "none" : $"#{drv.Id} {drv.Type} '{drv.Name}'")}, structure pilot {pilot}, passengers [{pass}]";
                }
                catch (Exception e) { seat = "(" + e.GetType().Name + ": " + e.Message + ")"; }
                if (!probeSeat.TryGetValue(p.Id, out var before) || before != seat)
                {
                    probeSeat[p.Id] = seat;
                    ProbeLine($"seat {p.Name}: {seat}");
                }
                if (s == null) continue;

                if (probeSignalHooked.Add(s.Id))
                {
                    var sid = s.Id;
                    try
                    {
                        s.SignalChanged += (name, state, trig) => ProbeLine($"SignalChanged structure #{sid}: '{name}' -> {state} (triggered by entity#{trig})");
                    }
                    catch (Exception e) { ProbeLine($"SignalChanged hook #{sid}: {e.Message}"); }
                }

                string sig;
                try
                {
                    string Fmt(SenderSignal x)
                    {
                        bool? st = null;
                        try { st = s.GetSignalState(x.Name); } catch { }
                        string block = "";
                        if (x.BlockPos.HasValue)
                        {
                            var bp = x.BlockPos.Value;
                            bool? sw = null;
                            try { sw = s.GetBlock(bp)?.GetSwitchState(x.Index); } catch { }
                            block = $" block ({bp.x},{bp.y},{bp.z}) switch {(sw?.ToString() ?? "null")}";
                        }
                        return $"'{x.Name}'={(st?.ToString() ?? "?")}#{x.Index}{block}";
                    }
                    var panel = s.GetControlPanelSignals()?.Select(Fmt).ToList() ?? new List<string>();
                    var blocks = s.GetBlockSignals(null)?.Select(Fmt).ToList() ?? new List<string>();
                    var names = s.GetAllCustomDeviceNames() ?? new string[0];
                    sig = $"panel [{string.Join(", ", panel)}]; block signals [{string.Join(", ", blocks)}]; " +
                          $"{names.Length} custom device names [{string.Join(", ", names.Take(40))}]; powered {s.IsPowered}";
                }
                catch (Exception e) { sig = "(" + e.GetType().Name + ": " + e.Message + ")"; }
                if (!probeSignals.TryGetValue(s.Id, out var sb) || sb != sig)
                {
                    probeSignals[s.Id] = sig;
                    ProbeLine($"signals #{s.Id}: {sig}");
                }
            }
        }
    }
}
