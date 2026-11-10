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

        // Operation log position: lets a reconnect resume instead of replaying.
        ulong mLastSeq;

        // Needs + money of the household this player owns.
        readonly Buf mVitals = new Buf();
        readonly float[] mMotives = new float[6];
        byte[] mLastVitals = new byte[0];
        long mNextVitals, mVitalsForced;

        // Build/buy replication. Objects that exist in the hosted save have
        // the same id on every client; objects created during the session
        // are named by the sequence number of the operation that made them.
        ulong mBuildLot;
        long mNextScan;
        readonly List<ObjState> mScan = new List<ObjState>();
        readonly Dictionary<ulong, ObjState> mSent = new Dictionary<ulong, ObjState>();  // what the others have
        readonly Dictionary<ulong, ObjState> mSeen = new Dictionary<ulong, ObjState>();  // previous scan
        readonly Dictionary<ulong, ulong> mNetToLocal = new Dictionary<ulong, ulong>();
        readonly Dictionary<ulong, ulong> mLocalToNet = new Dictionary<ulong, ulong>();
        readonly Queue<ulong> mPendingCreates = new Queue<ulong>();

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
            // Look at the local speed before applying server messages: a clock
            // update handled first would silently undo the admin's own change.
            if (InSession && mGame.InWorld) SyncSpeed();
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
                ForgetWorld();
                return;
            }
            if (!InSession) { TryEnter(); return; }

            long now = Now;
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
            TrackBuild(now);
        }

        // Everything tied to the loaded copy of the world.
        void ForgetWorld()
        {
            mLastSeq = 0;
            mNetToLocal.Clear();
            mLocalToNet.Clear();
        }

        void Leave()
        {
            InSession = false;
            mReadySent = false;
            mApplied = -1;
            mClaimed = 0;
            mLastInteraction.Clear();
            mAskedLots.Clear();
            mPendingCreates.Clear();
            mBuildLot = 0;
            mSent.Clear();
            mSeen.Clear();
            mLastVitals = new byte[0];
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
            mOut.Begin(Msg.CReady).U64(mLastSeq); Send();
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
                mOut.Begin(Msg.COp).U8(Msg.OpGeneral | Msg.OpTransient).U64(0).U64(house).Bytes(p.Data, p.Length); Send();
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

            if (house != 0 && house == mClaimed && Now >= mNextVitals) { mNextVitals = Now + 1000; SendVitals(house); }

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

        // --- needs and money ---------------------------------------------

        // The player who owns a household is the authority on its funds and
        // its sims' needs; everybody else's game is overwritten with them.
        void SendVitals(ulong house)
        {
            mSims.Clear();
            mGame.ControlledSims(mSims);
            mVitals.Length = 0;
            mVitals.U32((uint)mGame.GetFunds(house));
            int at = mVitals.Length, n = 0;
            mVitals.U8(0);
            for (int i = 0; i < mSims.Count && n < 32; i++)
            {
                if (!mGame.ReadMotives(mSims[i].SimId, mMotives)) continue;
                mVitals.U64(mSims[i].SimId);
                for (int k = 0; k < 6; k++) mVitals.F32(mMotives[k]);
                n++;
            }
            mVitals.Data[at] = (byte)n;

            bool same = mVitals.Length == mLastVitals.Length;
            for (int i = 0; same && i < mVitals.Length; i++) same = mVitals.Data[i] == mLastVitals[i];
            if (same && Now < mVitalsForced) return;
            mVitalsForced = Now + 15000;
            mLastVitals = new byte[mVitals.Length];
            Array.Copy(mVitals.Data, mLastVitals, mVitals.Length);
            mOut.Begin(Msg.CEntityData).U64(house).U8(Msg.SlotVitals).Bytes(mVitals.Data, mVitals.Length); Send();
        }

        void ApplyVitals(ulong house, Rd p)
        {
            int funds = (int)p.U32();
            int n = p.U8();
            if (p.Err) return;
            mGame.SetFunds(house, funds);
            for (int i = 0; i < n; i++)
            {
                ulong sim = p.U64();
                for (int k = 0; k < 6; k++) mMotives[k] = p.F32();
                if (p.Err) return;
                mGame.ApplyMotives(sim, mMotives);
            }
        }

        // --- build / buy -------------------------------------------------

        static void WriteObj(Buf b, ObjState o)
        {
            b.U32(o.KeyType).U32(o.KeyGroup).U64(o.KeyInstance).F32(o.X).F32(o.Y).F32(o.Z).F32(o.FwdX).F32(o.FwdZ).U32((uint)o.Level);
        }

        static ObjState ReadObj(Rd r)
        {
            ObjState o = new ObjState();
            o.KeyType = r.U32(); o.KeyGroup = r.U32(); o.KeyInstance = r.U64();
            o.X = r.F32(); o.Y = r.F32(); o.Z = r.F32(); o.FwdX = r.F32(); o.FwdZ = r.F32();
            o.Level = (int)r.U32();
            return o;
        }

        // Writes a reference every client can resolve; false while this
        // client's own create operation has not come back from the server.
        bool WriteRef(Buf b, ulong local)
        {
            ulong net;
            if (mLocalToNet.TryGetValue(local, out net)) { b.U8(1).U64(net); return true; }
            if (mPendingCreates.Contains(local)) return false;
            b.U8(0).U64(local);
            return true;
        }

        ulong ReadRef(Rd r)
        {
            byte kind = r.U8(); ulong id = r.U64(), local;
            if (kind == 0) return id;
            return mNetToLocal.TryGetValue(id, out local) ? local : 0UL;
        }

        void SendBuild(ulong lot, Buf payload)
        {
            mOut.Begin(Msg.COp).U8(Msg.OpBuild).U64(lot).U64(0).Bytes(payload.Data, payload.Length); Send();
        }

        void TrackBuild(long now)
        {
            ulong lot = mGame.InBuildBuy ? mGame.BuildLot : 0UL;
            if (lot != 0 && !CanBuild(lot)) lot = 0;
            if (lot != mBuildLot)
            {
                if (mBuildLot != 0) ScanLot(mBuildLot, true); // leaving: publish what is left
                mBuildLot = lot;
                mSent.Clear(); mSeen.Clear();
                if (lot != 0)
                {
                    // Whatever is on the lot now is what everyone already has.
                    mScan.Clear();
                    mGame.LotObjects(lot, mScan);
                    for (int i = 0; i < mScan.Count; i++) { mSent[mScan[i].Id] = mScan[i]; mSeen[mScan[i].Id] = mScan[i]; }
                    mNextScan = now + 400;
                }
                return;
            }
            if (lot != 0 && now >= mNextScan) { mNextScan = now + 400; ScanLot(lot, false); }
        }

        // Compares the lot with what was last published. Unless final, a change
        // is only sent once it has been stable for one scan, so an object being
        // dragged around produces one operation, not a stream of them.
        void ScanLot(ulong lot, bool final)
        {
            mScan.Clear();
            mGame.LotObjects(lot, mScan);
            Buf p = new Buf();
            Dictionary<ulong, bool> present = new Dictionary<ulong, bool>();
            for (int i = 0; i < mScan.Count; i++)
            {
                ObjState cur = mScan[i], seen, sent;
                present[cur.Id] = true;
                bool stable = final || (mSeen.TryGetValue(cur.Id, out seen) && seen.Same(cur));
                mSeen[cur.Id] = cur;
                if (!stable) continue;
                if (!mSent.TryGetValue(cur.Id, out sent))
                {
                    p.Length = 0;
                    p.U8(Msg.BuildCreate).U64(cur.Id);
                    WriteObj(p, cur);
                    mPendingCreates.Enqueue(cur.Id);
                    mSent[cur.Id] = cur;
                    SendBuild(lot, p);
                }
                else if (!sent.Same(cur))
                {
                    p.Length = 0;
                    p.U8(Msg.BuildMove);
                    if (!WriteRef(p, cur.Id)) continue; // retry on the next scan
                    WriteObj(p, cur);
                    mSent[cur.Id] = cur;
                    SendBuild(lot, p);
                }
            }
            List<ulong> gone = new List<ulong>();
            foreach (ulong id in mSent.Keys) if (!present.ContainsKey(id)) gone.Add(id);
            for (int i = 0; i < gone.Count; i++)
            {
                p.Length = 0;
                p.U8(Msg.BuildDelete);
                if (!WriteRef(p, gone[i]) && !final) continue;
                mSent.Remove(gone[i]); mSeen.Remove(gone[i]);
                if (p.Length > 1) SendBuild(lot, p);
                ulong net;
                if (mLocalToNet.TryGetValue(gone[i], out net)) { mLocalToNet.Remove(gone[i]); mNetToLocal.Remove(net); }
            }
        }

        void ApplyBuild(ulong seq, bool mine, ulong lot, Rd p)
        {
            byte kind = p.U8();
            if (kind == Msg.BuildCreate)
            {
                p.U64();
                ObjState o = ReadObj(p);
                if (p.Err) return;
                ulong local;
                if (mine)
                {
                    // Our own object: now we know the name everyone uses for it.
                    if (mPendingCreates.Count == 0) return;
                    local = mPendingCreates.Dequeue();
                }
                else
                {
                    local = mGame.CreateObject(lot, o);
                    if (local == 0) return;
                    o.Id = local;
                    if (lot == mBuildLot) { mSent[local] = o; mSeen[local] = o; }
                }
                mNetToLocal[seq] = local;
                mLocalToNet[local] = seq;
                return;
            }
            if (mine) return;
            ulong target = ReadRef(p);
            if (p.Err || target == 0) return;
            if (kind == Msg.BuildMove)
            {
                ObjState o = ReadObj(p);
                if (p.Err) return;
                o.Id = target;
                mGame.MoveObject(target, o);
                if (lot == mBuildLot) { mSent[target] = o; mSeen[target] = o; }
            }
            else if (kind == Msg.BuildDelete)
            {
                mGame.DestroyObject(target);
                mSent.Remove(target); mSeen.Remove(target);
                ulong net;
                if (mLocalToNet.TryGetValue(target, out net)) { mLocalToNet.Remove(target); mNetToLocal.Remove(net); }
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
                        ForgetWorld(); // a new world starts a new operation log
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
                    ulong seq = r.U64();
                    uint from = r.U32(); byte cat = (byte)(r.U8() & 0x7F);
                    ulong lot = r.U64(); r.U64();
                    Rd p = r.Bytes();
                    if (r.Err || !mReadySent) break;
                    if (seq > mLastSeq) mLastSeq = seq;
                    // During the replay that precedes the snapshot nothing is "ours":
                    // player ids are per connection and this copy of the world is fresh.
                    bool mine = InSession && from == PlayerId;
                    if (cat == Msg.OpBuild) ApplyBuild(seq, mine, lot, p);
                    else if (cat == Msg.OpGeneral && !mine && p.U8() == Msg.OpInteraction)
                    {
                        ulong sim = p.U64(); string def = p.Str(); ulong target = p.U64();
                        if (!p.Err) mGame.PushInteraction(sim, def, target);
                    }
                    break;
                }
                case Msg.SEntityData:
                {
                    ulong ent = r.U64(); byte slot = r.U8();
                    Rd p = r.Bytes();
                    // Also applies our own household's last published values after a
                    // reload (it is not claimed yet at that point), restoring them.
                    if (!r.Err && mReadySent && slot == Msg.SlotVitals && ent != mClaimed) ApplyVitals(ent, p);
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
