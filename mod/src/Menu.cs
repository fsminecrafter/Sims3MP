// The F9 "server management" menu. Runs inside a game task (dialogs block).
using System.Collections.Generic;

namespace Sims3MP
{
    public sealed class Menu
    {
        readonly Session mS;
        readonly IGame mG;
        string mName = "";
        bool mOpen;

        public Menu(Session session, IGame game) { mS = session; mG = game; }

        /// <summary>Shows a numbered list and returns the chosen index, or -1.</summary>
        int Choose(string title, List<string> options)
        {
            string text = "";
            for (int i = 0; i < options.Count; i++) text += (i + 1) + ". " + options[i] + "\n";
            string answer = mG.Ask(title, text + "\nType a number:", "");
            int n;
            if (answer == null || !int.TryParse(answer.Trim(), out n) || n < 1 || n > options.Count) return -1;
            return n - 1;
        }

        string AskName()
        {
            if (mS.PlayerName.Length > 0) return mS.PlayerName;
            string name = mG.Ask("Sims3MP", "Your player name (1-24 characters):", mName.Length > 0 ? mName : "Player");
            if (name == null) return null;
            name = name.Trim();
            if (name.Length < 1 || name.Length > 24) { mG.Message("Sims3MP", "That name is not valid."); return null; }
            mName = name;
            return name;
        }

        public void Open()
        {
            if (mOpen) return; // F9 pressed again while a dialog is up
            mOpen = true;
            try { Show(); }
            finally { mOpen = false; }
        }

        void Show()
        {
            if (!mS.BridgeUp)
            {
                mG.Message("Sims3MP", "The Sims3MP bridge is not running.\n\nCheck that MonoPatcher is installed and that Sims3MPNative.dll, sims3mp-bridge.exe and sims3mp-server.exe are in Game\\Bin\\MonoPatcher\\plugins.");
                return;
            }
            List<string> o = new List<string>();
            List<VoidHandler> a = new List<VoidHandler>();
            bool connected = mS.Phase != Msg.PhaseIdle && mS.Phase != Msg.PhaseError && mS.Phase != Msg.PhaseConnecting;

            if (!connected)
            {
                o.Add("Host on this PC (start a server here)"); a.Add(HostLocal);
                o.Add("Connect to a server as admin"); a.Add(delegate { Join(true); });
                o.Add("Join a server"); a.Add(delegate { Join(false); });
            }
            else
            {
                if (mS.Admin) { o.Add("Copy world and start hosting"); a.Add(CopyWorld); }
                if (mS.Phase == Msg.PhaseWorldReady && !mS.InSession && mG.InWorld)
                {
                    o.Add("Enter the session (I have loaded '" + mS.SaveName + "')"); a.Add(mS.EnterManually);
                }
                if (mS.InSession)
                {
                    o.Add("Players"); a.Add(PlayersMenu);
                    o.Add("Lots and builders"); a.Add(LotsMenu);
                    o.Add("Choose household"); a.Add(HouseholdMenu);
                    o.Add("Chat"); a.Add(delegate { string t = mG.Ask("Chat", "Message:", ""); if (!string.IsNullOrEmpty(t)) mS.Chat(t); });
                    o.Add("Voice: microphone " + (mS.MicOn ? "ON" : "OFF")); a.Add(delegate { mS.SetVoice(!mS.MicOn, mS.SpeakerOn); });
                    o.Add("Voice: speakers " + (mS.SpeakerOn ? "ON" : "OFF")); a.Add(delegate { mS.SetVoice(mS.MicOn, !mS.SpeakerOn); });
                    if (mS.Admin) { o.Add("Game speed (admin)"); a.Add(SpeedMenu); }
                }
                o.Add("Disconnect"); a.Add(delegate { if (mG.Confirm("Disconnect from the server?")) mS.Disconnect(); });
            }
            int pick = Choose("Sims3MP - " + mS.Status, o);
            if (pick >= 0) a[pick]();
        }

        void HostLocal()
        {
            string name = AskName();
            if (name == null) return;
            string port = mG.Ask("Host on this PC", "Port (other players connect to your IP address and this port; forward it for TCP and UDP):", "38920");
            ushort p;
            if (port == null || !ushort.TryParse(port.Trim(), out p) || p == 0) return;
            mS.StartLocalServer(p, name);
            mG.Notify("Sims3MP: starting the server. Press F9 again and choose 'Copy world and start hosting'.");
        }

        void Join(bool admin)
        {
            string name = AskName();
            if (name == null) return;
            string addr = mG.Ask("Connect", "Server address (host or host:port):", "");
            if (string.IsNullOrEmpty(addr)) return;
            string pass = mG.Ask("Connect", admin ? "Admin password:" : "Server password (leave empty if none):", "");
            if (pass == null) return;
            mS.Connect(addr.Trim(), name, pass);
        }

        void CopyWorld()
        {
            if (!mG.InWorld) { mG.Message("Sims3MP", "Load your world first."); return; }
            string save = mG.Ask("Copy world and start hosting",
                "Save the game first (the server gets the last SAVED state).\n\nName of the save to host:", mG.SaveName);
            if (string.IsNullOrEmpty(save)) return;
            if (mS.InSession && !mG.Confirm("This replaces the world on the server. Other players will have to reload it. Continue?")) return;
            mS.HostWorld(save.Trim());
        }

        void PlayersMenu()
        {
            List<string> o = new List<string>();
            foreach (KeyValuePair<uint, string> p in mS.Players) o.Add(p.Value);
            int pick = Choose(mS.Admin ? "Players (pick one to kick)" : "Players", o);
            if (pick >= 0 && mS.Admin && o[pick] != mS.PlayerName && mG.Confirm("Kick " + o[pick] + "?")) mS.Kick(o[pick]);
        }

        string PickPlayer(string title, List<string> from)
        {
            int pick = Choose(title, from);
            return pick < 0 ? null : from[pick];
        }

        void LotsMenu()
        {
            ulong lot = mG.BuildLot;
            if (lot == 0) { mG.Message("Lots", "Move the camera over a lot first."); return; }
            LotInfo info;
            mS.Lots.TryGetValue(lot, out info);
            string owner = info == null ? "" : info.Owner;
            bool mine = owner == mS.PlayerName || (mS.Admin && owner.Length > 0);
            string title = mG.LotName(lot) + " - " + (owner.Length == 0 ? "not owned" : "owner: " + owner
                + (info.Builders.Count > 0 ? ", builders: " + string.Join(", ", info.Builders.ToArray()) : ""));

            List<string> o = new List<string>();
            List<VoidHandler> a = new List<VoidHandler>();
            if (owner.Length == 0) { o.Add("Claim this lot"); a.Add(delegate { mS.ClaimLot(lot); }); }
            if (mine)
            {
                o.Add("Add a builder"); a.Add(delegate
                {
                    List<string> c = new List<string>();
                    foreach (string n in mS.Players.Values) if (n != owner && !info.Builders.Contains(n)) c.Add(n);
                    string who = PickPlayer("Add a builder", c);
                    if (who != null) mS.SetBuilder(lot, who, true);
                });
                if (info.Builders.Count > 0)
                {
                    o.Add("Remove a builder"); a.Add(delegate
                    {
                        string who = PickPlayer("Remove a builder", info.Builders);
                        if (who != null) mS.SetBuilder(lot, who, false);
                    });
                }
                o.Add("Give up this lot"); a.Add(delegate { if (mG.Confirm("Release ownership of this lot?")) mS.ReleaseLot(lot); });
            }
            int pick = Choose(title, o);
            if (pick >= 0) a[pick]();
        }

        void HouseholdMenu()
        {
            List<NamedId> all = mG.Households();
            List<NamedId> free = new List<NamedId>();
            List<string> o = new List<string>();
            foreach (NamedId h in all)
            {
                string owner;
                if (mS.Households.TryGetValue(h.Id, out owner) && owner != mS.PlayerName) continue;
                free.Add(h); o.Add(h.Name);
            }
            int pick = Choose("Choose the household you want to play", o);
            if (pick >= 0 && !mG.SwitchHousehold(free[pick].Id)) mG.Message("Sims3MP", "Could not switch to that household.");
        }

        void SpeedMenu()
        {
            List<string> o = new List<string>(new string[] {
                "Automatic (1x, 3x while everyone sleeps)", "Pause", "1x", "2x", "3x" });
            byte[] modes = new byte[] { Msg.SpeedAuto, 0, 1, 2, 3 };
            int pick = Choose("Game speed for everyone", o);
            if (pick >= 0) mS.SetSpeed(modes[pick]);
        }
    }
}
