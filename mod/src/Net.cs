// Wire protocol helpers for the script mod. Mirrors internal/proto/proto.go.
// Deliberately restricted to what the game's cut-down mscorlib provides:
// no System.IO, no System.Core, no threads.
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Sims3MP
{
    /// <summary>Message link to the local bridge process.</summary>
    public interface ITransport
    {
        bool Poll();                       // drive the connection; true while connected
        bool Send(byte[] body, int len);   // body = type byte + payload
        int Recv(byte[] buffer);           // one message, 0 if none, -1 if down
    }

    /// <summary>The native shim (Game\Bin\MonoPatcher\plugins\Sims3MPNative.dll).</summary>
    public sealed class NativeTransport : ITransport
    {
        [DllImport("Sims3MPNative.dll")] static extern int MP_Poll();
        [DllImport("Sims3MPNative.dll")] static extern int MP_Send(byte[] body, int len);
        [DllImport("Sims3MPNative.dll")] static extern int MP_Recv(byte[] buffer, int cap);
        [DllImport("Sims3MPNative.dll")] static extern int MP_Version();

        bool mBroken;

        /// <summary>False when the native part is not installed.</summary>
        public bool Available
        {
            get
            {
                if (mBroken) return false;
                try { return MP_Version() >= 1; }
                catch (Exception) { mBroken = true; return false; }
            }
        }

        public bool Poll() { return !mBroken && MP_Poll() == 1; }
        public bool Send(byte[] body, int len) { return !mBroken && MP_Send(body, len) == 1; }
        public int Recv(byte[] buffer) { return mBroken ? -1 : MP_Recv(buffer, buffer.Length); }
    }

    public static class Msg
    {
        public const byte CReady = 10, COp = 11, CSetSleeping = 12, CSetSpeed = 13, CClaimLot = 14,
            CReleaseLot = 15, CSetBuilder = 16, CClaimEntity = 17, CSetShared = 18, CChat = 19,
            CKick = 21, CSetLotOwner = 22, CReleaseEntity = 23, CEntityData = 24, CLotData = 25;

        public const byte SWelcome = 64, SPlayerJoin = 66, SPlayerLeave = 67, SState = 69, SClock = 70,
            SOp = 71, SOpRejected = 72, SLot = 73, SEntity = 74, SShared = 75, SChat = 76,
            SNotice = 78, SSnapshotEnd = 79, SEntityData = 80, SLotData = 81;

        public const byte LConnect = 128, LDisconnect = 129, LHostWorld = 130, LState = 131,
            LListener = 132, LVoice = 133, LStartServer = 134, LQuery = 135,
            LStatus = 160, LStateRelay = 161;

        public const byte PhaseIdle = 0, PhaseConnecting = 1, PhaseWaitingForWorld = 2, PhaseSyncing = 3,
            PhaseRestartRequired = 4, PhaseWorldReady = 5, PhaseError = 6;

        public const byte StateInitializing = 1, StateRunning = 2;
        public const byte OpGeneral = 0, OpBuild = 1, OpAdmin = 2, OpWorld = 3;
        public const byte SpeedAuto = 255;

        // OR-ed into a category: relayed in order but never replayed to late joiners.
        public const byte OpTransient = 0x80;

        // First payload byte of an OpGeneral operation.
        public const byte OpInteraction = 1;

        // First payload byte of an OpBuild operation.
        public const byte BuildCreate = 1, BuildMove = 2, BuildDelete = 3;

        // Entity data slots.
        public const byte SlotVitals = 1; // household funds + needs of its sims
        // Per-sim slots (sub = sim id). The payload format belongs to the game adapter.
        public const byte SlotSkills = 2, SlotCareer = 3, SlotMoodlets = 4, SlotRelationships = 5;
        public const byte SimSlotFirst = 2, SimSlotLast = 5;

        // Lot data slots.
        public const byte LotStructure = 1; // walls, floors, roofs, terrain, wall coverings
    }

    /// <summary>Growable little-endian message writer. Byte 0 is the message type.</summary>
    public sealed class Buf
    {
        public byte[] Data = new byte[256];
        public int Length;

        public Buf Begin(byte type) { Length = 0; return U8(type); }

        void Need(int n)
        {
            if (Length + n <= Data.Length) return;
            byte[] bigger = new byte[Math.Max(Data.Length * 2, Length + n)];
            Array.Copy(Data, bigger, Length);
            Data = bigger;
        }

        public Buf U8(byte v) { Need(1); Data[Length++] = v; return this; }
        public Buf Bool(bool v) { return U8(v ? (byte)1 : (byte)0); }
        public Buf U16(ushort v) { Need(2); Data[Length++] = (byte)v; Data[Length++] = (byte)(v >> 8); return this; }
        public Buf U32(uint v)
        {
            Need(4);
            for (int i = 0; i < 4; i++) Data[Length++] = (byte)(v >> (8 * i));
            return this;
        }
        public Buf U64(ulong v)
        {
            Need(8);
            for (int i = 0; i < 8; i++) Data[Length++] = (byte)(v >> (8 * i));
            return this;
        }
        public Buf F32(float v)
        {
            byte[] b = BitConverter.GetBytes(v);
            if (!BitConverter.IsLittleEndian) Array.Reverse(b);
            Need(4);
            Array.Copy(b, 0, Data, Length, 4);
            Length += 4;
            return this;
        }
        public Buf Str(string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s ?? "");
            int n = Math.Min(b.Length, 0xFFFF);
            U16((ushort)n);
            Need(n);
            Array.Copy(b, 0, Data, Length, n);
            Length += n;
            return this;
        }
        public Buf Bytes(byte[] b, int n)
        {
            U32((uint)n);
            Need(n);
            if (n > 0) Array.Copy(b, 0, Data, Length, n);
            Length += n;
            return this;
        }
    }

    /// <summary>Bounds-checked reader; after a short read Err is set and reads return zero.</summary>
    public sealed class Rd
    {
        byte[] mData;
        int mPos, mEnd;
        public bool Err;

        public Rd(byte[] data, int start, int end) { mData = data; mPos = start; mEnd = end; }
        public int Left { get { return Err ? 0 : mEnd - mPos; } }
        public Rd Clone() { Rd r = new Rd(mData, mPos, mEnd); r.Err = Err; return r; }

        bool Take(int n)
        {
            if (Err || n < 0 || mEnd - mPos < n) { Err = true; return false; }
            return true;
        }
        public byte U8() { return Take(1) ? mData[mPos++] : (byte)0; }
        public bool Bool() { return U8() != 0; }
        public ushort U16()
        {
            if (!Take(2)) return 0;
            ushort v = (ushort)(mData[mPos] | (mData[mPos + 1] << 8));
            mPos += 2;
            return v;
        }
        public uint U32()
        {
            if (!Take(4)) return 0;
            uint v = 0;
            for (int i = 0; i < 4; i++) v |= (uint)mData[mPos + i] << (8 * i);
            mPos += 4;
            return v;
        }
        public ulong U64()
        {
            if (!Take(8)) return 0;
            ulong v = 0;
            for (int i = 0; i < 8; i++) v |= (ulong)mData[mPos + i] << (8 * i);
            mPos += 8;
            return v;
        }
        public float F32()
        {
            if (!Take(4)) return 0f;
            float v;
            if (BitConverter.IsLittleEndian) v = BitConverter.ToSingle(mData, mPos);
            else
            {
                byte[] b = new byte[] { mData[mPos + 3], mData[mPos + 2], mData[mPos + 1], mData[mPos] };
                v = BitConverter.ToSingle(b, 0);
            }
            mPos += 4;
            return v;
        }
        public string Str()
        {
            int n = U16();
            if (!Take(n)) return "";
            string s = Encoding.UTF8.GetString(mData, mPos, n);
            mPos += n;
            return s;
        }
        public Rd Bytes()
        {
            int n = (int)U32();
            if (!Take(n)) return new Rd(mData, 0, 0);
            Rd r = new Rd(mData, mPos, mPos + n);
            mPos += n;
            return r;
        }
    }
}
