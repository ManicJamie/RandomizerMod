﻿using RandomizerCore;
using RandomizerCore.Extensions;
using RandomizerMod.RandomizerData;
using RandomizerMod.Settings;
using RandomizerCore.Randomization;
using RandomizerCore.Logic;
using System.Collections.ObjectModel;
using ItemChanger;
using StartDef = RandomizerMod.RandomizerData.StartDef;

namespace RandomizerMod.RC
{
    public delegate bool ItemMatchHandler(string name, out ItemRequestInfo info);
    public delegate bool LocationMatchHandler(string name, out LocationRequestInfo info);
    public delegate bool TransitionMatchHandler(string name, out TransitionRequestInfo info);

    /// <summary>
    /// Class used to build the request that is passed to the randomizer. The OnUpdate event allows modification of the request as it is built.
    /// </summary>
    public class RequestBuilder
    {
        public void Run(out RandomizationStage[] stages, out RandoModContext ctx) 
        {
            HandleUpdateEvents();

            RandoFactory factory = new(this);
            stages = Stages.Select(s => s.ToRandomizationStage(factory)).ToArray();
            foreach (RandomizationStage stage in stages) rng.PermuteInPlace(stage.groups); // random tiebreakers between groups

            ctx = this.ctx;
            ctx.Vanilla.AddRange(Vanilla.Values.SelectMany(v => v).Select(factory.MakeVanillaPlacement));
            ctx.itemPlacements.AddRange(StartItems.EnumerateWithMultiplicity().Select(factory.MakeStartPlacement));
            foreach (var kvp in Preplaced)
            {
                if (lm.TransitionLookup.ContainsKey(kvp.Key))
                {
                    ctx.transitionPlacements.AddRange(kvp.Value.Select(factory.MakeTransitionPlacement));
                }
                else
                {
                    ctx.itemPlacements.AddRange(kvp.Value.Select(factory.MakeItemPlacement));
                }
            }
        }


        private readonly List<StageBuilder> _stages;
        public readonly ReadOnlyCollection<StageBuilder> Stages;
        public readonly StageBuilder MainItemStage;
        public readonly ItemGroupBuilder MainItemGroup;

        /// <summary>
        /// Enumerates the GroupBuilders of type ItemGroupBuilder.
        /// </summary>
        public IEnumerable<ItemGroupBuilder> EnumerateItemGroups()
        {
            foreach (StageBuilder sb in Stages)
            {
                foreach (GroupBuilder gb in sb.Groups)
                {
                    if (gb is ItemGroupBuilder igb) yield return igb;
                }
            }
        }

        /// <summary>
        /// Enumerates the GroupBuilers not of type ItemGroupBuilder.
        /// </summary>
        public IEnumerable<GroupBuilder> EnumerateTransitionGroups()
        {
            foreach (StageBuilder sb in Stages)
            {
                foreach (GroupBuilder gb in sb.Groups)
                {
                    if (gb is not ItemGroupBuilder) yield return gb;
                }
            }
        }


        public readonly Dictionary<string, ItemRequestInfo> ItemRequests = new();
        public readonly List<ItemMatchHandler> ItemMatchers = new();

        public readonly Dictionary<string, LocationRequestInfo> LocationRequests = new();
        public readonly List<LocationMatchHandler> LocationMatchers = new();

        public readonly Dictionary<string, TransitionRequestInfo> TransitionRequests = new();
        public readonly List<TransitionMatchHandler> TransitionMatchers = new();

        public readonly Bucket<string> StartItems = new();

        /// <summary>
        /// List of vanilla requests, with indexing for fast lookup by location/source transition name.
        /// </summary>
        public readonly Dictionary<string, List<VanillaDef>> Vanilla = new();
        /// <summary>
        /// List of preplaced requests, with indexing for fast lookup by location/source transition name.
        /// </summary>
        public readonly Dictionary<string, List<VanillaDef>> Preplaced = new();

        public readonly GenerationSettings gs;
        public readonly SettingsPM pm;
        public readonly LogicManager lm;
        public readonly Random rng;
        public readonly RandoMonitor rm;
        public readonly RandoModContext ctx;

        private static readonly List<string> _set = new(); // used as a utility for several methods

        public RequestBuilder(GenerationSettings gs, SettingsPM pm, RandoMonitor rm)
        {
            this.gs = gs;
            this.pm = pm;
            this.rng = new(gs.Seed + 11);
            this.rm = rm;

            ctx = new(gs, SelectStart());
            lm = ctx.LM;
            
            _stages = new();
            Stages = new(_stages);

            MainItemStage = new(RBConsts.MainItemStage);
            MainItemGroup = new()
            {
                label = RBConsts.MainItemGroup,
            };
            MainItemStage.Add(MainItemGroup);
            _stages.Add(MainItemStage);

            OnGetGroupFor = new(out _onGetGroupForOwner);
            CostConverters = new(out _costConvertersOwner);
        }

        public enum ElementType
        {
            Unknown,
            Item,
            Location,
            Transition
        }

        /// <summary>
        /// Adds a stage with the given label after all existing stages.
        /// </summary>
        public StageBuilder AddStage(string name)
        {
            return InsertStage(_stages.Count, name);
        }

        /// <summary>
        /// Creates a StageBuilder at the specified index with the given label.
        /// <br/>Throws an exception if the index is out of range or the label is already in use.
        /// </summary>
        public StageBuilder InsertStage(int index, string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (_stages.Any(s => s.label == name)) throw new ArgumentException(nameof(name));
            if (index < 0 || index > _stages.Count) throw new ArgumentOutOfRangeException(nameof(index));

            StageBuilder sb = new(name);
            _stages.Insert(index, sb);
            return sb;
        }

        /// <summary>
        /// Moves the stage with the given label to the given index.
        /// <br/>Throws an exception if the index is out of range or the stage with the given label is not found.
        /// </summary>
        public void MoveStage(int destinationIndex, string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            int init = _stages.FindIndex(s => s.label == name);
            if (init < 0) throw new ArgumentException(nameof(name));
            if (destinationIndex < 0 || destinationIndex > _stages.Count) throw new ArgumentOutOfRangeException(nameof(destinationIndex));

            StageBuilder sb = _stages[init];
            _stages.RemoveAt(init);
            _stages.Insert(destinationIndex, sb);
        }

        /// <summary>
        /// Searches for a StageBuilder with the given label and returns it if found.
        /// </summary>
        public bool TryGetStage(string name, out StageBuilder sb)
        {
            int i = _stages.FindIndex(s => s.label == name);
            if (i < 0)
            {
                sb = null;
                return false;
            }
            else
            {
                sb = _stages[i];
                return true;
            }
        }

        /// <summary>
        /// Searches for the appropriate group for the named item, location, or transition, using the OnGetGroupFor event.
        /// </summary>
        public GroupBuilder GetGroupFor(string name, ElementType type = ElementType.Unknown)
        {
            foreach (GroupResolver resolver in _onGetGroupForOwner.GetSubscribers())
            {
                if (resolver(this, name, type, out GroupBuilder gb)) return gb;
            }

            if (Data.IsItem(name) || Data.IsLocation(name))
            {
                return MainItemGroup;
            }

            return null;
        }

        /// <summary>
        /// Searches for the appropriate item group for the named item, using the OnGetGroupFor event.
        /// </summary>
        public ItemGroupBuilder GetItemGroupFor(string name)
        {
            if (GetGroupFor(name, ElementType.Item) is ItemGroupBuilder igb) return igb;
            return MainItemGroup;
        }

        /// <summary>
        /// Searches for the appropriate item group for the named location, using the OnGetGroupFor event.
        /// </summary>
        public ItemGroupBuilder GetLocationGroupFor(string name)
        {
            if (GetGroupFor(name, ElementType.Location) is ItemGroupBuilder igb) return igb;
            return MainItemGroup;
        }

        /// <summary>
        /// Adds one copy of the named item to the group returned by GetItemGroupFor.
        /// </summary>
        public void AddItemByName(string name)
        {
            GetItemGroupFor(name).Items.Add(name);
        }

        /// <summary>
        /// Adds the requested number of copies of the named item to the group returned by GetItemGroupFor.
        /// </summary>
        public void AddItemByName(string name, int count)
        {
            GetItemGroupFor(name).Items.Increment(name, count);
        }

        /// <summary>
        /// Removes all copies of the named item from all item groups.
        /// </summary>
        public void RemoveItemByName(string name)
        {
            foreach (ItemGroupBuilder gb in EnumerateItemGroups()) gb.Items.RemoveAll(name);
        }

        public bool TryGetItemRequest(string name, out ItemRequestInfo info)
        {
            if (ItemRequests.TryGetValue(name, out info))
            {
                return true;
            }
            else
            {
                foreach (ItemMatchHandler matcher in ItemMatchers)
                {
                    if (matcher(name, out info))
                    {
                        ItemRequests.Add(name, info);
                        return true;
                    }
                }
            }
            info = default;
            return false;
        }

        public void EditItemRequest(string name, Action<ItemRequestInfo> edit)
        {
            if (!TryGetItemRequest(name, out ItemRequestInfo info))
            {
                ItemRequests.Add(name, info = new());
            }

            edit(info);
        }

        public bool TryGetItemDef(string name, out ItemDef def)
        {
            def = TryGetItemRequest(name, out ItemRequestInfo info) && info.getItemDef != null 
                ? info.getItemDef() 
                : Data.GetItemDef(name);
            return def is not null;
        }

        /// <summary>
        /// Adds one copy of the named location to the group returned by GetLocationGroupFor.
        /// </summary>
        public void AddLocationByName(string name)
        {
            GetLocationGroupFor(name).Locations.Add(name);
        }

        /// <summary>
        /// Adds the requested number of copies of the named location to the group returned by GetLocationGroupFor.
        /// </summary>
        public void AddLocationByName(string name, int count)
        {
            GetLocationGroupFor(name).Locations.Increment(name, count);
        }

        /// <summary>
        /// Removes all copies of the named location from all item groups.
        /// </summary>
        public void RemoveLocationByName(string name)
        {
            foreach (ItemGroupBuilder gb in EnumerateItemGroups()) gb.Locations.RemoveAll(name);
        }

        /// <summary>
        /// Removes the requested number of copies of the named location from the group returned by GetLocationGroupFor.
        /// </summary>
        public void RemoveLocationByName(string name, int count)
        {
            GetLocationGroupFor(name).Locations.Remove(name, count);
        }

        public bool TryGetLocationRequest(string name, out LocationRequestInfo info)
        {
            if (LocationRequests.TryGetValue(name, out info))
            {
                return true;
            }
            else
            {
                foreach (LocationMatchHandler matcher in LocationMatchers)
                {
                    if (matcher(name, out info))
                    {
                        LocationRequests.Add(name, info);
                        return true;
                    }
                }
            }
            info = default;
            return false;
        }

        public void EditLocationRequest(string name, Action<LocationRequestInfo> edit)
        {
            if (!LocationRequests.TryGetValue(name, out LocationRequestInfo info))
            {
                LocationRequests.Add(name, info = new());
            }
            edit(info);
        }

        public bool TryGetLocationDef(string name, out LocationDef def)
        {
            def = TryGetLocationRequest(name, out LocationRequestInfo info) && info.getLocationDef != null
                ? info.getLocationDef()
                : Data.GetLocationDef(name);
            return def is not null;
        }

        /// <summary>
        /// Removes all copies of all items in any item groups for which the predicate returns true.
        /// </summary>
        public void RemoveItemsWhere(Predicate<string> selector)
        {
            foreach (ItemGroupBuilder gb in EnumerateItemGroups())
            {
                if (_set.Count != 0) _set.Clear();
                foreach (string s in gb.Items.EnumerateDistinct())
                {
                    if (selector(s)) _set.Add(s);
                }
                foreach (string s in _set) gb.Items.RemoveAll(s);
            }
            _set.Clear();
        }

        /// <summary>
        /// Replaces each copy of the named item in each item group with the replacement string.
        /// </summary>
        public void ReplaceItem(string name, string replaceWith)
        {
            foreach (ItemGroupBuilder gb in EnumerateItemGroups())
            {
                gb.Items.Replace(name, replaceWith);
            }
        }

        public void ReplaceItem(string name, Func<int, IEnumerable<string>> replacer)
        {
            foreach (ItemGroupBuilder gb in EnumerateItemGroups())
            {
                gb.Items.Replace(name, replacer);
            }
        }

        public void ReplaceItem(Predicate<string> selector, Func<string, int, IEnumerable<string>> replacer)
        {
            foreach (ItemGroupBuilder gb in EnumerateItemGroups())
            {
                if (_set.Count != 0) _set.Clear();

                foreach (string s in gb.Items.EnumerateDistinct())
                {
                    if (selector(s)) _set.Add(s);
                }
                foreach (string s in _set)
                {
                    int count = gb.Items.GetCount(s);
                    gb.Items.RemoveAll(s);
                    gb.Items.AddRange(replacer(s, count));
                }
            }
            _set.Clear();
        }

        /// <summary>
        /// Removes all copies of all locations in any item groups for which the predicate returns true.
        /// </summary>
        public void RemoveLocationsWhere(Predicate<string> selector)
        {
            foreach (ItemGroupBuilder gb in EnumerateItemGroups())
            {
                if (_set.Count != 0) _set.Clear();
                foreach (string s in gb.Locations.EnumerateDistinct())
                {
                    if (selector(s)) _set.Add(s);
                }
                foreach (string s in _set) gb.Locations.RemoveAll(s);
            }
            _set.Clear();
        }

        public void ReplaceLocation(string name, string replaceWith)
        {
            foreach (ItemGroupBuilder gb in EnumerateItemGroups())
            {
                gb.Locations.Replace(name, replaceWith);
            }
        }

        public void ReplaceLocation(string name, Func<int, IEnumerable<string>> replacer)
        {
            foreach (ItemGroupBuilder gb in EnumerateItemGroups())
            {
                gb.Locations.Replace(name, replacer);
            }
        }

        public void ReplaceLocation(Predicate<string> selector, Func<string, int, IEnumerable<string>> replacer)
        {
            foreach (ItemGroupBuilder gb in EnumerateItemGroups())
            {
                if (_set.Count != 0) _set.Clear();

                foreach (string s in gb.Locations.EnumerateDistinct())
                {
                    if (selector(s)) _set.Add(s);
                }
                foreach (string s in _set)
                {
                    int count = gb.Locations.GetCount(s);
                    gb.Locations.RemoveAll(s);
                    gb.Locations.AddRange(replacer(s, count));
                }
            }
            _set.Clear();
        }

        public bool TryGetTransitionRequest(string name, out TransitionRequestInfo info)
        {
            if (TransitionRequests.TryGetValue(name, out info))
            {
                return true;
            }
            else
            {
                foreach (TransitionMatchHandler matcher in TransitionMatchers)
                {
                    if (matcher(name, out info))
                    {
                        TransitionRequests.Add(name, info);
                        return true;
                    }
                }
            }
            info = default;
            return false;
        }

        public void EditTransitionRequest(string name, Action<TransitionRequestInfo> edit)
        {
            if (!TransitionRequests.TryGetValue(name, out TransitionRequestInfo info))
            {
                TransitionRequests.Add(name, info = new());
            }
            edit(info);
        }

        public bool TryGetTransitionDef(string name, out TransitionDef def)
        {
            def = TryGetTransitionRequest(name, out TransitionRequestInfo info) && info.getTransitionDef != null
                ? info.getTransitionDef()
                : Data.GetTransitionDef(name);
            return def is not null;
        }

        /// <summary>
        /// Removes the transition by name from all transition groups.
        /// </summary>
        /// <param name="name"></param>
        public void RemoveTransitionByName(string name)
        {
            foreach (GroupBuilder gb in EnumerateTransitionGroups())
            {
                if (gb is TransitionGroupBuilder tgb)
                {
                    tgb.Sources.RemoveAll(name);
                    tgb.Targets.RemoveAll(name);
                }
                else if (gb is SymmetricTransitionGroupBuilder stgb)
                {
                    stgb.Group1.RemoveAll(name);
                    stgb.Group2.RemoveAll(name);
                }
                else if (gb is SelfDualTransitionGroupBuilder sdtgb)
                {
                    sdtgb.Transitions.RemoveAll(name);
                }
            }
        }

        /// <summary>
        /// Removes all copies of all transitions in any transition groups where the selector returns true.
        /// </summary>
        public void RemoveTransitionsWhere(Func<string, bool> selector)
        {
            foreach (GroupBuilder gb in EnumerateTransitionGroups())
            {
                if (gb is TransitionGroupBuilder tgb)
                {
                    RemoveFromBucket(tgb.Sources, selector);
                    RemoveFromBucket(tgb.Targets, selector);
                }
                else if (gb is SymmetricTransitionGroupBuilder stgb)
                {
                    RemoveFromBucket(stgb.Group1, selector);
                    RemoveFromBucket(stgb.Group2, selector);
                }
                else if (gb is SelfDualTransitionGroupBuilder sdtgb)
                {
                    RemoveFromBucket(sdtgb.Transitions, selector);
                }
            }
        }

        /// <summary>
        /// Identical to RemoveTransitionByName, but if the transition is a source, it is ensured to be in Vanilla with its VanillaTarget.
        /// </summary>
        public void UnrandomizeTransitionByName(string name)
        {
            RemoveTransitionByName(name);
            EnsureVanillaSourceTransition(name);
        }

        /// <summary>
        /// Identical to RemoveTransitionsWhere, but each removed source transition is ensured to be in Vanilla with its VanillaTarget.
        /// </summary>
        public void UnrandomizeTransitionsWhere(Func<string, bool> selector)
        {
            foreach (GroupBuilder gb in EnumerateTransitionGroups())
            {
                if (gb is TransitionGroupBuilder tgb)
                {
                    TakeFromBucket(_set, tgb.Sources, selector);
                    foreach (string s in _set) EnsureVanillaSourceTransition(s);
                    _set.Clear();
                    RemoveFromBucket(tgb.Targets, selector);
                }
                else if (gb is SymmetricTransitionGroupBuilder stgb)
                {
                    TakeFromBucket(_set, stgb.Group1, selector);
                    foreach (string s in _set) EnsureVanillaSourceTransition(s);
                    _set.Clear();
                    TakeFromBucket(_set, stgb.Group2, selector);
                    foreach (string s in _set) EnsureVanillaSourceTransition(s);
                    _set.Clear();
                }
                else if (gb is SelfDualTransitionGroupBuilder sdtgb)
                {
                    TakeFromBucket(_set, sdtgb.Transitions, selector);
                    foreach (string s in _set) EnsureVanillaSourceTransition(s);
                    _set.Clear();
                }
            }
        }

        public void AddToVanilla(string item, string location)
        {
            if (!Vanilla.TryGetValue(location, out List<VanillaDef> defs))
            {
                Vanilla.Add(location, defs = new(1));
            }

            defs.Add(new VanillaDef(item, location));
        }

        public void AddToVanilla(VanillaDef def)
        {
            if (!Vanilla.TryGetValue(def.Location, out List<VanillaDef> defs))
            {
                Vanilla.Add(def.Location, defs = new(1));
            }

            defs.Add(def);
        }

        /// <summary>
        /// Ensures that the Vanilla bucket contains the placement of the transition's VanillaTarget at the transition.
        /// <br/>Has no effect on a transition without a VanillaTarget, such as a OneWayOut transition.
        /// <br/>Only ensures one side of a coupled transition is vanilla.
        /// </summary>
        public void EnsureVanillaSourceTransition(string source)
        {
            if (TryGetTransitionDef(source, out TransitionDef def) && def?.VanillaTarget is string target)
            {
                if (!Vanilla.TryGetValue(source, out List<VanillaDef> defs))
                {
                    Vanilla.Add(source, defs = new(1));
                }

                if (defs.Count == 0)
                {
                    defs.Add(new VanillaDef(target, source));
                }
            }
        }

        /// <summary>
        /// Removes all vanilla placements for the specified location or source transition.
        /// </summary>
        public void RemoveFromVanilla(string location)
        {
            Vanilla.Remove(location);
        }

        /// <summary>
        /// Removes all vanilla placements for the specified location or source transition, matching the specified item or target transition.
        /// </summary>
        public void RemoveFromVanilla(string location, string item)
        {
            if (Vanilla.TryGetValue(location, out List<VanillaDef> defs))
            {
                defs.RemoveAll(def => def.Item == item);
            }
        }

        /// <summary>
        /// Removes all vanilla placements matching the defined location, item, and cost list (compared sequentially).
        /// </summary>
        public void RemoveFromVanilla(VanillaDef def)
        {
            if (Vanilla.TryGetValue(def.Location, out List<VanillaDef> defs))
            {
                defs.RemoveAll(def1 => def1.Equals(def));
            }
        }

        public void AddToStart(string item)
        {
            StartItems.Add(item);
        }

        public void AddToStart(string item, int count)
        {
            StartItems.Increment(item, count);
        }

        public void RemoveFromStart(string item)
        {
            StartItems.RemoveAll(item);
        }

        public bool IsAtStart(string item)
        {
            return StartItems.GetCount(item) > 0;
        }

        public void AddToPreplaced(string item, string location)
        {
            if (!Preplaced.TryGetValue(location, out List<VanillaDef> defs))
            {
                Preplaced.Add(location, defs = new(1));
            }

            defs.Add(new VanillaDef(item, location));
        }

        public void AddToPreplaced(VanillaDef def)
        {
            if (!Preplaced.TryGetValue(def.Location, out List<VanillaDef> defs))
            {
                Preplaced.Add(def.Location, defs = new(1));
            }

            defs.Add(def);
        }

        /// <summary>
        /// Removes all preplaced placements for the specified location or source transition.
        /// </summary>
        public void RemoveFromPreplaced(string location)
        {
            Preplaced.Remove(location);
        }

        /// <summary>
        /// Removes all preplaced placements for the specified location or source transition, matching the specified item or target transition.
        /// </summary>
        public void RemoveFromPreplaced(string location, string item)
        {
            if (Preplaced.TryGetValue(location, out List<VanillaDef> defs))
            {
                defs.RemoveAll(def => def.Item == item);
            }
        }

        /// <summary>
        /// Removes all preplaced placements matching the defined location, item, and cost list (compared sequentially).
        /// </summary>
        public void RemoveFromPreplaced(VanillaDef def)
        {
            if (Preplaced.TryGetValue(def.Location, out List<VanillaDef> defs))
            {
                defs.RemoveAll(def1 => def1.Equals(def));
            }
        }

        private void RemoveFromBucket(Bucket<string> bucket, Func<string, bool> selector)
        {
            _set.Clear();
            _set.AddRange(bucket.EnumerateDistinct().Where(selector));
            foreach (string s in _set) bucket.RemoveAll(s);
            _set.Clear();
        }

        // adds the distinct matches to result, and removes them from the bucket.
        private void TakeFromBucket(List<string> result, Bucket<string> bucket, Func<string, bool> selector)
        {
            result.Clear();
            result.AddRange(bucket.EnumerateDistinct().Where(selector));
            foreach (string s in result) bucket.RemoveAll(s);
        }

        public delegate bool GroupResolver(RequestBuilder rb, string item, ElementType type, out GroupBuilder gb);
        public readonly PriorityEvent<GroupResolver> OnGetGroupFor;
        private readonly PriorityEvent<GroupResolver>.IPriorityEventOwner _onGetGroupForOwner;

        public delegate bool StartResolver(Random rng, GenerationSettings gs, SettingsPM pm, out StartDef def);
        public static readonly PriorityEvent<StartResolver> OnSelectStart = new(out _onSelectStartOwner);
        private static readonly PriorityEvent<StartResolver>.IPriorityEventOwner _onSelectStartOwner;

        protected StartDef SelectStart()
        {
            StartDef def = null;
            foreach (StartResolver d in _onSelectStartOwner.GetSubscribers())
            {
                if (d(rng, gs, pm, out def))
                {
                    break;
                }
            }

            return def;
        }

        public delegate bool CostConverter(LogicCost logicCost, out Cost cost);
        public readonly PriorityEvent<CostConverter> CostConverters;
        private readonly PriorityEvent<CostConverter>.IPriorityEventOwner _costConvertersOwner;

        public bool TryConvertCost(LogicCost logicCost, out Cost cost)
        {
            foreach (CostConverter d in _costConvertersOwner.GetSubscribers())
            {
                if (d(logicCost, out cost)) return true;
            }
            cost = null;
            return false;
        }

        public delegate void RequestBuilderUpdateHandler(RequestBuilder rb);
        public static readonly PriorityEvent<RequestBuilderUpdateHandler> OnUpdate = new(out _onUpdateOwner);
        private static readonly PriorityEvent<RequestBuilderUpdateHandler>.IPriorityEventOwner _onUpdateOwner;

        protected void HandleUpdateEvents()
        {
            foreach (var d in _onUpdateOwner?.GetSubscribers())
            {
                d?.Invoke(this);
            }
        }

        static RequestBuilder()
        {
            BuiltinRequests.ApplyAll();
        }
    }
}
