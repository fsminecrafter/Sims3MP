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

        A.HostWorld("Town");
        Wait("re-hosting pauses Bob and asks him to reload", delegate { return !B.InSession && GB.SpeedNow == 0; });
        Wait("Alice is back in the session", delegate { return A.InSession; });
        Console.WriteLine("ALL OK");
        return 0;
    }
}
