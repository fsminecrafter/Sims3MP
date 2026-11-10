// NOT part of the mod. Minimal declarations of the game API surface that
// mod/src/GameApi.cs assumes, so the mod can be compile-checked on machines
// without The Sims 3. Written from memory of the public script API: treat
// every line as "to be verified against the real assemblies". Never ship it.
using System;
using System.Collections.Generic;

namespace Sims3.SimIFace
{
    public class TunableAttribute : Attribute { }
    public struct Vector3 { public float x, y, z; public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; } }
    public struct ResourceKey
    {
        public ulong InstanceId; public uint TypeId, GroupId;
        public ResourceKey(ulong instance, uint type, uint group) { InstanceId = instance; TypeId = type; GroupId = group; }
    }
    public struct ObjectGuid { public ulong Value; public ObjectGuid(ulong v) { Value = v; } }
    public static class World
    {
        public static EventHandler sOnWorldLoadFinishedEventHandler;
        public static EventHandler sOnWorldQuitEventHandler;
    }
    public abstract class Task { public virtual void Simulate() { } }
    public static class Simulator { public static ObjectGuid AddObject(Task t) { return new ObjectGuid(); } }
    public delegate int CommandHandler(object[] parameters);
    public static class CommandSystem { public static void RegisterCommand(string name, string help, CommandHandler h) { } }
    public static class CameraController
    {
        public static Vector3 GetPosition() { return new Vector3(); }
        public static Vector3 GetTarget() { return new Vector3(); }
    }
}

namespace Sims3.UI
{
    public class UIEventArgs : EventArgs { }
    public class UIKeyEventArgs : UIEventArgs { public uint KeyCode; }
    public delegate void UIEventHandler<T>(WindowBase sender, T args) where T : UIEventArgs;
    public class WindowBase
    {
        public event UIEventHandler<UIEventArgs> Tick;
        public event UIEventHandler<UIKeyEventArgs> KeyDown;
        protected void Raise() { if (Tick != null) Tick(this, null); if (KeyDown != null) KeyDown(this, null); }
    }
    public static class UIManager { public static WindowBase GetSceneWindow() { return new WindowBase(); } }
    public static class SimpleMessageDialog { public static void Show(string title, string message) { } }
    public static class StringInputDialog { public static string Show(string title, string prompt, string text) { return ""; } }
    public static class TwoButtonDialog { public static bool Show(string message, string yes, string no) { return false; } }
    public class StyledNotification
    {
        public enum NotificationStyle { kSystemMessage }
        public class Format { public Format(string text, NotificationStyle style) { } }
        public static void Show(Format f) { }
    }
}

namespace Sims3.Gameplay
{
    public delegate void Function();
    public class OneShotFunctionTask : Sims3.SimIFace.Task { public OneShotFunctionTask(Function f) { } }
    public static class Gameflow
    {
        public enum GameSpeed { Pause, Normal, Double, Triple, Skip }
        public enum SetGameSpeedContext { GameStates }
        public static GameSpeed CurrentGameSpeed { get { return GameSpeed.Normal; } }
        public static void SetGameSpeed(GameSpeed s, SetGameSpeedContext c) { }
    }
    public class InWorldState { public void GotoLiveMode() { } }
    public static class GameStates
    {
        public static bool IsBuildBuyLikeState { get { return false; } }
        public static InWorldState GetInWorldState() { return null; }
    }
    public static class Queries { public static T[] GetObjects<T>() { return new T[0]; } }
}

namespace Sims3.Gameplay.Interactions
{
    using Sims3.Gameplay.Abstracts;
    public interface IGameObject { Sims3.SimIFace.ObjectGuid ObjectId { get; } }
    public interface IActor : IGameObject { }
    public enum InteractionPriorityLevel { UserDirected }
    public struct InteractionPriority { public InteractionPriority(InteractionPriorityLevel l) { } }
    public interface InteractionDefinition
    {
        InteractionInstance CreateInstance(IGameObject target, IActor actor, InteractionPriority priority, bool isAutonomous, bool cancellableByPlayer);
    }
    public class InteractionInstance { public InteractionDefinition InteractionDefinition; public IGameObject Target; }
    public class InteractionObjectPair { public InteractionDefinition InteractionDefinition; }
    public class InteractionQueue { public void CancelAllInteractions() { } public bool Add(InteractionInstance i) { return true; } }
}

namespace Sims3.Gameplay.Abstracts
{
    using Sims3.Gameplay.Interactions;
    using Sims3.SimIFace;
    public class GameObject : IGameObject
    {
        public ObjectGuid ObjectId { get { return new ObjectGuid(); } }
        public Vector3 Position { get { return new Vector3(); } }
        public Vector3 ForwardVector { get { return new Vector3(); } }
        public bool HasBeenDestroyed { get { return false; } }
        public bool InWorld { get { return true; } }
        public IGameObject Parent { get { return null; } }
        public int Level { get { return 0; } }
        public ResourceKey GetResourceKey() { return new ResourceKey(); }
        public void AddToWorld() { }
        public void Destroy() { }
        public List<InteractionObjectPair> Interactions;
        public void SetPosition(Vector3 p) { }
        public void SetForward(Vector3 f) { }
        public static T GetObject<T>(ObjectGuid id) where T : class { return null; }
    }
}

namespace Sims3.Gameplay.Autonomy
{
    public class Autonomy { public void IncrementAutonomyDisabled() { } public void DecrementAutonomyDisabled() { } }
    public enum CommodityKind { Hunger, Energy, Bladder, Hygiene, Social, Fun }
    public class Motives
    {
        public float GetValue(CommodityKind k) { return 0f; }
        public void SetValue(CommodityKind k, float v) { }
    }
}

namespace Sims3.Gameplay.Actors
{
    using Sims3.Gameplay.Interactions;
    public class Sim : Sims3.Gameplay.Abstracts.GameObject, IActor
    {
        public Sims3.Gameplay.CAS.SimDescription SimDescription;
        public InteractionQueue InteractionQueue;
        public InteractionInstance CurrentInteraction { get { return null; } }
        public Sims3.Gameplay.Autonomy.Autonomy Autonomy;
        public bool IsSleeping { get { return false; } }
        public Sims3.Gameplay.Autonomy.Motives Motives;
        public Sims3.Gameplay.Skills.SkillManager SkillManager;
        public Sims3.Gameplay.ActorSystems.BuffManager BuffManager;
        public Sims3.Gameplay.Careers.Occupation Occupation;
        public Sims3.Gameplay.CAS.Household Household;
        public bool AcquireOccupation(Sims3.Gameplay.Careers.AcquireOccupationParameters p) { return true; }
    }
}

namespace Sims3.Gameplay.CAS
{
    using Sims3.Gameplay.Actors;
    public class SimDescription
    {
        public ulong SimDescriptionId;
        public Sim CreatedSim;
        public Household Household;
        public static SimDescription Find(ulong id) { return null; }
    }
    public class Household
    {
        public static Household ActiveHousehold { get { return null; } }
        public static List<Household> sHouseholdList = new List<Household>();
        public ulong HouseholdId;
        public string Name;
        public List<Sim> Sims = new List<Sim>();
        public Sims3.Gameplay.Core.Lot LotHome;
        public bool IsSpecialHousehold { get { return false; } }
        public int FamilyFunds { get { return 0; } }
        public void SetFamilyFunds(int funds) { }
    }
}

namespace Sims3.Gameplay.Core
{
    using Sims3.SimIFace;
    public class Lot { public ulong LotId; public string Name; public string Address; public bool IsWorldLot { get { return false; } } public T[] GetObjects<T>() { return new T[0]; } }
    public static class GlobalFunctions
    {
        public static Sims3.Gameplay.Interactions.IGameObject CreateObjectOutOfWorld(ResourceKey key) { return null; }
    }
    public static class LotManager
    {
        public static Lot GetLotAtPoint(Vector3 p) { return null; }
        public static Lot GetLot(ulong id) { return null; }
    }
    public static class PlumbBob { public static bool ForceSelectActor(Sims3.Gameplay.Actors.Sim s) { return true; } }
}

namespace Sims3.Gameplay.Utilities { }

namespace Sims3.Gameplay.Skills
{
    public enum SkillNames : ulong { None }
    public class Skill
    {
        public SkillNames Guid;
        public int SkillLevel;
        public float SkillPoints;
        public void ForceSkillLevelUp(int level) { }
    }
    public class SkillManager
    {
        public Dictionary<ulong, Skill> List = new Dictionary<ulong, Skill>();
        public Skill GetElement(SkillNames guid) { return null; }
        public Skill AddElement(SkillNames guid) { return null; }
    }
}

namespace Sims3.Gameplay.Careers
{
    public enum OccupationNames : ulong { Undefined }
    public class AcquireOccupationParameters { public AcquireOccupationParameters(OccupationNames guid, bool a, bool b) { } }
    public class Occupation
    {
        public OccupationNames Guid;
        public int CareerLevel;
        public void PromoteSim() { }
        public void DemoteSim() { }
        public void LeaveJobNow(Career.LeaveJobReason reason) { }
    }
    public class Career : Occupation { public enum LeaveJobReason { kQuit } }
}

namespace Sims3.Gameplay.ActorSystems
{
    public enum BuffNames : ulong { Undefined }
    public enum Origin : ulong { None }
    public class BuffInstance { public BuffNames Guid; }
    public class BuffManager
    {
        public List<BuffInstance> List = new List<BuffInstance>();
        public bool AddElement(BuffNames guid, Origin origin) { return true; }
        public void RemoveElement(BuffNames guid) { }
    }
}

namespace Sims3.Gameplay.Socializing
{
    using Sims3.Gameplay.CAS;
    public enum LongTermRelationshipTypes : uint { Undefined }
    public class LongTermRelationship
    {
        public float Liking;
        public LongTermRelationshipTypes CurrentLTR;
        public void SetLiking(float v) { }
        public void ForceChangeState(LongTermRelationshipTypes t) { }
    }
    public class Relationship
    {
        public LongTermRelationship LTR;
        public SimDescription GetOtherSimDescription(SimDescription me) { return null; }
        public static List<Relationship> GetRelationships(SimDescription sim) { return new List<Relationship>(); }
        public static Relationship Get(SimDescription a, SimDescription b, bool create) { return null; }
    }
}
