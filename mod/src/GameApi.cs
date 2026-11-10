// The only file that touches The Sims 3's own assemblies.
//
// It compiles against the real game assemblies (ScriptCore, SimIFace,
// Sims3GameplaySystems, Sims3GameplayObjects, UI), so every name and
// signature used here exists. It has not been run inside the game yet, so
// behaviour (as opposed to names) is still unverified.
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
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.UI;
using Sims3.UI.Controller;
using Flow = Sims3.Gameplay.Gameflow;
using GameSpeed = Sims3.SimIFace.Gameflow.GameSpeed;

namespace Sims3MP
{
    /// <summary>Entry point. The game instantiates this class because of the
    /// tunable field (the package needs a matching "Sims3MP.Main" tuning XML).</summary>
    public class Main
    {
        [Tunable] protected static bool kInstantiator = false;

        static GameApi sGame;
        static NativeTransport sNet;
        static Session sSession;
        static Menu sMenu;
        static bool sCommand, sErrorShown;

        static Main()
        {
            World.OnWorldLoadFinishedEventHandler += new EventHandler(OnWorldLoaded);
            World.OnWorldQuitEventHandler += new EventHandler(OnWorldQuit);
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
                sNet = net;
                sSession = new Session(net, sGame);
                sMenu = new Menu(sSession, sGame);
            }
            sGame.WorldLoaded = true;

            WindowBase scene = UIManager.GetSceneWindow();
            scene.Tick -= OnTick;
            scene.Tick += OnTick;
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
                // The game's UI only reports keys bound in its own hotkey maps,
                // so F9 is read by the native shim (only while the game has focus).
                if (sNet.MenuKeyPressed()) OpenMenu();
            }
            catch (Exception ex)
            {
                if (!sErrorShown) { sErrorShown = true; sGame.Notify("Sims3MP error: " + ex); }
            }
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
        public void RunTask(VoidHandler fn) { Simulator.AddObject(new Sims3.Gameplay.OneShotFunctionTask(new Sims3.Gameplay.Function(fn.Invoke))); }
        public void Message(string title, string text) { SimpleMessageDialog.Show(title, text); }
        public string Ask(string title, string prompt, string initial) { return StringInputDialog.Show(title, prompt, initial ?? ""); }
        public bool Confirm(string text) { return TwoButtonDialog.Show(text, "Yes", "No"); }

        // --- world -------------------------------------------------------

        public bool InWorld { get { return WorldLoaded; } }

        // The file the world was loaded from, e.g. "Town.sims3" -> "Town".
        public string SaveName
        {
            get
            {
                string name = GameStates.LoadFileName ?? "";
                int slash = Math.Max(name.LastIndexOf('\\'), name.LastIndexOf('/'));
                if (slash >= 0) name = name.Substring(slash + 1);
                if (name.EndsWith(".sims3", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 6);
                return name;
            }
        }

        public int Speed
        {
            get
            {
                switch (Flow.CurrentGameSpeed)
                {
                    case GameSpeed.Pause: return 0;
                    case GameSpeed.Normal: return 1;
                    case GameSpeed.Double: return 2;
                    default: return 3;
                }
            }
            set
            {
                GameSpeed s = value <= 0 ? GameSpeed.Pause
                    : value == 1 ? GameSpeed.Normal
                    : value == 2 ? GameSpeed.Double : GameSpeed.Triple;
                if (Flow.CurrentGameSpeed != s) Flow.SetGameSpeed(s, Flow.SetGameSpeedContext.GameStates);
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
            GameStates.TransitionToLiveMode();
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
                    foreach (Skill skill in sim.SkillManager.List)
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
                        job.LeaveJob(false, Career.LeaveJobReason.kQuit);
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
        // What the game lets a script read and write: terrain heights, the
        // auto-roof style, and an object's design preset. Walls, floors and
        // wall/floor coverings have no script function for creating or
        // changing them (only the interactive build tools do), so they are
        // not part of the blob and do not sync.

        const float kHeightStep = 1f / 64f; // heights are quantised so both ends produce identical bytes

        // Samples the lot on a one-unit grid between its four corners.
        static bool LotGrid(ulong lotId, out LotCorners c, out int nx, out int nz)
        {
            c = World.LotGetCorners(lotId);
            float dx = c.mCorner1.x - c.mCorner0.x, dz = c.mCorner1.z - c.mCorner0.z;
            float ex = c.mCorner3.x - c.mCorner0.x, ez = c.mCorner3.z - c.mCorner0.z;
            nx = (int)Math.Round(Math.Sqrt(dx * dx + dz * dz));
            nz = (int)Math.Round(Math.Sqrt(ex * ex + ez * ez));
            return nx >= 1 && nz >= 1 && nx <= 128 && nz <= 128;
        }

        static void GridPoint(LotCorners c, int nx, int nz, int i, int k, out float x, out float z)
        {
            float u = (float)i / nx, v = (float)k / nz;
            x = c.mCorner0.x + (c.mCorner1.x - c.mCorner0.x) * u + (c.mCorner3.x - c.mCorner0.x) * v;
            z = c.mCorner0.z + (c.mCorner1.z - c.mCorner0.z) * u + (c.mCorner3.z - c.mCorner0.z) * v;
        }

        public bool ReadLotStructure(ulong lot, Buf into)
        {
            LotCorners c; int nx, nz;
            if (LotManager.GetLot(lot) == null || !LotGrid(lot, out c, out nx, out nz)) return false;
            ResourceKey roof = World.LotGetAutoRoofStyle(lot);
            into.U8(1).U64(roof.InstanceId).U32(roof.TypeId).U32(roof.GroupId).U8((byte)nx).U8((byte)nz);
            for (int k = 0; k <= nz; k++)
                for (int i = 0; i <= nx; i++)
                {
                    float x, z;
                    GridPoint(c, nx, nz, i, k, out x, out z);
                    into.U32((uint)(int)Math.Round(World.GetTerrainHeight(x, z) / kHeightStep));
                }
            return true;
        }

        public void ApplyLotStructure(ulong lot, Rd from)
        {
            LotCorners c; int nx, nz;
            if (LotManager.GetLot(lot) == null || !LotGrid(lot, out c, out nx, out nz)) return;
            if (from.U8() != 1) return;
            ResourceKey roof = new ResourceKey(from.U64(), from.U32(), from.U32());
            if (from.U8() != nx || from.U8() != nz || from.Left < (nx + 1) * (nz + 1) * 4) return; // not the same lot shape
            for (int k = 0; k <= nz; k++)
                for (int i = 0; i <= nx; i++)
                {
                    int want = (int)from.U32();
                    float x, z;
                    GridPoint(c, nx, nz, i, k, out x, out z);
                    if ((int)Math.Round(World.GetTerrainHeight(x, z) / kHeightStep) != want)
                        World.LotSetTerrainHeight(lot, x, z, want * kHeightStep);
                }
            if (roof.InstanceId != 0 && !roof.Equals(World.LotGetAutoRoofStyle(lot)))
            {
                World.LotSetAutoRoofStyle(lot, roof);
                if (World.LotGetIsAutoRoofEnabled(lot)) World.LotDoAutoRoof(lot);
            }
        }

        // An object's colours and patterns are its design preset, an XML string.
        static byte[] ReadDesign(GameObject obj)
        {
            string preset = ObjectDesigner.GetObjectDesignPreset(obj.ObjectId);
            if (string.IsNullOrEmpty(preset)) return null;
            byte[] data = System.Text.Encoding.UTF8.GetBytes(preset);
            return data.Length > 60000 ? null : data;
        }

        static void ApplyDesign(GameObject obj, byte[] design)
        {
            string preset = System.Text.Encoding.UTF8.GetString(design, 0, design.Length);
            if (preset != ObjectDesigner.GetObjectDesignPreset(obj.ObjectId))
                ObjectDesigner.SetPresetInfo(obj.ObjectId, preset);
        }

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
                mScan = Sims3.Gameplay.Queries.GetObjects<GameObject>();
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
