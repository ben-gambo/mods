using System;
using Blukulele.CHE;
using Blukulele.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Gambonanza.Coop
{
    /// <summary>
    /// The whole mod driven from the home menu: a CO-OP button beside the other menu buttons
    /// that opens a panel with host / invite / start / leave and a live status line.
    /// Every piece is cloned from the game's own menu so it looks and feels native.
    /// The console commands still work, but nobody should have to use them.
    /// </summary>
    internal sealed class CoopMenu
    {
        private const string ButtonName = "__CoopHomeButton";

        private readonly CoopNet _net;
        private readonly CoopSession _session;
        private readonly CoopPanel _panel = new CoopPanel();

        private CoopNativeButton _homeButton;
        private float _retryClock;
        private bool _openOnPeerJoined;

        public CoopMenu(CoopNet net, CoopSession session)
        {
            _net = net;
            _session = session;
            // P2 accepts the invite from the Steam overlay and lands back on the home menu with
            // nothing to show for it - no panel, no seat, no sign the lobby worked. Pop the
            // panel for them (and for the host) the moment the peer resolves.
            _net.OnPeerJoined += () => _openOnPeerJoined = true;
        }

        public void Tick()
        {
            var gm = SingletonMonoBehaviour<GameManager>.Instance;
            bool inMenu = gm != null && gm.CurrentState == State.MENU;

            if (!inMenu)
            {
                // The menu canvas is torn down between visits, taking our clones with it.
                if (_homeButton == null || _homeButton.gameObject == null) _homeButton = null;
                if (_panel.IsOpen) _panel.Hide();
                return;
            }

            if (_homeButton == null)
            {
                _retryClock -= Time.unscaledDeltaTime;
                if (_retryClock <= 0f)
                {
                    _retryClock = 0.5f;
                    TryInjectButton();
                }
            }

            // Wait for the home button: it is the proof that the menu canvas is assembled and
            // that CoopMenuParts.Find() will succeed, so Open() cannot fail in a loop here.
            if (_openOnPeerJoined && _homeButton != null)
            {
                Open();
                if (_panel.IsOpen) _openOnPeerJoined = false;
                return;
            }

            if (_panel.IsOpen) Refresh();
        }

        private void TryInjectButton()
        {
            var parts = CoopMenuParts.Find();
            if (parts == null) return;

            var row = parts.ButtonCell.parent;
            if (row == null) return;
            if (row.Find(ButtonName) != null) return;      // already there

            // Clone a real menu cell so the CO-OP button IS a menu button.
            var cell = UnityEngine.Object.Instantiate(parts.ButtonCell.gameObject, row);
            cell.name = ButtonName;
            cell.transform.SetSiblingIndex(parts.ButtonCell.GetSiblingIndex() + 1);
            cell.SetActive(true);

            _homeButton = CoopNativeButton.Attach(cell, "CO-OP", Open);
            CoopLog.Debug("home menu button injected");

        }

        public void Open()
        {
            if (!_panel.Ensure(
                    onHost: () => { _net.HostLobby(); Refresh(); },
                    onInvite: () => { _net.OpenInviteDialog(); Refresh(); },
                    onStart: () => { _session.HostStartRun(); _panel.Hide(); },
                    onLeave: () => { _session.LeaveParty(); Refresh(); },
                    onClose: () => _panel.Hide()))
            {
                CoopLog.Warn("could not build the co-op panel (menu parts unavailable).");
                return;
            }

            Refresh();
            _panel.Show();
        }

        private void Refresh()
        {
            bool connected = _net.Connected;
            bool inLobby = _net.LobbyId != Steamworks.CSteamID.Nil;
            bool host = _net.IsHost;
            bool running = _session.Phase == Phase.Running;

            string seat = !inLobby ? "Not in a lobby" : (host ? "You are P1" : "You are P2");
            Color seatColor = !inLobby ? new Color(0.35f, 0.30f, 0.25f)
                                       : (host ? CoopVisuals.P1 : CoopVisuals.P2);

            string peer = connected
                ? $"Playing with {_net.PeerName}"
                : (inLobby ? "Waiting for a friend to join..." : "");

            string hint;
            if (running) hint = "Run in progress.  P1 moves, P2 moves, then the enemy moves twice.";
            else if (connected && host) hint = "Both of you are in. Start whenever you are ready.";
            else if (connected) hint = "Waiting for P1 to start the run.";
            else if (inLobby) hint = "Invite a friend, or let them accept from Steam.";
            else hint = "Host a game, then invite a friend from Steam.";

            _panel.SetTexts(seat, seatColor, peer, hint);

            // Hide what does not apply rather than greying it out.
            Set(_panel.Host, !inLobby);
            Set(_panel.Invite, inLobby && host && !running);
            Set(_panel.Start, connected && host && !running);
            Set(_panel.Leave, inLobby);
            Set(_panel.Close, true);
        }

        private static void Set(CoopNativeButton b, bool on)
        {
            if (b != null) b.SetVisible(on);
        }

        public void Teardown()
        {
            _panel.Teardown();
            if (_homeButton != null && _homeButton.gameObject != null)
                UnityEngine.Object.Destroy(_homeButton.gameObject);
            _homeButton = null;
        }
    }
}
