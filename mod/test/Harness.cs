// Runs the mod's real Session and Menu code against real bridges and a real
// server, with a scripted fake game. See scripts/test-mod.sh.
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using Sims3MP;

sealed class SocketTransport : ITransport
{
    readonly Socket mSock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    readonly List<byte> mIn = new List<byte>();
    public SocketTransport(int port) { mSock.Connect("127.0.0.1", port); mSock.NoDelay = true; }
    public bool Poll() { return true; }
    public bool Send(byte[] body, int len)
    {
        mSock.Send(BitConverter.GetBytes((uint)len));
        mSock.Send(body, len, SocketFlags.None);
        return true;
    }
    public int Recv(byte[] buffer)
    {
        byte[] tmp = new byte[65536];
        while (mSock.Available > 0) { int n = mSock.Receive(tmp); for (int i = 0; i < n; i++) mIn.Add(tmp[i]); }
        if (mIn.Count < 4) return 0;
        int len = BitConverter.ToInt32(mIn.GetRange(0, 4).ToArray(), 0);
        if (mIn.Count < 4 + len) return 0;
        mIn.CopyTo(4, buffer, 0, len);
        mIn.RemoveRange(0, 4 + len);
        return len;
    }
}

sealed class FakeGame : IGame
{
    public string Who;
    public bool World, Asleep, BuildMode;
    public string Save = "";
    public int SpeedNow = 1, Exits;
    public ulong House, Home, CameraLot;
    public List<SimState> Sims = new List<SimState>();
    public Dictionary<ulong, float> RemoteX = new Dictionary<ulong, float>();
    public List<string> Pushed = new List<string>(), Notes = new List<string>();
    public Queue<string> Answers = new Queue<string>();
    public bool Restricted;

    public void Notify(string text) { Notes.Add(text); Console.WriteLine("  [" + Who + "] " + text); }
    public void RunTask(VoidHandler fn) { fn(); }
    public void Message(string title, string text) { Notify(text); }
    public string Ask(string title, string prompt, string initial) { return Answers.Count > 0 ? Answers.Dequeue() : null; }
    public bool Confirm(string text) { return true; }
    public bool InWorld { get { return World; } }
    public string SaveName { get { return Save; } }
    public int Speed { get { return SpeedNow; } set { SpeedNow = value; } }
    public ulong ActiveHousehold { get { return House; } }
    public ulong HomeLot { get { return Home; } }
    public bool AllActiveSimsAsleep { get { return Asleep; } }
    public List<NamedId> Households() { return new List<NamedId>(); }
    public bool SwitchHousehold(ulong id) { House = id; return true; }
    public bool InBuildBuy { get { return BuildMode; } }
    public ulong BuildLot { get { return CameraLot; } }
    public void ExitBuildBuy() { Exits++; BuildMode = false; }
    public string LotName(ulong lot) { return "Lot " + lot; }
    public void ControlledSims(List<SimState> into) { into.AddRange(Sims); }
    public void ApplyRemoteSim(ulong id, float x, float y, float z, float fx, float fz) { RemoteX[id] = x; }
    public void PushInteraction(ulong sim, string def, ulong target) { Pushed.Add(sim + ":" + def + ":" + target); }
    public Dictionary<ulong, int> Funds = new Dictionary<ulong, int>();
    public Dictionary<ulong, float[]> Motives = new Dictionary<ulong, float[]>();
    public List<ObjState> Objects = new List<ObjState>(); // everything is on lot 77
    public ulong NextId = 1000; // the same on every client, as in the real game after loading one save

    public int GetFunds(ulong h) { int v; return Funds.TryGetValue(h, out v) ? v : 0; }
    public void SetFunds(ulong h, int funds) { Funds[h] = funds; }
    public bool ReadMotives(ulong sim, float[] six)
    {
        float[] m;
        if (!Motives.TryGetValue(sim, out m)) return false;
        Array.Copy(m, six, 6);
        return true;
    }
    public void ApplyMotives(ulong sim, float[] six) { Motives[sim] = (float[])six.Clone(); }
    public void LotObjects(ulong lot, List<ObjState> into) { if (lot == 77) into.AddRange(Objects); }
    public ulong CreateObject(ulong lot, ObjState o) { o.Id = NextId++; Objects.Add(o); return o.Id; }
    public ulong Place(ulong key, float x) { ObjState o = new ObjState(); o.KeyInstance = key; o.X = x; return CreateObject(77, o); }
    public int Find(ulong key) { return Objects.FindIndex(delegate(ObjState o) { return o.KeyInstance == key; }); }
    public void MoveObject(ulong id, ObjState st)
    {
        int i = Objects.FindIndex(delegate(ObjState o) { return o.Id == id; });
        if (i >= 0) { st.KeyInstance = Objects[i].KeyInstance; st.Id = id; if (st.Design == null) st.Design = Objects[i].Design; Objects[i] = st; }
    }
    public void DestroyObject(ulong id) { Objects.RemoveAll(delegate(ObjState o) { return o.Id == id; }); }

    // Sim progress and lot structure are opaque blobs to the session.
    public Dictionary<string, string> SimData = new Dictionary<string, string>();
    public Dictionary<ulong, string> Structure = new Dictionary<ulong, string>();
    public bool ReadSimData(ulong sim, byte kind, Buf into)
    {
        string v;
        if (!SimData.TryGetValue(sim + ":" + kind, out v)) return false;
        into.Str(v);
        return true;
    }
    public void ApplySimData(ulong sim, byte kind, Rd from) { SimData[sim + ":" + kind] = from.Str(); }
    public bool ReadLotStructure(ulong lot, Buf into)
    {
        string v;
        if (!Structure.TryGetValue(lot, out v)) return false;
        into.Str(v);
        return true;
    }
    public void ApplyLotStructure(ulong lot, Rd from) { Structure[lot] = from.Str(); }

    public void Listener(out float x, out float y, out float z, out float fx, out float fz) { x = y = z = fx = 0; fz = 1; }
    public void RestrictAdminMods(bool restrict) { Restricted = restrict; }
}

delegate bool Cond();

static class Harness
{
    static Session A, B;
    static FakeGame GA = new FakeGame(), GB = new FakeGame();

    static void Wait(string what, Cond cond)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(8);
        while (!cond())
        {
            if (DateTime.UtcNow > end) { Console.WriteLine("FAIL: timed out waiting for: " + what); Environment.Exit(1); }
            A.Update(); B.Update();
            Thread.Sleep(5);
        }
        Console.WriteLine("ok: " + what);
    }

    static void Pump(int ms) { DateTime end = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < end) { A.Update(); B.Update(); Thread.Sleep(5); } }

    static int Main(string[] args)
    {
        string server = args[0], adminPw = args[1];
        GA.Who = "Alice"; GB.Who = "Bob";
        A = new Session(new SocketTransport(int.Parse(args[2])), GA);
        B = new Session(new SocketTransport(int.Parse(args[3])), GB);
        Menu menuB = new Menu(B, GB);

        Wait("bridges answer", delegate { return A.BridgeUp && B.BridgeUp; });
        A.Connect(server, "Alice", adminPw);
        Wait("Alice is connected as admin", delegate { return A.Phase == Msg.PhaseWaitingForWorld && A.Admin; });

        GA.World = true; GA.Save = "Town"; GA.House = 1001; GA.Home = 77;
        SimState s = new SimState(); s.SimId = 5001; s.X = 12.5f; s.Interaction = "";
        GA.Sims.Add(s);
        A.HostWorld("Town");
        Wait("Alice hosts and enters the session at 1x", delegate { return A.InSession && GA.SpeedNow == 1; });
        if (GA.Restricted) { Console.WriteLine("FAIL: admin must keep NRaas access"); return 1; }

        // Bob uses the F9 menu: option 3 = "Join a server".
        foreach (string a in new string[] { "3", "Bob", server, "" }) GB.Answers.Enqueue(a);
        menuB.Open();
        Wait("Bob downloads the world", delegate { return B.Phase == Msg.PhaseWorldReady; });
        if (B.Admin) { Console.WriteLine("FAIL: Bob must not be admin"); return 1; }
        GB.World = true; GB.Save = B.SaveName; GB.House = 1002;
        Wait("Bob enters the session", delegate { return B.InSession && GB.Restricted; });

        Wait("Bob sees Alice's sim move", delegate { float x; return GB.RemoteX.TryGetValue(5001, out x) && x == 12.5f; });
        Wait("Alice owns her home lot", delegate { return B.OwnerOf(77) == "Alice"; });

        GB.CameraLot = 77; GB.BuildMode = true;
        Wait("Bob is thrown out of build mode on Alice's lot", delegate { return GB.Exits == 1; });
        A.SetBuilder(77, "Bob", true);
        Wait("Bob becomes a builder", delegate { return B.CanBuild(77); });
        GB.BuildMode = true; Pump(300);
        if (GB.Exits != 1 || !GB.BuildMode) { Console.WriteLine("FAIL: builder was not allowed to build"); return 1; }
        Console.WriteLine("ok: builder may build");
        GB.BuildMode = false;

        s.Interaction = "Some.Interaction+Definition, Sims3GameplayObjects"; s.Target = 9;
        GA.Sims[0] = s;
        Wait("Alice's interaction is replicated to Bob", delegate { return GB.Pushed.Contains("5001:Some.Interaction+Definition, Sims3GameplayObjects:9"); });
        if (GA.Pushed.Count != 0) { Console.WriteLine("FAIL: own interaction echoed back"); return 1; }

        GA.Asleep = true; Pump(800);
        if (GB.SpeedNow != 1) { Console.WriteLine("FAIL: sped up while Bob is awake"); return 1; }
        GB.Asleep = true;
        Wait("3x while everyone sleeps", delegate { return GA.SpeedNow == 3 && GB.SpeedNow == 3; });
        GB.Asleep = false;
        Wait("1x when somebody wakes up", delegate { return GA.SpeedNow == 1 && GB.SpeedNow == 1; });
        GA.Asleep = false; Pump(700);

        GB.SpeedNow = 3; Pump(200);
        if (GB.SpeedNow != 1) { Console.WriteLine("FAIL: non-admin changed the speed"); return 1; }
        Console.WriteLine("ok: non-admin speed change is reverted");
        GA.SpeedNow = 2;
        Wait("admin speed change applies to everyone", delegate { return GB.SpeedNow == 2; });

        // Needs and money follow the household's owner.
        GA.Funds[1001] = 1234; GA.Motives[5001] = new float[] { 0.5f, 10f, 20f, 30f, 40f, -50f };
        GB.Funds[1002] = 777;
        Wait("Alice's funds and needs reach Bob", delegate
        {
            float[] m;
            return GB.GetFunds(1001) == 1234 && GB.Motives.TryGetValue(5001, out m) && m[0] == 0.5f && m[5] == -50f;
        });
        Wait("Bob's funds reach Alice", delegate { return GA.GetFunds(1002) == 777; });
        GA.Funds[1001] = 900; // Alice buys something
        Wait("spending shows up for Bob", delegate { return GB.GetFunds(1001) == 900; });
        if (GA.GetFunds(1001) != 900 || GB.GetFunds(1002) != 777) { Console.WriteLine("FAIL: an owner's funds were overwritten"); return 1; }

        // Skills, career, moodlets, relationships follow the owner too.
        GA.SimData["5001:" + Msg.SlotSkills] = "cooking 3";
        GA.SimData["5001:" + Msg.SlotCareer] = "medical 2";
        GA.SimData["5001:" + Msg.SlotMoodlets] = "well rested";
        GA.SimData["5001:" + Msg.SlotRelationships] = "friend of 6001";
        Wait("skills, career, moodlets and relationships reach Bob", delegate
        {
            string a, b, c, d;
            return GB.SimData.TryGetValue("5001:" + Msg.SlotSkills, out a) && a == "cooking 3"
                && GB.SimData.TryGetValue("5001:" + Msg.SlotCareer, out b) && b == "medical 2"
                && GB.SimData.TryGetValue("5001:" + Msg.SlotMoodlets, out c) && c == "well rested"
                && GB.SimData.TryGetValue("5001:" + Msg.SlotRelationships, out d) && d == "friend of 6001";
        });
        GA.SimData["5001:" + Msg.SlotSkills] = "cooking 4";
        Wait("a skill gain is passed on", delegate { return GB.SimData["5001:" + Msg.SlotSkills] == "cooking 4"; });
        if (GA.SimData["5001:" + Msg.SlotSkills] != "cooking 4") { Console.WriteLine("FAIL: owner's skills were overwritten"); return 1; }

        // Build/buy: both build on lot 77 at once. Their games hand out the
        // same local object ids, which must not confuse anything.
        GA.CameraLot = 77; GA.BuildMode = true; GB.BuildMode = true;
        Pump(100);
        ulong sofaB = GB.Place(0xB0B, 1f), lampA = GA.Place(0xA11CE, 2f);
        if (sofaB != lampA) { Console.WriteLine("FAIL: test expects colliding local ids"); return 1; }
        Wait("objects placed by each appear for the other", delegate { return GA.Find(0xB0B) >= 0 && GB.Find(0xA11CE) >= 0; });
        if (GA.Objects.Count != 2 || GB.Objects.Count != 2) { Console.WriteLine("FAIL: duplicated objects"); return 1; }
        ObjState moved = GB.Objects[GB.Find(0xB0B)]; moved.X = 7.5f; GB.Objects[GB.Find(0xB0B)] = moved;
        Wait("moving an object is replicated", delegate { return GA.Objects[GA.Find(0xB0B)].X == 7.5f; });
        if (GA.Objects[GA.Find(0xA11CE)].X != 2f) { Console.WriteLine("FAIL: the wrong object was moved"); return 1; }
        GA.DestroyObject(lampA);
        Wait("deleting an object is replicated", delegate { return GB.Find(0xA11CE) < 0; });
        if (GB.Find(0xB0B) < 0 || GA.Find(0xB0B) < 0) { Console.WriteLine("FAIL: the wrong object was deleted"); return 1; }

        // Colours/patterns: recolouring an object reaches the other player.
        ObjState painted = GB.Objects[GB.Find(0xB0B)]; painted.Design = new byte[] { 9, 8, 7 }; GB.Objects[GB.Find(0xB0B)] = painted;
        Wait("an object's design is replicated", delegate
        {
            byte[] d = GA.Objects[GA.Find(0xB0B)].Design;
            return d != null && d.Length == 3 && d[0] == 9 && d[2] == 7;
        });
        if (GA.Objects[GA.Find(0xB0B)].X != 7.5f) { Console.WriteLine("FAIL: recolouring moved the object"); return 1; }

        // Walls, floors, roofs, terrain, wallpaper: the lot's structure blob.
        GA.Structure[77] = "one room"; GB.Structure[77] = "one room";
        GA.BuildMode = false; GB.BuildMode = false; Pump(200);
        GB.BuildMode = true; Pump(200);            // Bob enters: baseline, nothing sent
        GB.Structure[77] = "one room + extension"; // Bob builds a wall
        GB.BuildMode = false;                      // leaving build mode publishes it
        Wait("structure changes reach Alice", delegate { return GA.Structure[77] == "one room + extension"; });
        Pump(300);
        if (GB.Structure[77] != "one room + extension") { Console.WriteLine("FAIL: structure bounced back"); return 1; }
        GA.BuildMode = false; GB.BuildMode = false;
        Pump(300);

        // Bob reloads the (old) save: what was built since is rebuilt from the
        // server's log, his own funds come back, old interactions do not replay.
        int pushed = GB.Pushed.Count;
        GB.World = false; Pump(200);
        GB.Objects.Clear(); GB.NextId = 1000; GB.Funds.Clear(); GB.Motives.Clear(); GB.SimData.Clear(); GB.Structure[77] = "one room";
        GB.World = true;
        Wait("Bob is back after reloading", delegate { return B.InSession; });
        if (GB.Objects.Count != 1 || GB.Find(0xB0B) < 0 || GB.Objects[0].X != 7.5f) { Console.WriteLine("FAIL: build log not replayed correctly (" + GB.Objects.Count + " objects)"); return 1; }
        if (GB.GetFunds(1002) != 777 || GB.GetFunds(1001) != 900) { Console.WriteLine("FAIL: funds not restored after reload"); return 1; }
        if (GB.Pushed.Count != pushed) { Console.WriteLine("FAIL: old interactions were replayed"); return 1; }
        byte[] design = GB.Objects[0].Design;
        if (design == null || design.Length != 3) { Console.WriteLine("FAIL: design not restored after reload"); return 1; }
        if (GB.Structure[77] != "one room + extension") { Console.WriteLine("FAIL: structure not restored after reload"); return 1; }
        if (!GB.SimData.ContainsKey("5001:" + Msg.SlotSkills) || GB.SimData["5001:" + Msg.SlotSkills] != "cooking 4") { Console.WriteLine("FAIL: skills not restored after reload"); return 1; }
        Console.WriteLine("ok: reload rebuilds objects, restores funds, replays no interactions");
        Console.WriteLine("ok: reload restores designs, structure and sim progress");
        Pump(1200);
        if (GA.Objects.Count != 1 || GB.Objects.Count != 1) { Console.WriteLine("FAIL: objects duplicated after rejoin"); return 1; }

        A.HostWorld("Town");
        Wait("re-hosting pauses Bob and asks him to reload", delegate { return !B.InSession && GB.SpeedNow == 0; });
        Wait("Alice is back in the session", delegate { return A.InSession; });
        Console.WriteLine("ALL OK");
        return 0;
    }
}
