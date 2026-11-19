using System;
using System.Collections.Generic;
using System.Linq;

namespace VoxtaBridge
{
    /// <summary>A container as the transfer logic sees it. The game side wraps an IContainer.</summary>
    public interface ITransferContainer
    {
        /// <summary>Unique, human-readable address, e.g. <c>Hold</c>, <c>ammo box 1</c>, <c>Fuel Depot @ Hauler</c>.</summary>
        string Label { get; }
        /// <summary>Custom block name, or null.</summary>
        string Name { get; }
        /// <summary>AmmoCntr, Container, Fridge, HarvestCntr.</summary>
        string Kind { get; }
        /// <summary>Name of the structure the container is on (always set; labels are "&lt;container&gt; @ &lt;structure&gt;").</summary>
        string Vessel { get; }
        /// <summary>On the structure the player is in/on.</summary>
        bool IsCurrent { get; }
        /// <summary>Someone has it open (IPlayfield.IsStructureDeviceLocked): don't touch.</summary>
        bool Locked { get; }

        int Count(int itemId);
        IEnumerable<int> ItemIds();
        /// <summary>
        /// Removes up to <paramref name="count"/>. The return value is NOT used: the game's IContainer.RemoveItems
        /// returns the remaining amount, not the removed one (cost 250 ingots on 2026-11-17 when read the other way).
        /// <see cref="Transfers.Move"/> only trusts <see cref="Count"/> before/after.
        /// </summary>
        void Remove(int itemId, int count);
        /// <summary>Adds up to <paramref name="count"/>; return value of the game call is not used either (see Remove).</summary>
        void Add(int itemId, int count);

        /// <summary>Full contents with slot and state (for items that must move as whole stacks).</summary>
        List<InvStack> GetStacks();
        /// <summary>Replace the full contents (IContainer.SetContent). Callers verify by re-reading.</summary>
        void SetStacks(List<InvStack> stacks);
        /// <summary>Slot count of the container block.</summary>
        int MaxSlots { get; }
    }

    public sealed class TransferResult
    {
        public bool Ok;
        public string Error;
        public int ItemId;
        public int Requested;
        public int Moved;
        /// <summary>Stacks taken out (RemoveOnly), with their state, for the caller to place.</summary>
        public List<InvStack> Stacks = new List<InvStack>();
        /// <summary>Removed from a source but neither arrived nor went back. Should always be 0.</summary>
        public int Lost;
        public string Target;
        public List<(string label, int count)> Sources = new List<(string, int)>();
        public string Note;

        public static TransferResult Fail(string error) => new TransferResult { Ok = false, Error = error };
    }

    /// <summary>Game-independent transfer rules (unit-tested). Runs on the playfield server's main thread.</summary>
    public static class Transfers
    {
        /// <summary>
        /// Words that mean the player's own inventory instead of a container. "bag" / "toolbar" limit it to one part.
        /// </summary>
        public static bool IsPlayerSpec(string spec, out bool bag, out bool toolbar)
        {
            bag = toolbar = false;
            if (string.IsNullOrWhiteSpace(spec)) return false;
            switch (spec.Trim().ToLowerInvariant())
            {
                case "player": case "me": case "myself": case "inventory": case "my inventory": case "player inventory":
                    bag = toolbar = true; return true;
                case "bag": case "backpack": case "my bag":
                    bag = true; return true;
                case "toolbar": case "hotbar": case "my toolbar":
                    toolbar = true; return true;
                default: return false;
            }
        }

        public static int CountIn(IEnumerable<InvStack> stacks, int itemId) => stacks.Where(s => s.Id == itemId).Sum(s => s.Count);

        /// <summary>Bag first, then toolbar (smaller stacks first); returns the taken stacks with their state.</summary>
        public static List<InvStack> TakeFromInventory(List<InvStack> bag, List<InvStack> toolbar, int itemId, int amount, bool useBag, bool useToolbar)
        {
            var taken = new List<InvStack>();
            if (useBag) taken.AddRange(Stacks.Take(bag, itemId, amount));
            if (useToolbar) taken.AddRange(Stacks.Take(toolbar, itemId, amount - Stacks.Total(taken)));
            return taken;
        }

        /// <summary>
        /// Items that must move as whole stacks: any stack of it in the sources carries state (loaded ammo, wear,
        /// spoilage) or it is a token.
        /// </summary>
        public static bool NeedsStackMode(int itemId, IEnumerable<ITransferContainer> sources, bool isToken) =>
            isToken || sources.Any(s => s.Count(itemId) > 0 && s.GetStacks().Any(st => st.Id == itemId && st.HasState));

        /// <summary>
        /// Takes up to <paramref name="amount"/> (or <see cref="All"/>) out of the sources, counted, nothing added
        /// anywhere: the caller hands the items on (to the player) and must give back what it can't place.
        /// <see cref="TransferResult.Stacks"/> holds what was taken (with state in stack mode).
        /// </summary>
        public static TransferResult RemoveOnly(int itemId, int amount, IList<ITransferContainer> sources, int maxPerTransfer, bool stackMode = false)
        {
            var usable = sources.Where(s => !s.Locked).ToList();
            var lockedWithItem = sources.Where(s => s.Locked && s.Count(itemId) > 0).Select(s => s.Label).ToList();
            var available = usable.Sum(s => s.Count(itemId));
            if (available == 0)
                return TransferResult.Fail(lockedWithItem.Count > 0 ? OpenMessage(lockedWithItem, "source") : "None of that item in the source container(s).");

            var want = amount == All ? available : amount;
            if (want <= 0) return TransferResult.Fail("The amount must be at least 1.");
            var note = new List<string>();
            if (want > maxPerTransfer) { want = maxPerTransfer; note.Add($"capped at {maxPerTransfer} per transfer"); }
            if (want > available) { note.Add($"only {available} available"); want = available; }

            var r = new TransferResult { Ok = true, ItemId = itemId, Requested = amount };
            foreach (var src in usable)
            {
                if (r.Moved >= want) break;
                var take = Math.Min(want - r.Moved, src.Count(itemId));
                if (take <= 0) continue;
                var before = src.Count(itemId);
                if (stackMode)
                {
                    var stacks = src.GetStacks();
                    var original = stacks.ToList();
                    var taken = Stacks.Take(stacks, itemId, take);
                    src.SetStacks(stacks);
                    if (before - src.Count(itemId) != Stacks.Total(taken))
                    {
                        src.SetStacks(original); // the game did not do what we wrote: undo
                        note.Add($"'{src.Label}' did not accept the change, left untouched");
                        continue;
                    }
                    r.Stacks.AddRange(taken);
                    r.Sources.Add((src.Label, Stacks.Total(taken)));
                    r.Moved += Stacks.Total(taken);
                    continue;
                }
                src.Remove(itemId, take);
                var removed = before - src.Count(itemId);
                if (removed <= 0) continue;
                r.Stacks.Add(new InvStack(itemId, removed));
                r.Sources.Add((src.Label, removed));
                r.Moved += removed;
            }
            if (lockedWithItem.Count > 0) note.Add($"skipped containers that are open in the game: {string.Join(", ", lockedWithItem)}");
            if (r.Moved <= 0) { r.Ok = false; r.Error = "Nothing could be taken out."; }
            r.Note = note.Count > 0 ? string.Join("; ", note) : null;
            return r;
        }

        /// <summary>
        /// Puts stacks with their state into containers in order: count-only items via Add, stacks with state by
        /// writing the full contents (verified by re-reading; undone if the game didn't keep it). Returns what didn't fit.
        /// </summary>
        public static List<InvStack> PutStacksInto(IEnumerable<InvStack> stacks, IEnumerable<ITransferContainer> targets, Func<int, int> stackSize)
        {
            var rest = stacks.ToList();
            foreach (var t in targets)
            {
                if (rest.Count == 0) break;
                if (t.Locked) continue;
                var plain = rest.Where(x => !x.HasState).ToList();
                var stateful = rest.Where(x => x.HasState).ToList();
                rest = new List<InvStack>();

                foreach (var p in plain)
                {
                    var before = t.Count(p.Id);
                    t.Add(p.Id, p.Count);
                    var added = Math.Max(0, t.Count(p.Id) - before);
                    if (added < p.Count) rest.Add(new InvStack(p.Id, p.Count - added));
                }

                if (stateful.Count > 0)
                {
                    var contents = t.GetStacks();
                    var original = contents.ToList();
                    var totalBefore = Stacks.Total(contents);
                    var notPlaced = Stacks.Place(contents, stateful, t.MaxSlots, stackSize);
                    var placed = Stacks.Total(stateful) - Stacks.Total(notPlaced);
                    if (placed > 0)
                    {
                        t.SetStacks(contents);
                        if (Stacks.Total(t.GetStacks()) - totalBefore != placed)
                        {
                            t.SetStacks(original);
                            notPlaced = stateful;
                        }
                    }
                    rest.AddRange(notPlaced);
                }
            }
            return rest;
        }

        /// <summary>
        /// Stack-mode move (items with state, tokens): whole stacks with their state from the sources into the target.
        /// Each source step is verified by counting both sides; on a mismatch both containers are restored.
        /// </summary>
        public static TransferResult MoveStacks(int itemId, int amount, ITransferContainer target,
            IList<ITransferContainer> sources, int maxPerTransfer, Func<int, int> stackSize)
        {
            if (target.Locked) return TransferResult.Fail(OpenMessage(new[] { target.Label }, "target"));
            var usable = sources.Where(s => s != target && !s.Locked).ToList();
            var lockedWithItem = sources.Where(s => s != target && s.Locked && s.Count(itemId) > 0).Select(s => s.Label).ToList();
            var available = usable.Sum(s => s.Count(itemId));
            if (available == 0)
                return TransferResult.Fail(lockedWithItem.Count > 0 ? OpenMessage(lockedWithItem, "source") : "None of that item in the source container(s).");

            var want = amount == All ? available : amount;
            if (want <= 0) return TransferResult.Fail("The amount must be at least 1.");
            var note = new List<string>();
            if (want > maxPerTransfer) { want = maxPerTransfer; note.Add($"capped at {maxPerTransfer} per transfer"); }
            if (want > available) { note.Add($"only {available} available"); want = available; }

            var r = new TransferResult { Ok = true, ItemId = itemId, Requested = amount, Target = target.Label };
            foreach (var src in usable)
            {
                if (r.Moved >= want) break;
                var srcStacks = src.GetStacks();
                var tgtStacks = target.GetStacks();
                var srcOriginal = srcStacks.ToList();
                var tgtOriginal = tgtStacks.ToList();
                var srcBefore = src.Count(itemId);
                var tgtBefore = target.Count(itemId);

                var taken = Stacks.Take(srcStacks, itemId, want - r.Moved);
                if (taken.Count == 0) continue;
                var notPlaced = Stacks.Place(tgtStacks, taken, target.MaxSlots, stackSize);
                // what didn't fit goes straight back into the source list (same slots were freed by Take)
                var backLeft = Stacks.Place(srcStacks, notPlaced, src.MaxSlots, stackSize);
                if (backLeft.Count > 0)
                {
                    note.Add($"'{target.Label}' is full and '{src.Label}' could not take everything back; left untouched");
                    continue; // nothing written yet
                }
                var moved = Stacks.Total(taken) - Stacks.Total(notPlaced);
                if (moved <= 0) { note.Add($"'{target.Label}' is full"); break; }

                src.SetStacks(srcStacks);
                target.SetStacks(tgtStacks);
                var srcDelta = srcBefore - src.Count(itemId);
                var tgtDelta = target.Count(itemId) - tgtBefore;
                if (srcDelta != moved || tgtDelta != moved)
                {
                    src.SetStacks(srcOriginal);
                    target.SetStacks(tgtOriginal);
                    note.Add($"the game did not keep the change for '{src.Label}' → '{target.Label}'; both restored");
                    continue;
                }
                r.Sources.Add((src.Label, moved));
                r.Moved += moved;
                if (notPlaced.Count > 0) { note.Add($"'{target.Label}' is full"); break; }
            }
            if (lockedWithItem.Count > 0) note.Add($"skipped containers that are open in the game: {string.Join(", ", lockedWithItem)}");
            if (r.Moved <= 0)
            {
                r.Ok = false;
                r.Error = note.Count > 0 ? $"Nothing was moved ({string.Join("; ", note)})." : "Nothing was moved.";
            }
            r.Note = note.Count > 0 ? string.Join("; ", note) : null;
            return r;
        }

        /// <summary>Puts items into containers in order (counted); returns how many went in.</summary>
        public static int PutInto(int itemId, int count, IEnumerable<ITransferContainer> targets)
        {
            var placed = 0;
            foreach (var t in targets)
            {
                if (placed >= count) break;
                if (t.Locked) continue;
                var before = t.Count(itemId);
                t.Add(itemId, count - placed);
                placed += Math.Max(0, t.Count(itemId) - before);
            }
            return placed;
        }

        public const int All = -1;

        static readonly Dictionary<string, string[]> KindWords = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["AmmoCntr"] = new[] { "ammo box", "ammo boxes", "ammo container", "ammo" },
            ["Container"] = new[] { "cargo box", "cargo boxes", "container", "containers", "cargo" },
            ["HarvestCntr"] = new[] { "harvest box", "harvest container", "cargo/harvest box", "harvest", "cargo" },
            ["Fridge"] = new[] { "fridge", "fridges", "food" },
        };

        /// <summary>The reason, phrased so the character can pass it on as is.</summary>
        public static string OpenMessage(IEnumerable<string> labels, string role)
        {
            var list = string.Join(", ", labels.Select(l => $"'{l}'"));
            var verb = labels.Count() == 1 ? "is" : "are";
            return $"Not moved: {list} {verb} open in the game right now (its inventory window is open), " +
                   $"and the {role} must be closed so nothing gets lost. Close it and try again.";
        }

        public static string KindLabel(string kind)
        {
            switch (kind)
            {
                case "AmmoCntr": return "ammo box";
                case "Container": return "cargo box";
                case "HarvestCntr": return "harvest box";
                case "Fridge": return "fridge";
                default: return "container";
            }
        }

        /// <summary>
        /// Labels for a structure's containers: the custom name if unique, else "kind n" (numbered per kind in
        /// list order); " @ Vessel" for docked vessels.
        /// </summary>
        public static List<string> MakeLabels(IList<(string name, string kind, string vessel)> containers)
        {
            var labels = new List<string>();
            var nameCount = containers.Where(c => !string.IsNullOrWhiteSpace(c.name))
                .GroupBy(c => (c.name.Trim().ToLowerInvariant(), c.vessel ?? ""))
                .ToDictionary(g => g.Key, g => g.Count());
            var kindIndex = new Dictionary<(string, string), int>();
            foreach (var c in containers)
            {
                string label;
                var vesselKey = c.vessel ?? "";
                if (!string.IsNullOrWhiteSpace(c.name) && nameCount[(c.name.Trim().ToLowerInvariant(), vesselKey)] == 1)
                    label = c.name.Trim();
                else
                {
                    var key = (c.kind ?? "", vesselKey);
                    kindIndex.TryGetValue(key, out var n);
                    kindIndex[key] = ++n;
                    label = $"{KindLabel(c.kind)} {n}";
                }
                if (!string.IsNullOrEmpty(c.vessel)) label += $" @ {c.vessel}";
                labels.Add(label);
            }
            return labels;
        }

        /// <summary>
        /// Containers matching a spec: exact label, then custom name, then a kind word ("ammo box", "fridge", …),
        /// each optionally narrowed with "@ vessel". Empty spec matches nothing (caller decides).
        /// </summary>
        public static List<ITransferContainer> Match(IEnumerable<ITransferContainer> all, string spec)
        {
            var list = all.ToList();
            if (string.IsNullOrWhiteSpace(spec)) return new List<ITransferContainer>();
            spec = spec.Trim();

            var exact = list.Where(c => string.Equals(c.Label, spec, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count > 0) return exact;

            string vessel = null;
            var at = spec.LastIndexOf('@');
            if (at >= 0)
            {
                vessel = spec.Substring(at + 1).Trim();
                spec = spec.Substring(0, at).Trim();
                list = list.Where(c => string.Equals(c.Vessel, vessel, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            var named = list.Where(c => string.Equals(c.Name?.Trim(), spec, StringComparison.OrdinalIgnoreCase)).ToList();
            if (named.Count > 0) return named;

            // Label without its "@ structure" part, e.g. "ammo box 1" for "ammo box 1 @ Test Corvette".
            var bare = list.Where(c =>
            {
                var at2 = c.Label.LastIndexOf(" @ ", StringComparison.Ordinal);
                var head = at2 >= 0 ? c.Label.Substring(0, at2) : c.Label;
                return string.Equals(head, spec, StringComparison.OrdinalIgnoreCase);
            }).ToList();
            if (bare.Count > 0) return bare;

            var byKind = list.Where(c => KindWords.TryGetValue(c.Kind ?? "", out var words)
                && words.Any(w => string.Equals(w, spec, StringComparison.OrdinalIgnoreCase))).ToList();
            if (byKind.Count > 0) return byKind;

            // "the ship" / "ship" / "anywhere": every container on the structure the player is in.
            if (spec.Equals("ship", StringComparison.OrdinalIgnoreCase) || spec.Equals("the ship", StringComparison.OrdinalIgnoreCase)
                || spec.Equals("any", StringComparison.OrdinalIgnoreCase) || spec.Equals("anywhere", StringComparison.OrdinalIgnoreCase))
                return list.Where(c => vessel != null || c.IsCurrent).ToList();

            return new List<ITransferContainer>();
        }

        /// <summary>
        /// All item ids matching a search (find_item): exact internal/display names win; otherwise every item whose
        /// display or internal name contains the text (ignoring spaces/dashes). Several results are fine here.
        /// </summary>
        public static List<int> MatchItems(string spec, IEnumerable<int> candidates, Func<int, string> internalName, Func<int, string> displayName)
        {
            var ids = candidates.Distinct().ToList();
            if (string.IsNullOrWhiteSpace(spec)) return new List<int>();
            spec = spec.Trim();
            string Squash(string s) => s?.Replace(" ", "").Replace("-", "").Replace("_", "");
            var exact = ids.Where(id => string.Equals(internalName(id), spec, StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(displayName(id), spec, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count > 0) return exact;
            var q = Squash(spec);
            return ids.Where(id => (Squash(displayName(id))?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                                || (Squash(internalName(id))?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0).ToList();
        }

        /// <summary>
        /// Item id from a spec, among the ids present in <paramref name="candidates"/>: exact internal name,
        /// exact display name, then "contains" on either. Several different items → error listing them.
        /// </summary>
        public static int ResolveItem(string spec, IEnumerable<int> candidates, Func<int, string> internalName,
            Func<int, string> displayName, out string error)
        {
            error = null;
            var ids = candidates.Distinct().ToList();
            if (string.IsNullOrWhiteSpace(spec)) { error = "No item given."; return 0; }
            spec = spec.Trim();
            string Show(int id) => displayName(id) ?? internalName(id) ?? $"item #{id}";
            string Squash(string s) => s?.Replace(" ", "").Replace("-", "").Replace("_", "");

            List<int> Pick(Func<int, bool> pred) => ids.Where(pred).ToList();
            var steps = new Func<int, bool>[]
            {
                id => string.Equals(internalName(id), spec, StringComparison.OrdinalIgnoreCase),
                id => string.Equals(displayName(id), spec, StringComparison.OrdinalIgnoreCase),
                id => string.Equals(Squash(displayName(id)), Squash(spec), StringComparison.OrdinalIgnoreCase)
                      || string.Equals(Squash(internalName(id)), Squash(spec), StringComparison.OrdinalIgnoreCase),
                id => (displayName(id)?.IndexOf(spec, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                      || (internalName(id)?.IndexOf(spec, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0,
            };
            foreach (var step in steps)
            {
                var hit = Pick(step);
                if (hit.Count == 1) return hit[0];
                if (hit.Count > 1)
                {
                    error = $"'{spec}' matches several items: {string.Join(", ", hit.Select(Show).Distinct())}. Say which one.";
                    return 0;
                }
            }
            error = $"No '{spec}' found in the source container(s).";
            return 0;
        }

        /// <summary>
        /// Moves <paramref name="amount"/> (or <see cref="All"/>) of an item into exactly one target container,
        /// taking it from the source containers in order. Locked containers are refused. Anything that does not
        /// fit is put back where it came from. The caller passes the item already resolved.
        /// </summary>
        public static TransferResult Move(int itemId, int amount, ITransferContainer target,
            IList<ITransferContainer> sources, int maxPerTransfer)
        {
            if (target.Locked) return TransferResult.Fail(OpenMessage(new[] { target.Label }, "target"));
            var usable = sources.Where(s => s != target && !s.Locked).ToList();
            var lockedSources = sources.Where(s => s != target && s.Locked).Select(s => s.Label).ToList();
            var lockedWithItem = sources.Where(s => s != target && s.Locked && s.Count(itemId) > 0).Select(s => s.Label).ToList();
            var available = usable.Sum(s => s.Count(itemId));
            if (available == 0)
            {
                // The item is there, but only in containers someone has open: say so, not "none".
                if (lockedWithItem.Count > 0) return TransferResult.Fail(OpenMessage(lockedWithItem, "source"));
                return TransferResult.Fail("None of that item in the source container(s).");
            }

            var want = amount == All ? available : amount;
            if (want <= 0) return TransferResult.Fail("The amount must be at least 1.");
            var note = new List<string>();
            if (want > maxPerTransfer) { want = maxPerTransfer; note.Add($"capped at {maxPerTransfer} per transfer"); }
            if (want > available) { note.Add($"only {available} available"); want = available; }

            var result = new TransferResult { Ok = true, ItemId = itemId, Requested = amount, Target = target.Label };
            var targetBefore = target.Count(itemId);
            var remaining = want;
            foreach (var src in usable)
            {
                if (remaining <= 0) break;
                var take = Math.Min(remaining, src.Count(itemId));
                if (take <= 0) continue;

                // Every step is measured by counting, never by the API's return values.
                var srcBefore = src.Count(itemId);
                src.Remove(itemId, take);
                var removed = srcBefore - src.Count(itemId);
                if (removed <= 0) continue;

                var tBefore = target.Count(itemId);
                target.Add(itemId, removed);
                var added = target.Count(itemId) - tBefore;
                var leftover = removed - added;

                if (leftover > 0)
                {
                    // Give back what did not fit, and check that it really went back.
                    var backBefore = src.Count(itemId);
                    src.Add(itemId, leftover);
                    var back = src.Count(itemId) - backBefore;
                    if (back < leftover)
                    {
                        result.Lost += leftover - back;
                        note.Add($"WARNING: {leftover - back} could not be put back into '{src.Label}'");
                    }
                }
                if (added > 0) result.Sources.Add((src.Label, added));
                remaining -= added;
                if (leftover > 0) { note.Add($"'{target.Label}' is full"); break; }
            }

            result.Moved = target.Count(itemId) - targetBefore;
            if (result.Moved != want - remaining)
                note.Add($"counted {result.Moved} arriving, expected {want - remaining}");
            if (result.Moved <= 0)
            {
                result.Ok = false;
                result.Error = note.Count > 0 ? $"Nothing was moved ({string.Join("; ", note)})." : "Nothing was moved.";
            }
            if (lockedWithItem.Count > 0) note.Add($"skipped containers that are open in the game: {string.Join(", ", lockedWithItem)}");
            result.Note = note.Count > 0 ? string.Join("; ", note) : null;
            return result;
        }
    }
}
