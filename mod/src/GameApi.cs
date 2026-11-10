// The only file that touches The Sims 3's own assemblies.
//
// IMPORTANT: this file was written without access to the game's DLLs. It is
// compile-checked only against mod/stubs/Sims3Stubs.cs, which lists every
// game type and member assumed here. Expect to adjust names/signatures when
// first building against the real ScriptCore/SimIFace/Sims3GameplaySystems/UI.
using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Skills;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.UI;

namespace Sims3MP
{
    /// <summary>Entry point. The game instantiates this class because of the
    /// tunable field (the package needs a matching "Sims3MP.Main" tuning XML).</summary>
    public class Main
    {
        [Tunable] protected static bool kInstantiator = false;

        static GameApi sGame;
        static Session sSession;
        static Menu sMenu;
        static bool sCommand, sErrorShown;

        static Main()
        {
            World.sOnWorldLoadFinishedEventHandler += new EventHandler(OnWorldLoaded);
            World.sOnWorldQuitEventHandler += new EventHandler(OnWorldQuit);
        }

        static void OnWorldLoaded(object sender, EventArgs e)
        {
            if (sGame == null)
            {
                NativeTransport net = new NativeTransport();
                sGame = new GameApi();
                if (!net.Available)
                {
                    sGame.Notify("Sims3MP: the native part is not installed (MonoPatcher + Sims3MPNative.dll). Multiplayer is disabled.");
                    sGame = null;
                    return;
                }
                sSession = new Session(net, sGame);
                sMenu = new Menu(sSession, sGame);
            }
            sGame.WorldLoaded = true;

            WindowBase scene = UIManager.GetSceneWindow();
            scene.Tick -= OnTick;
            scene.Tick += OnTick;
            scene.KeyDown -= OnKeyDown;
            scene.KeyDown += OnKeyDown;
            if (!sCommand)
            {
                sCommand = true; // cheat console fallback for F9: "sims3mp"
                CommandSystem.RegisterCommand("sims3mp", "Opens the Sims3MP multiplayer menu.", new CommandHandler(OnCommand));
            }
            sGame.Notify("Sims3MP loaded. Press F9 for multiplayer.");
        }

        static void OnWorldQuit(object sender, EventArgs e)
        {
            if (sGame != null) sGame.WorldLoaded = false;
        }

        // Runs every rendered frame, also while the simulation is paused.
        static void OnTick(WindowBase sender, UIEventArgs e)
        {
            try
            {
                sSession.Update();
                sGame.Tick();
            }
            catch (Exception ex)
            {
                if (!sErrorShown) { sErrorShown = true; sGame.Notify("Sims3MP error: " + ex); }
            }
        }

        static void OnKeyDown(WindowBase sender, UIKeyEventArgs e)
        {
            if ((uint)e.KeyCode == 0x78) OpenMenu(); // VK_F9
        }

        static int OnCommand(object[] parameters) { OpenMenu(); return 1; }

        static void OpenMenu()
        {
            if (sGame != null) sGame.RunTask(sMenu.Open); // dialogs must run in a simulator task
        }
    }

    public sealed class GameApi : IGame
    {
        public bool WorldLoaded;

        readonly Dictionary<ulong, Sim> mSimCache = new Dictionary<ulong, Sim>();
        readonly Dictionary<ulong, bool> mRemote = new Dictionary<ulong, bool>();
        bool mRestrict;
        GameObject[] mScan;
        int mScanPos;
        long mNextScan;

        // --- UI ----------------------------------------------------------

        public void Notify(string text)
        {
            StyledNotification.Show(new StyledNotification.Format(text, StyledNotification.NotificationStyle.kSystemMessage));
        }
        public void RunTask(VoidHandler fn) { Simulator.AddObject(new OneShotFunctionTask(new Function(fn.Invoke))); }
        public void Message(string title, string text) { SimpleMessageDialog.Show(title, text); }
        public string Ask(string title, string prompt, string initial) { return StringInputDialog.Show(title, prompt, initial ?? ""); }
        public bool Confirm(string text) { return TwoButtonDialog.Show(text, "Yes", "No"); }

        // --- world -------------------------------------------------------

        public bool InWorld { get { return WorldLoaded; } }

        // No confirmed script API for the current save's name; the menu offers
        // "Enter the session" so the player confirms it instead.
        public string SaveName { get { return ""; } }

        public int Speed
        {
            get
            {
                switch (Gameflow.CurrentGameSpeed)
                {
                    case Gameflow.GameSpeed.Pause: return 0;
                    case Gameflow.GameSpeed.Normal: return 1;
                    case Gameflow.GameSpeed.Double: return 2;
                    default: return 3;
                }
            }
            set
            {
                Gameflow.GameSpeed s = value <= 0 ? Gameflow.GameSpeed.Pause
                    : value == 1 ? Gameflow.GameSpeed.Normal
                    : value == 2 ? Gameflow.GameSpeed.Double : Gameflow.GameSpeed.Triple;
                if (Gameflow.CurrentGameSpeed != s) Gameflow.SetGameSpeed(s, Gameflow.SetGameSpeedContext.GameStates);
            }
        }

        // --- households --------------------------------------------------

        public ulong ActiveHousehold
        {
            get { Household h = Household.ActiveHousehold; return h == null ? 0UL : h.HouseholdId; }
        }

        public ulong HomeLot
        {
            get
            {
                Household h = Household.ActiveHousehold;
                return h == null || h.LotHome == null ? 0UL : h.LotHome.LotId;
            }
        }

        public bool AllActiveSimsAsleep
        {
            get
            {
                Household h = Household.ActiveHousehold;
                if (h == null) return false;
                int n = 0;
                foreach (Sim sim in h.Sims)
                {
                    if (sim == null) continue;
                    if (!sim.IsSleeping) return false;
                    n++;
                }
                return n > 0;
            }
        }

        public List<NamedId> Households()
        {
            List<NamedId> list = new List<NamedId>();
            foreach (Household h in Household.sHouseholdList)
            {
                if (h == null || h.IsSpecialHousehold || h.LotHome == null || h.Sims.Count == 0) continue;
                list.Add(new NamedId(h.HouseholdId, h.Name));
            }
            return list;
        }

        public bool SwitchHousehold(ulong householdId)
        {
            foreach (Household h in Household.sHouseholdList)
            {
                if (h == null || h.HouseholdId != householdId) continue;
                foreach (Sim sim in h.Sims)
                {
                    if (sim != null) { Release(sim); return PlumbBob.ForceSelectActor(sim); }
                }
            }
            return false;
        }

        // --- lots --------------------------------------------------------

        public bool InBuildBuy { get { return GameStates.IsBuildBuyLikeState; } }

        public ulong BuildLot
        {
            get
            {
                Lot lot = LotManager.GetLotAtPoint(CameraController.GetTarget());
                return lot == null || lot.IsWorldLot ? 0UL : lot.LotId;
            }
        }

        public void ExitBuildBuy()
        {
            InWorldState state = GameStates.GetInWorldState();
            if (state != null) state.GotoLiveMode();
        }

        public string LotName(ulong id)
        {
            Lot lot = LotManager.GetLot(id);
            if (lot == null) return "Lot";
            return string.IsNullOrEmpty(lot.Name) ? lot.Address : lot.Name;
        }

        // --- sims --------------------------------------------------------

        public void ControlledSims(List<SimState> into)
        {
            Household h = Household.ActiveHousehold;
            if (h == null) return;
            foreach (Sim sim in h.Sims)
            {
                if (sim == null || sim.HasBeenDestroyed || sim.SimDescription == null) continue;
                SimState s = new SimState();
                s.SimId = sim.SimDescription.SimDescriptionId;
                Vector3 p = sim.Position, f = sim.ForwardVector;
                s.X = p.x; s.Y = p.y; s.Z = p.z; s.FwdX = f.x; s.FwdZ = f.z;
                s.Interaction = "";
                InteractionInstance cur = sim.CurrentInteraction;
                if (cur != null && cur.InteractionDefinition != null)
                {
                    Type t = cur.InteractionDefinition.GetType();
                    s.Interaction = t.FullName + ", " + t.Assembly.GetName().Name;
                    if (cur.Target != null) s.Target = cur.Target.ObjectId.Value;
                }
                into.Add(s);
            }
        }

        Sim FindSim(ulong simId)
        {
            Sim sim;
            if (mSimCache.TryGetValue(simId, out sim) && sim != null && !sim.HasBeenDestroyed) return sim;
            SimDescription desc = SimDescription.Find(simId);
            sim = desc == null ? null : desc.CreatedSim;
            if (sim != null) mSimCache[simId] = sim;
            return sim;
        }

        // Sims played by someone else must not make their own decisions here.
        void Hold(ulong simId, Sim sim)
        {
            if (mRemote.ContainsKey(simId)) return;
            mRemote[simId] = true;
            if (sim.Autonomy != null) sim.Autonomy.IncrementAutonomyDisabled();
        }

        void Release(Sim sim)
        {
            ulong id = sim.SimDescription.SimDescriptionId;
            if (!mRemote.ContainsKey(id)) return;
            mRemote.Remove(id);
            if (sim.Autonomy != null) sim.Autonomy.DecrementAutonomyDisabled();
        }

        public void ApplyRemoteSim(ulong simId, float x, float y, float z, float fwdX, float fwdZ)
        {
            Sim sim = FindSim(simId);
            if (sim == null) return;
            Hold(simId, sim);
            // The sim normally gets where it should by running the replicated
            // interaction locally; only correct it when it has clearly diverged.
            Vector3 p = sim.Position;
            float dx = p.x - x, dy = p.y - y, dz = p.z - z;
            if (dx * dx + dy * dy + dz * dz < 9f) return;
            sim.SetPosition(new Vector3(x, y, z));
            sim.SetForward(new Vector3(fwdX, 0f, fwdZ));
        }

        public void PushInteraction(ulong simId, string definitionType, ulong targetId)
        {
            Sim sim = FindSim(simId);
            if (sim == null || sim.InteractionQueue == null) return;
            Hold(simId, sim);

            // The type name comes from another player: accept interaction
            // definitions only, before touching anything on the type.
            Type type = Type.GetType(definitionType, false);
            if (type == null || !typeof(InteractionDefinition).IsAssignableFrom(type)) return;
            InteractionDefinition def = null;
            FieldInfo singleton = type.GetField("Singleton", BindingFlags.Public | BindingFlags.Static);
            if (singleton != null) def = singleton.GetValue(null) as InteractionDefinition;
            if (def == null && !type.IsAbstract && type.GetConstructor(Type.EmptyTypes) != null)
                def = Activator.CreateInstance(type) as InteractionDefinition;
            if (def == null) return;

            IGameObject target = sim;
            if (targetId != 0 && targetId != sim.ObjectId.Value)
            {
                target = GameObject.GetObject<GameObject>(new ObjectGuid(targetId));
                if (target == null) return; // object does not exist in this copy of the world
            }
            InteractionInstance inst = def.CreateInstance(target, sim,
                new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true);
            if (inst == null) return;
            sim.InteractionQueue.CancelAllInteractions();
            sim.InteractionQueue.Add(inst);
        }

        // --- needs and money ---------------------------------------------

        static readonly CommodityKind[] kMotives = new CommodityKind[] {
            CommodityKind.Hunger, CommodityKind.Energy, CommodityKind.Bladder,
            CommodityKind.Hygiene, CommodityKind.Social, CommodityKind.Fun };

        static Household FindHousehold(ulong id)
        {
            foreach (Household h in Household.sHouseholdList)
                if (h != null && h.HouseholdId == id) return h;
            return null;
        }

        public int GetFunds(ulong household)
        {
            Household h = FindHousehold(household);
            return h == null ? 0 : h.FamilyFunds;
        }

        public void SetFunds(ulong household, int funds)
        {
            Household h = FindHousehold(household);
            if (h != null && h.FamilyFunds != funds) h.SetFamilyFunds(funds);
        }

        public bool ReadMotives(ulong simId, float[] six)
        {
            Sim sim = FindSim(simId);
            if (sim == null || sim.Motives == null) return false;
            for (int i = 0; i < 6; i++) six[i] = sim.Motives.GetValue(kMotives[i]);
            return true;
        }

        public void ApplyMotives(ulong simId, float[] six)
        {
            Sim sim = FindSim(simId);
            if (sim == null || sim.Motives == null) return;
            for (int i = 0; i < 6; i++) sim.Motives.SetValue(kMotives[i], six[i]);
        }

        // --- skills, careers, moodlets, relationships --------------------
        //
        // The owner's game is the authority; these write the owner's values
        // into this copy of the sim. Formats are private to this file.

        public bool ReadSimData(ulong simId, byte kind, Buf into)
        {
            Sim sim = FindSim(simId);
            if (sim == null) return false;
            switch (kind)
            {
                case Msg.SlotSkills:
                {
                    if (sim.SkillManager == null) return false;
                    int at = into.Length, n = 0;
                    into.U16(0);
                    foreach (Skill skill in sim.SkillManager.List.Values)
                    {
                        if (skill == null || n == 0xFFFF) continue;
                        into.U64((ulong)skill.Guid).U32((uint)skill.SkillLevel).F32(skill.SkillPoints);
                        n++;
                    }
                    into.Data[at] = (byte)n; into.Data[at + 1] = (byte)(n >> 8);
                    return true;
                }
                case Msg.SlotCareer:
                {
                    Occupation job = sim.Occupation;
                    into.U64(job == null ? 0UL : (ulong)job.Guid).U32(job == null ? 0u : (uint)job.CareerLevel);
                    return true;
                }
                case Msg.SlotMoodlets:
                {
                    if (sim.BuffManager == null) return false;
                    int at = into.Length, n = 0;
                    into.U16(0);
                    foreach (BuffInstance buff in sim.BuffManager.List)
                    {
                        if (buff == null || n == 0xFFFF) continue;
                        into.U64((ulong)buff.Guid);
                        n++;
                    }
                    into.Data[at] = (byte)n; into.Data[at + 1] = (byte)(n >> 8);
                    return true;
                }
                case Msg.SlotRelationships:
                {
                    int at = into.Length, n = 0;
                    into.U16(0);
                    foreach (Relationship rel in Relationship.GetRelationships(sim.SimDescription))
                    {
                        if (rel == null || rel.LTR == null || n == 600) continue;
                        SimDescription other = rel.GetOtherSimDescription(sim.SimDescription);
                        if (other == null) continue;
                        // A relationship has two ends. When the other end is a sim played
                        // by someone else too, only the lower id publishes it, so the two
                        // owners never fight over the same value.
                        ulong otherId = other.SimDescriptionId;
                        if (simId > otherId && (mRemote.ContainsKey(otherId) || other.Household == sim.Household)) continue;
                        into.U64(otherId).F32(rel.LTR.Liking).U32((uint)rel.LTR.CurrentLTR);
                        n++;
                    }
                    into.Data[at] = (byte)n; into.Data[at + 1] = (byte)(n >> 8);
                    return true;
                }
            }
            return false;
        }

        public void ApplySimData(ulong simId, byte kind, Rd from)
        {
            Sim sim = FindSim(simId);
            if (sim == null) return;
            switch (kind)
            {
                case Msg.SlotSkills:
                {
                    if (sim.SkillManager == null) return;
                    int n = from.U16();
                    for (int i = 0; i < n; i++)
                    {
                        SkillNames guid = (SkillNames)from.U64(); int level = (int)from.U32(); float points = from.F32();
                        if (from.Err) return;
                        Skill skill = sim.SkillManager.GetElement(guid);
                        if (skill == null) skill = sim.SkillManager.AddElement(guid);
                        if (skill == null) continue;
                        if (skill.SkillLevel != level) skill.ForceSkillLevelUp(level);
                        skill.SkillPoints = points;
                    }
                    break;
                }
                case Msg.SlotCareer:
                {
                    OccupationNames guid = (OccupationNames)from.U64(); int level = (int)from.U32();
                    if (from.Err) return;
                    Occupation job = sim.Occupation;
                    if (job != null && (guid == OccupationNames.Undefined || job.Guid != guid))
                    {
                        job.LeaveJobNow(Career.LeaveJobReason.kQuit);
                        job = null;
                    }
                    if (guid == OccupationNames.Undefined) return;
                    if (job == null)
                    {
                        sim.AcquireOccupation(new AcquireOccupationParameters(guid, false, true));
                        job = sim.Occupation;
                    }
                    // Bounded: a bad level from the network must not loop forever.
                    for (int i = 0; job != null && job.CareerLevel < level && i < 12; i++) job.PromoteSim();
                    for (int i = 0; job != null && job.CareerLevel > level && i < 12; i++) job.DemoteSim();
                    break;
                }
                case Msg.SlotMoodlets:
                {
                    if (sim.BuffManager == null) return;
                    int n = from.U16();
                    Dictionary<ulong, bool> want = new Dictionary<ulong, bool>();
                    for (int i = 0; i < n; i++) want[from.U64()] = true;
                    if (from.Err) return;
                    List<BuffNames> remove = new List<BuffNames>();
                    foreach (BuffInstance buff in sim.BuffManager.List)
                    {
                        if (buff == null) continue;
                        if (!want.Remove((ulong)buff.Guid)) remove.Add(buff.Guid);
                    }
                    foreach (BuffNames guid in remove) sim.BuffManager.RemoveElement(guid);
                    foreach (ulong guid in want.Keys) sim.BuffManager.AddElement((BuffNames)guid, Origin.None);
                    break;
                }
                case Msg.SlotRelationships:
                {
                    int n = from.U16();
                    for (int i = 0; i < n; i++)
                    {
                        ulong otherId = from.U64(); float liking = from.F32(); uint state = from.U32();
                        if (from.Err) return;
                        SimDescription other = SimDescription.Find(otherId);
                        if (other == null) continue;
                        Relationship rel = Relationship.Get(sim.SimDescription, other, true);
                        if (rel == null || rel.LTR == null) continue;
                        if (rel.LTR.Liking != liking) rel.LTR.SetLiking(liking);
                        if ((uint)rel.LTR.CurrentLTR != state) rel.LTR.ForceChangeState((LongTermRelationshipTypes)state);
                    }
                    break;
                }
            }
        }

        // --- lot structure and object design -----------------------------
        //
        // NOT IMPLEMENTED on the game side. Everything around these hooks is
        // done and tested (change detection, permissions, transport, storage,
        // replay to late joiners): a lot's walls, floors, roofs, terrain and
        // coverings are one blob, an object's colours/patterns are
        // ObjState.Design. What is missing is reading them from and writing
        // them to the game, and the script functions for that could not be
        // confirmed without the game's assemblies. They are deliberately left
        // empty rather than filled with guessed function names.

        public bool ReadLotStructure(ulong lot, Buf into) { return false; }
        public void ApplyLotStructure(ulong lot, Rd from) { }
        static byte[] ReadDesign(GameObject obj) { return null; }
        static void ApplyDesign(GameObject obj, byte[] design) { }

        // --- build / buy -------------------------------------------------

        public void LotObjects(ulong lotId, List<ObjState> into)
        {
            Lot lot = LotManager.GetLot(lotId);
            if (lot == null) return;
            foreach (GameObject obj in lot.GetObjects<GameObject>())
            {
                // Sims, and things carried or stored inside other things, are not furniture.
                if (obj == null || obj.HasBeenDestroyed || obj is Sim || obj.Parent != null || !obj.InWorld) continue;
                ResourceKey key = obj.GetResourceKey();
                if (key.InstanceId == 0) continue;
                ObjState s = new ObjState();
                s.Id = obj.ObjectId.Value;
                s.KeyType = key.TypeId; s.KeyGroup = key.GroupId; s.KeyInstance = key.InstanceId;
                Vector3 p = obj.Position, f = obj.ForwardVector;
                s.X = p.x; s.Y = p.y; s.Z = p.z; s.FwdX = f.x; s.FwdZ = f.z;
                s.Level = obj.Level;
                s.Design = ReadDesign(obj);
                into.Add(s);
            }
        }

        public ulong CreateObject(ulong lot, ObjState s)
        {
            ResourceKey key = new ResourceKey(s.KeyInstance, s.KeyType, s.KeyGroup);
            GameObject obj = GlobalFunctions.CreateObjectOutOfWorld(key) as GameObject;
            if (obj == null) return 0;
            obj.SetPosition(new Vector3(s.X, s.Y, s.Z));
            obj.SetForward(new Vector3(s.FwdX, 0f, s.FwdZ));
            obj.AddToWorld();
            if (s.Design != null) ApplyDesign(obj, s.Design);
            return obj.ObjectId.Value;
        }

        public void MoveObject(ulong id, ObjState s)
        {
            GameObject obj = GameObject.GetObject<GameObject>(new ObjectGuid(id));
            if (obj == null || obj is Sim) return;
            obj.SetPosition(new Vector3(s.X, s.Y, s.Z));
            obj.SetForward(new Vector3(s.FwdX, 0f, s.FwdZ));
            if (s.Design != null) ApplyDesign(obj, s.Design);
        }

        public void DestroyObject(ulong id)
        {
            GameObject obj = GameObject.GetObject<GameObject>(new ObjectGuid(id));
            if (obj != null && !(obj is Sim)) obj.Destroy();
        }

        public void Listener(out float x, out float y, out float z, out float fwdX, out float fwdZ)
        {
            // Voice is heard from where the player is looking, i.e. the camera.
            Vector3 p = CameraController.GetPosition(), t = CameraController.GetTarget();
            x = t.x; y = t.y; z = t.z;
            fwdX = t.x - p.x; fwdZ = t.z - p.z;
        }

        // --- admin-only mods ---------------------------------------------

        public void RestrictAdminMods(bool restrict)
        {
            // Hidden interactions come back on the next world load, which is
            // also the only time a player's admin status can change.
            mRestrict = restrict;
            mScan = null;
            mNextScan = 0;
        }

        /// <summary>Per-frame housekeeping with a strict budget (no hitches).</summary>
        public void Tick()
        {
            if (!mRestrict || !WorldLoaded) return;
            if (mScan == null)
            {
                long now = DateTime.UtcNow.Ticks / 10000;
                if (now < mNextScan) return;
                mNextScan = now + 30000;
                mScan = Queries.GetObjects<GameObject>();
                mScanPos = 0;
            }
            for (int i = 0; i < 150 && mScanPos < mScan.Length; i++, mScanPos++)
            {
                GameObject obj = mScan[mScanPos];
                if (obj == null || obj.Interactions == null) continue;
                List<InteractionObjectPair> list = obj.Interactions;
                for (int k = list.Count - 1; k >= 0; k--)
                {
                    InteractionDefinition def = list[k] == null ? null : list[k].InteractionDefinition;
                    string ns = def == null ? null : def.GetType().Namespace;
                    if (ns != null && ns.StartsWith("NRaas")) list.RemoveAt(k);
                }
            }
            if (mScanPos >= mScan.Length) mScan = null;
        }
    }
}
