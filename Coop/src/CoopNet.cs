using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Steamworks;
using UnityEngine;

namespace Gambonanza.Coop
{
    /// <summary>
    /// Steam plumbing: friends-only lobby + SteamNetworkingMessages P2P.
    /// Channel 0 = reliable game state, channel 1 = unreliable cursor stream.
    /// SteamAPI.RunCallbacks() is already pumped by the game's SteamManager.
    /// </summary>
    internal sealed class CoopNet
    {
        public const int ChannelState = 0;
        public const int ChannelCursor = 1;

        public CSteamID LobbyId { get; private set; } = CSteamID.Nil;
        public CSteamID PeerId { get; private set; } = CSteamID.Nil;
        public bool IsHost { get; private set; }
        public bool Connected => PeerId != CSteamID.Nil;

        public Action<string> OnLog;                       // console/log line
        public Action OnPeerJoined;                        // both sides, once peer known
        public Action OnPeerLeft;
        public Action<string> OnStateMessage;              // decoded channel-0 payload
        public Action<string> OnCursorMessage;             // decoded channel-1 payload

        private Callback<GameLobbyJoinRequested_t> _cbJoinRequested;
        private Callback<LobbyEnter_t> _cbLobbyEnter;
        private Callback<LobbyChatUpdate_t> _cbChatUpdate;
        private Callback<SteamNetworkingMessagesSessionRequest_t> _cbSessionRequest;
        private CallResult<LobbyCreated_t> _crLobbyCreated;
        private readonly IntPtr[] _recvBuf = new IntPtr[64];
        private bool _installed;

        /// <summary>
        /// Retries Install() while Steam is still coming up. Mods load during GameManager.Start,
        /// which can be before SteamManager finishes initializing - without this the invite
        /// callbacks would never register and accepting a Steam invite would do nothing.
        /// </summary>
        public void EnsureInstalled()
        {
            if (_installed) return;
            if (Time.unscaledTime < _nextInstallRetry) return;
            _nextInstallRetry = Time.unscaledTime + 2f;
            Install();
        }

        private float _nextInstallRetry;
        private bool _warnedNoSteam;

        public void Install()
        {
            if (_installed) return;
            if (!SteamManager.Initialized)
            {
                if (!_warnedNoSteam)
                {
                    _warnedNoSteam = true;
                    OnLog?.Invoke("Steam is not initialized yet - co-op will arm itself once it is. "
                                + "(Launch the game through Steam if this persists.)");
                }
                return;
            }
            _cbJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);
            _cbLobbyEnter = Callback<LobbyEnter_t>.Create(OnLobbyEnter);
            _cbChatUpdate = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
            _cbSessionRequest = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
            _crLobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
            SteamNetworkingUtils.InitRelayNetworkAccess();
            _installed = true;
            OnLog?.Invoke("Steam ready - co-op armed. Use 'coop host' or accept a friend's invite.");
        }

        public void Teardown()
        {
            LeaveLobby();
            _cbJoinRequested?.Dispose();
            _cbLobbyEnter?.Dispose();
            _cbChatUpdate?.Dispose();
            _cbSessionRequest?.Dispose();
            _crLobbyCreated?.Dispose();
            _cbJoinRequested = null; _cbLobbyEnter = null; _cbChatUpdate = null;
            _cbSessionRequest = null; _crLobbyCreated = null;
            _installed = false;
        }

        // ---- lobby ----

        public void HostLobby()
        {
            Install();
            if (!_installed) return;
            if (LobbyId != CSteamID.Nil) { OnLog?.Invoke($"already in lobby {LobbyId.m_SteamID}"); return; }
            IsHost = true;
            _crLobbyCreated.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, 2));
            OnLog?.Invoke("creating Steam lobby...");
        }

        public void JoinLobby(ulong lobbyId)
        {
            Install();
            if (!_installed) return;
            IsHost = false;
            SteamMatchmaking.JoinLobby(new CSteamID(lobbyId));
            OnLog?.Invoke($"joining lobby {lobbyId}...");
        }

        public void OpenInviteDialog()
        {
            if (LobbyId == CSteamID.Nil) { OnLog?.Invoke("host a lobby first (coop host)."); return; }
            SteamFriends.ActivateGameOverlayInviteDialog(LobbyId);
        }

        public void LeaveLobby()
        {
            if (PeerId != CSteamID.Nil)
            {
                var id = IdentityOf(PeerId);
                SteamNetworkingMessages.CloseSessionWithUser(ref id);
            }
            if (LobbyId != CSteamID.Nil) SteamMatchmaking.LeaveLobby(LobbyId);
            LobbyId = CSteamID.Nil;
            PeerId = CSteamID.Nil;
            IsHost = false;
        }

        private void OnLobbyCreated(LobbyCreated_t cb, bool ioFailure)
        {
            if (ioFailure || cb.m_eResult != EResult.k_EResultOK)
            {
                OnLog?.Invoke($"lobby creation failed ({cb.m_eResult})");
                return;
            }
            LobbyId = new CSteamID(cb.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(LobbyId, "gmb_coop", "1");
            OnLog?.Invoke($"lobby ready ({LobbyId.m_SteamID}). Use 'coop invite' or the Steam overlay (Shift+Tab) to invite a friend.");
        }

        private void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t cb)
        {
            OnLog?.Invoke($"accepting invite from {SteamFriends.GetFriendPersonaName(cb.m_steamIDFriend)}...");
            IsHost = false;
            SteamMatchmaking.JoinLobby(cb.m_steamIDLobby);
        }

        private void OnLobbyEnter(LobbyEnter_t cb)
        {
            if (cb.m_EChatRoomEnterResponse != 1)
            {
                OnLog?.Invoke($"could not enter lobby (response {cb.m_EChatRoomEnterResponse})");
                return;
            }
            LobbyId = new CSteamID(cb.m_ulSteamIDLobby);
            if (!IsHost) OnLog?.Invoke("entered lobby, waiting for handshake...");
            ResolvePeer();
        }

        private void OnLobbyChatUpdate(LobbyChatUpdate_t cb)
        {
            if (cb.m_ulSteamIDLobby != LobbyId.m_SteamID) return;
            const uint entered = 1; // k_EChatMemberStateChangeEntered
            if ((cb.m_rgfChatMemberStateChange & entered) != 0)
            {
                ResolvePeer();
            }
            else
            {
                var gone = new CSteamID(cb.m_ulSteamIDUserChanged);
                if (gone == PeerId)
                {
                    PeerId = CSteamID.Nil;
                    OnLog?.Invoke("peer left the lobby.");
                    OnPeerLeft?.Invoke();
                }
            }
        }

        private void ResolvePeer()
        {
            if (LobbyId == CSteamID.Nil || PeerId != CSteamID.Nil) return;
            var me = SteamUser.GetSteamID();
            int n = SteamMatchmaking.GetNumLobbyMembers(LobbyId);
            for (int i = 0; i < n; i++)
            {
                var m = SteamMatchmaking.GetLobbyMemberByIndex(LobbyId, i);
                if (m != me)
                {
                    PeerId = m;
                    OnLog?.Invoke($"peer connected: {SteamFriends.GetFriendPersonaName(m)}");
                    OnPeerJoined?.Invoke();
                    return;
                }
            }
        }

        private void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t cb)
        {
            var remote = cb.m_identityRemote;
            var sid = remote.GetSteamID();
            // only accept sessions from our lobby peer
            if (PeerId != CSteamID.Nil && sid == PeerId)
                SteamNetworkingMessages.AcceptSessionWithUser(ref remote);
            else if (LobbyId != CSteamID.Nil)
            {
                // peer may not be resolved yet; accept any lobby member
                int n = SteamMatchmaking.GetNumLobbyMembers(LobbyId);
                for (int i = 0; i < n; i++)
                    if (SteamMatchmaking.GetLobbyMemberByIndex(LobbyId, i) == sid)
                    {
                        SteamNetworkingMessages.AcceptSessionWithUser(ref remote);
                        ResolvePeer();
                        return;
                    }
            }
        }

        // ---- messaging ----

        private static SteamNetworkingIdentity IdentityOf(CSteamID id)
        {
            var ident = default(SteamNetworkingIdentity);
            ident.SetSteamID(id);
            return ident;
        }

        public void Send(string payload, bool reliable = true, int channel = ChannelState)
        {
            if (!Connected) return;
            var bytes = Encoding.UTF8.GetBytes(payload);
            var ptr = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, ptr, bytes.Length);
                var ident = IdentityOf(PeerId);
                int flags = reliable
                    ? Constants.k_nSteamNetworkingSend_Reliable | Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession
                    : Constants.k_nSteamNetworkingSend_UnreliableNoDelay | Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession;
                var res = SteamNetworkingMessages.SendMessageToUser(ref ident, ptr, (uint)bytes.Length, flags, channel);
                if (reliable && res != EResult.k_EResultOK)
                    Debug.LogWarning($"[Coop] send failed ({res}): {Truncate(payload)}");
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        public void Pump()
        {
            if (!_installed) return;
            PumpChannel(ChannelState, OnStateMessage);
            PumpChannel(ChannelCursor, OnCursorMessage);
        }

        private void PumpChannel(int channel, Action<string> handler)
        {
            int n = SteamNetworkingMessages.ReceiveMessagesOnChannel(channel, _recvBuf, _recvBuf.Length);
            for (int i = 0; i < n; i++)
            {
                try
                {
                    var msg = SteamNetworkingMessage_t.FromIntPtr(_recvBuf[i]);
                    var sender = msg.m_identityPeer.GetSteamID();
                    if (PeerId == CSteamID.Nil || sender == PeerId)
                    {
                        var buf = new byte[msg.m_cbSize];
                        Marshal.Copy(msg.m_pData, buf, 0, msg.m_cbSize);
                        handler?.Invoke(Encoding.UTF8.GetString(buf));
                    }
                }
                catch (Exception ex) { Debug.LogError($"[Coop] recv error: {ex.Message}"); }
                finally { SteamNetworkingMessage_t.Release(_recvBuf[i]); }
            }
        }

        private static string Truncate(string s) => s.Length > 60 ? s.Substring(0, 60) + "…" : s;
    }
}
