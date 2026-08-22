using System;
using System.Collections.Generic;
using Blukulele.CHE;
using Blukulele.Core;
using Gambonanza.GameUI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Gambonanza.Coop
{
    /// <summary>
    /// The whole mod driven from the home menu: a "CO-OP" button next to Play that opens a
    /// panel with host / invite / start / leave and a live status line. The console commands
    /// still work, but nobody should have to use them.
    /// </summary>
    internal sealed class CoopMenu
    {
        private const string ButtonName = "__CoopHomeButton";

        private readonly CoopNet _net;
        private readonly CoopSession _session;

        private Modal _modal;
        private Button _hostBtn, _inviteBtn, _startBtn, _leaveBtn;
        private TMP_Text _seatLabel, _peerLabel, _hintLabel;
        private float _refreshClock;
        private bool _buttonInjected;

        public CoopMenu(CoopNet net, CoopSession session)
        {
            _net = net;
            _session = session;
        }

        /// <summary>
        /// Injects the home-menu button. ModHost exposes no OnHomeMenuOpened event to mods,
        /// so we look for a live CanvasMenu instead; Pixel.AddHomeMenuButton is idempotent by
        /// injected name, and we re-arm whenever the menu is rebuilt.
        /// </summary>
        public void Tick()
        {
            var gm = SingletonMonoBehaviour<GameManager>.Instance;
            bool inMenu = gm != null && gm.CurrentState == State.MENU;

            if (!inMenu)
            {
                _buttonInjected = false;      // the menu canvas is torn down between visits
                return;
            }

            _refreshClock -= Time.unscaledDeltaTime;
            if (!_buttonInjected && _refreshClock <= 0f)
            {
                _refreshClock = 0.5f;
                TryInjectButton();
            }

            if (_modal != null && _modal.Root != null && _modal.Root.activeSelf)
                RefreshPanel();
        }

        private void TryInjectButton()
        {
            var menu = UnityEngine.Object.FindAnyObjectByType<CanvasMenu>();
            if (menu == null) return;

            var btn = Pixel.AddHomeMenuButton(menu, "CO-OP", ButtonName, Open);
            if (btn != null)
            {
                _buttonInjected = true;
                CoopLog.Debug("home menu button injected");
            }
        }

        public void Open()
        {
            EnsureModal();
            if (_modal == null) { CoopLog.Warn("could not build the co-op panel."); return; }
            RefreshPanel();
            _modal.Show();
        }

        private void EnsureModal()
        {
            if (_modal != null && _modal.Root != null) return;

            _modal = Pixel.CreateModal("__CoopModal", "CO-OP");
            if (_modal == null) return;

            _seatLabel = Pixel.CreateLabel(_modal.Content, "", 22f);
            _peerLabel = Pixel.CreateLabel(_modal.Content, "", 18f);
            _hintLabel = Pixel.CreateLabel(_modal.Content, "", 15f);

            _hostBtn = Pixel.CreateButton(_modal.Content, "Host a game", () =>
            {
                _net.HostLobby();
                RefreshPanel();
            });

            _inviteBtn = Pixel.CreateButton(_modal.Content, "Invite a friend", () =>
            {
                _net.OpenInviteDialog();
                RefreshPanel();
            });

            _startBtn = Pixel.CreateButton(_modal.Content, "Start the run", () =>
            {
                _session.HostStartRun();
                _modal.Hide();
            });

            _leaveBtn = Pixel.CreateButton(_modal.Content, "Leave", () =>
            {
                _session.EndSession(restoreSave: true);
                _net.LeaveLobby();
                RefreshPanel();
            });

            _modal.AddToolbarButton("Close", () => _modal.Hide());
        }

        private void RefreshPanel()
        {
            if (_modal == null) return;

            bool connected = _net.Connected;
            bool inLobby = _net.LobbyId != Steamworks.CSteamID.Nil;
            bool host = _net.IsHost;
            bool running = _session.Phase == Phase.Running;

            if (_seatLabel != null)
            {
                _seatLabel.text = !inLobby
                    ? "Not in a lobby"
                    : (host ? "You are P1  (red)" : "You are P2  (blue)");
                _seatLabel.color = !inLobby ? Color.white
                    : (host ? CoopVisuals.P1 : CoopVisuals.P2);
            }

            if (_peerLabel != null)
                _peerLabel.text = connected
                    ? $"Playing with {_net.PeerName}"
                    : (inLobby ? "Waiting for a friend to join..." : "");

            if (_hintLabel != null)
            {
                if (running) _hintLabel.text = "Run in progress. P1 moves, P2 moves, then the enemy moves twice.";
                else if (connected && host) _hintLabel.text = "Both of you are in. Start the run whenever you are ready.";
                else if (connected) _hintLabel.text = "Waiting for P1 to start the run.";
                else if (inLobby) _hintLabel.text = "Invite a friend, or have them accept from the Steam overlay.";
                else _hintLabel.text = "Host a game, then invite a friend from Steam.";
            }

            SetActive(_hostBtn, !inLobby);
            SetActive(_inviteBtn, inLobby && host && !running);
            SetActive(_startBtn, connected && host && !running);
            SetActive(_leaveBtn, inLobby);
        }

        private static void SetActive(Button b, bool on)
        {
            if (b != null && b.gameObject != null && b.gameObject.activeSelf != on)
                b.gameObject.SetActive(on);
        }

        public void Teardown()
        {
            if (_modal != null && _modal.Root != null)
                UnityEngine.Object.Destroy(_modal.Root);
            _modal = null;
            _buttonInjected = false;
        }
    }
}
