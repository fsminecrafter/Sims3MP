// The multiplayer session as seen by one game client: talks to the bridge,
// mirrors server state (players, clock, ownership) and applies the rules to
// the local game every frame. Never blocks: called from the UI tick.
using System;
using System.Collections.Generic;

namespace Sims3MP
{
    public sealed class LotInfo
    {
        public string Owner = "";
        public List<string> Builders = new List<string>();
    }

    public sealed class Session
    {
        readonly ITransport mNet;
        readonly IGame mGame;
        readonly Buf mOut = new Buf();
        readonly byte[] mIn = new byte[(1 << 20) + 8];

        public bool BridgeUp;
        public byte Phase;
        public string Status = "Starting...";
        public uint PlayerId;
        public bool Admin;
        public string PlayerName = "";
        public string SaveName = "";
        public bool InSession;
        public int ClockSpeed;
        public byte ClockMode = Msg.SpeedAuto;
        public bool MicOn = true, SpeakerOn = true;

        public readonly Dictionary<uint, string> Players = new Dictionary<uint, string>();
        public readonly Dictionary<ulong, LotInfo> Lots = new Dictionary<ulong, LotInfo>();
        public readonly Dictionary<ulong, string> Households = new Dictionary<ulong, string>();

        bool mReadySent, mNeedReload, mHosting, mManualEnter, mRestartShown, mAsleep;
        int mApplied = -1;
        ulong mClaimed;
        long mNextState, mNextListener, mNextSlow, mLastDenied;
        readonly List<SimState> mSims = new List<SimState>();
        readonly Dictionary<ulong, string> mLastInteraction = new Dictionary<ulong, string>();
        readonly Dictionary<ulong, bool> mAskedLots = new Dictionary<ulong, bool>();

        public Session(ITransport net, IGame game) { mNet = net; mGame = game; }

        static long Now { get { return DateTime.UtcNow.Ticks / 10000; } }

        void Send() { mNet.Send(mOut.Data, mOut.Length); }

        // --- commands used by the menu -----------------------------------

        public void Connect(string address, string name, string password)
        {
            mOut.Begin(Msg.LConnect).Str(address).Str(name).Str(password); Send();
        }
        public void StartLocalServer(ushort port, string name)
        {
            mOut.Begin(Msg.LStartServer).U16(port).Str(name); Send();
        }
        public void Disconnect() { mOut.Begin(Msg.LDisconnect); Send(); }
        public void HostWorld(string save)
        {
            mHosting = true;
            mOut.Begin(Msg.LHostWorld).Str(save); Send();
        }
        public void EnterManually() { mManualEnter = true; mNeedReload = false; }
        public void SetSpeed(byte mode) { mOut.Begin(Msg.CSetSpeed).U8(mode); Send(); }
        public void ClaimLot(ulong lot) { mOut.Begin(Msg.CClaimLot).U64(lot); Send(); }
        public void ReleaseLot(ulong lot) { mOut.Begin(Msg.CReleaseLot).U64(lot); Send(); }
        public void SetBuilder(ulong lot, string player, bool add)
        {
            mOut.Begin(Msg.CSetBuilder).U64(lot).Str(player).Bool(add); Send();
        }
        public void Chat(string text) { mOut.Begin(Msg.CChat).Str(text); Send(); }
        public void Kick(string player) { mOut.Begin(Msg.CKick).Str(player); Send(); }
        public void SetVoice(bool mic, bool speaker)
        {
            MicOn = mic; SpeakerOn = speaker;
            mOut.Begin(Msg.LVoice).Bool(mic).Bool(speaker); Send();
        }

        public bool CanBuild(ulong lot)
        {
            if (Admin) return true;
            LotInfo info;
            if (!Lots.TryGetValue(lot, out info)) return false;
            return info.Owner == PlayerName || info.Builders.Contains(PlayerName);
        }

        public string OwnerOf(ulong lot)
        {
            LotInfo info;
            return Lots.TryGetValue(lot, out info) ? info.Owner : "";
        }

        // --- per-frame update --------------------------------------------

        public void Update()
        {
            if (!mNet.Poll())
            {
                if (BridgeUp) { BridgeUp = false; Leave(); Phase = Msg.PhaseIdle; Status = "Lost contact with the Sims3MP bridge."; }
                return;
            }
            if (!BridgeUp)
            {
                BridgeUp = true;
                mOut.Begin(Msg.LQuery); Send();
            }
            for (int i = 0; i < 256; i++) // bounded work per frame: no hitching on bursts
            {
                int n = mNet.Recv(mIn);
                if (n <= 0) break;
                try { Handle(mIn[0], new Rd(mIn, 1, n)); }
                catch (Exception e) { mGame.Notify("Sims3MP: " + e.Message); }
            }

            if (!mGame.InWorld)
            {
                if (InSession) Leave();
                mReadySent = false; mNeedReload = false; mManualEnter = false;
                return;
            }
            if (!InSession) { TryEnter(); return; }

            long now = Now;
            SyncSpeed();
            if (now >= mNextState) { mNextState = now + 33; SendSims(); }
            if (now >= mNextListener)
            {
                mNextListener = now + 50;
                float x, y, z, fx, fz;
                mGame.Listener(out x, out y, out z, out fx, out fz);
                mOut.Begin(Msg.LListener).F32(x).F32(y).F32(z).F32(fx).F32(fz); Send();
            }
            if (now >= mNextSlow) { mNextSlow = now + 500; Slow(); }
            if (mGame.InBuildBuy) GuardBuild(now);
        }

        void Leave()
        {
            InSession = false;
            mReadySent = false;
            mApplied = -1;
            mClaimed = 0;
            mLastInteraction.Clear();
            mAskedLots.Clear();
            mGame.RestrictAdminMods(false);
        }

        void TryEnter()
        {
            if (Phase != Msg.PhaseWorldReady || mReadySent || mNeedReload) return;
            string loaded = mGame.SaveName;
            bool match = mHosting || mManualEnter
                || (loaded.Length > 0 && string.Compare(loaded, SaveName, StringComparison.OrdinalIgnoreCase) == 0);
            if (!match) return;
            mReadySent = true;
            mGame.Speed = 0; mApplied = 0; // paused until the server's snapshot and clock arrive
            mOut.Begin(Msg.CReady); Send();
        }

        void SyncSpeed()
        {
            int cur = mGame.Speed;
            if (cur == mApplied) return;
            if (Admin && mApplied >= 0)
            {
                // The admin used the game's own speed buttons: that sets the speed for everybody.
                mApplied = cur;
                SetSpeed((byte)cur);
                return;
            }
            if (mApplied >= 0 && Now - mLastDenied > 5000)
            {
                mLastDenied = Now;
                mGame.Notify("Game speed is controlled by the server (3x while everyone sleeps).");
            }
            mGame.Speed = ClockSpeed;
            mApplied = ClockSpeed;
        }

        void SendSims()
        {
            ulong house = mGame.ActiveHousehold;
            if (house == 0 || house != mClaimed) return;
            mSims.Clear();
            mGame.ControlledSims(mSims);
            if (mSims.Count == 0) return;
            mOut.Begin(Msg.LState).U64(house);
            for (int i = 0; i < mSims.Count; i++)
            {
                SimState s = mSims[i];
                mOut.U64(s.SimId).F32(s.X).F32(s.Y).F32(s.Z).F32(s.FwdX).F32(s.FwdZ);
            }
            Send();

            // Interactions are replicated as reliable, ordered operations: the
            // other clients run the same interaction on their copy of the sim
            // and the position stream only corrects drift.
            for (int i = 0; i < mSims.Count; i++)
            {
                SimState s = mSims[i];
                string key = (s.Interaction ?? "") + "|" + s.Target;
                string last;
                if (mLastInteraction.TryGetValue(s.SimId, out last) && last == key) continue;
                mLastInteraction[s.SimId] = key;
                if (string.IsNullOrEmpty(s.Interaction)) continue;
                Buf p = new Buf();
                p.Begin(Msg.OpInteraction).U64(s.SimId).Str(s.Interaction).U64(s.Target);
                mOut.Begin(Msg.COp).U8(Msg.OpGeneral).U64(0).U64(house).Bytes(p.Data, p.Length); Send();
            }
        }

        // Twice a second: things that change rarely.
        void Slow()
        {
            bool asleep = mGame.AllActiveSimsAsleep;
            if (asleep != mAsleep)
            {
                mAsleep = asleep;
                mOut.Begin(Msg.CSetSleeping).Bool(asleep); Send();
            }

            ulong house = mGame.ActiveHousehold;
            if (house != mClaimed)
            {
                string owner;
                if (house != 0 && Households.TryGetValue(house, out owner) && owner.Length > 0 && owner != PlayerName)
                {
                    if (Now - mLastDenied > 5000)
                    {
                        mLastDenied = Now;
                        mGame.Notify("This household is played by " + owner + ". Press F9 and choose another household.");
                    }
                }
                else
                {
                    if (mClaimed != 0) { mOut.Begin(Msg.CReleaseEntity).U64(mClaimed); Send(); }
                    mClaimed = house;
                    if (house != 0) { mOut.Begin(Msg.CClaimEntity).U64(house); Send(); }
                }
            }

            // Moving a household onto a lot (buying it) makes this player its owner.
            ulong home = mGame.HomeLot;
            if (home != 0 && house == mClaimed && !Lots.ContainsKey(home) && !mAskedLots.ContainsKey(home))
            {
                mAskedLots[home] = true;
                ClaimLot(home);
            }
        }

        void GuardBuild(long now)
        {
            ulong lot = mGame.BuildLot;
            if (lot == 0 || CanBuild(lot)) return;
            mGame.ExitBuildBuy();
            if (now - mLastDenied > 3000)
            {
                mLastDenied = now;
                string owner = OwnerOf(lot);
                mGame.Notify(owner.Length > 0
                    ? "This lot belongs to " + owner + ". Ask them to add you as a builder (F9 > Lots)."
                    : "Nobody owns this lot yet. Move your household in to buy it, or ask the admin.");
            }
        }

        // --- incoming messages -------------------------------------------

        void Handle(byte type, Rd r)
        {
            switch (type)
            {
                case Msg.LStatus:
                {
                    byte phase = r.U8();
                    Status = r.Str(); PlayerId = r.U32(); Admin = r.Bool(); SaveName = r.Str(); PlayerName = r.Str();
                    if (phase != Phase)
                    {
                        if (phase != Msg.PhaseWorldReady) { mReadySent = false; if (InSession) Leave(); }
                        if (phase == Msg.PhaseIdle || phase == Msg.PhaseError) { Players.Clear(); mHosting = false; }
                        if (phase == Msg.PhaseRestartRequired && !mRestartShown)
                        {
                            mRestartShown = true;
                            string text = Status;
                            mGame.RunTask(delegate { mGame.Message("Sims3MP", text); });
                        }
                        else if (phase != Msg.PhaseSyncing || Phase != Msg.PhaseSyncing) mGame.Notify("Sims3MP: " + Status);
                        Phase = phase;
                    }
                    break;
                }
                case Msg.SPlayerJoin:
                {
                    uint id = r.U32(); string name = r.Str();
                    if (!Players.ContainsKey(id) && id != PlayerId && InSession) mGame.Notify(name + " joined.");
                    Players[id] = name;
                    break;
                }
                case Msg.SPlayerLeave:
                {
                    uint id = r.U32(); string name;
                    if (Players.TryGetValue(id, out name)) { Players.Remove(id); if (InSession) mGame.Notify(name + " left."); }
                    break;
                }
                case Msg.SState:
                    if (r.U8() == Msg.StateInitializing)
                    {
                        // The server is taking in a (new) world: everybody pauses until it is ready.
                        if (mGame.InWorld) { mGame.Speed = 0; mApplied = 0; }
                        if (InSession && !mHosting)
                        {
                            mNeedReload = true;
                            mGame.Notify("The admin uploaded a new copy of the world. Return to the main menu and load the save again to continue.");
                        }
                        InSession = false; mReadySent = false;
                    }
                    break;
                case Msg.SClock:
                    r.U64();
                    ClockSpeed = r.U8(); ClockMode = r.U8();
                    if (InSession || mReadySent) { mGame.Speed = ClockSpeed; mApplied = ClockSpeed; }
                    break;
                case Msg.SSnapshotEnd:
                    InSession = true;
                    mGame.Speed = ClockSpeed; mApplied = ClockSpeed;
                    mGame.RestrictAdminMods(!Admin);
                    mOut.Begin(Msg.LVoice).Bool(MicOn).Bool(SpeakerOn); Send();
                    mGame.Notify("Sims3MP: you are in the session" + (Admin ? " as admin." : "."));
                    break;
                case Msg.SLot:
                {
                    ulong lot = r.U64();
                    LotInfo info = new LotInfo();
                    info.Owner = r.Str();
                    int n = r.U16();
                    for (int i = 0; i < n && !r.Err; i++) info.Builders.Add(r.Str());
                    if (info.Owner.Length == 0) Lots.Remove(lot); else Lots[lot] = info;
                    mAskedLots.Remove(lot);
                    break;
                }
                case Msg.SEntity:
                {
                    ulong id = r.U64(); string owner = r.Str();
                    if (owner.Length == 0) Households.Remove(id); else Households[id] = owner;
                    break;
                }
                case Msg.SOp:
                {
                    r.U64();
                    uint from = r.U32(); byte cat = r.U8();
                    r.U64(); r.U64();
                    Rd p = r.Bytes();
                    if (from == PlayerId || cat != Msg.OpGeneral || !mReadySent) break;
                    if (p.U8() == Msg.OpInteraction)
                    {
                        ulong sim = p.U64(); string def = p.Str(); ulong target = p.U64();
                        if (!p.Err) mGame.PushInteraction(sim, def, target);
                    }
                    break;
                }
                case Msg.LStateRelay:
                {
                    r.U32(); r.U64();
                    while (InSession && r.Left >= 28)
                    {
                        ulong sim = r.U64();
                        float x = r.F32(), y = r.F32(), z = r.F32(), fx = r.F32(), fz = r.F32();
                        mGame.ApplyRemoteSim(sim, x, y, z, fx, fz);
                    }
                    break;
                }
                case Msg.SChat:
                {
                    uint from = r.U32(); string name;
                    if (!Players.TryGetValue(from, out name)) name = "?";
                    mGame.Notify(name + ": " + r.Str());
                    break;
                }
                case Msg.SNotice:
                    mGame.Notify("Sims3MP: " + r.Str());
                    break;
                case Msg.SOpRejected:
                    r.U8(); r.U64(); r.U64();
                    mGame.Notify("Sims3MP: not allowed - " + r.Str());
                    break;
            }
        }
    }
}
