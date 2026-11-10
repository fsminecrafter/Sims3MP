// Everything Sims3MP needs from the game, behind one interface. Session and
// Menu contain no Sims 3 types, so they can be compiled and tested without
// the game; GameApi.cs is the only file that touches the game's assemblies.
using System.Collections.Generic;

namespace Sims3MP
{
    public delegate void VoidHandler();

    public struct SimState
    {
        public ulong SimId;          // SimDescription id: identical on every client of the same save
        public float X, Y, Z, FwdX, FwdZ;
        public string Interaction;   // type name of the running interaction's definition, or ""
        public ulong Target;         // object id of that interaction's target
    }

    /// <summary>An object placed on a lot (furniture, doors, plants...).</summary>
    public struct ObjState
    {
        public ulong Id;             // this client's object id
        public uint KeyType, KeyGroup;
        public ulong KeyInstance;    // catalog resource the object is made from
        public float X, Y, Z, FwdX, FwdZ;
        public int Level;

        public bool Same(ObjState o)
        {
            return X == o.X && Y == o.Y && Z == o.Z && FwdX == o.FwdX && FwdZ == o.FwdZ && Level == o.Level;
        }
    }

    public struct NamedId
    {
        public ulong Id;
        public string Name;
        public NamedId(ulong id, string name) { Id = id; Name = name; }
    }

    public interface IGame
    {
        // UI. Notify never blocks. The three dialogs block and may only be
        // called from code started through RunTask.
        void Notify(string text);
        void RunTask(VoidHandler fn);
        void Message(string title, string text);
        string Ask(string title, string prompt, string initial); // null = cancelled
        bool Confirm(string text);

        bool InWorld { get; }
        string SaveName { get; }      // name of the loaded save, "" if unknown

        int Speed { get; set; }       // 0 pause, 1..3

        ulong ActiveHousehold { get; }
        ulong HomeLot { get; }        // lot the active household lives on, 0 if none
        bool AllActiveSimsAsleep { get; }
        List<NamedId> Households();
        bool SwitchHousehold(ulong householdId);

        bool InBuildBuy { get; }
        ulong BuildLot { get; }       // lot being edited / looked at
        void ExitBuildBuy();
        string LotName(ulong lot);

        void ControlledSims(List<SimState> into);
        void ApplyRemoteSim(ulong simId, float x, float y, float z, float fwdX, float fwdZ);
        void PushInteraction(ulong simId, string definitionType, ulong target);

        // Needs and money. Motives are always these six, in this order:
        // hunger, energy, bladder, hygiene, social, fun.
        int GetFunds(ulong household);
        void SetFunds(ulong household, int funds);
        bool ReadMotives(ulong simId, float[] six);
        void ApplyMotives(ulong simId, float[] six);

        // Build/buy. Ids are local to this client.
        void LotObjects(ulong lot, List<ObjState> into);
        ulong CreateObject(ulong lot, ObjState state); // 0 = failed
        void MoveObject(ulong id, ObjState state);
        void DestroyObject(ulong id);

        void Listener(out float x, out float y, out float z, out float fwdX, out float fwdZ);

        // While true, interactions added by admin-only mods (NRaas) are hidden.
        void RestrictAdminMods(bool restrict);
    }
}
