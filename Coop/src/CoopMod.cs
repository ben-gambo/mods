using System;
using System.Collections;
using Blukulele.CHE;
using Blukulele.Core;
using Gambonanza.ModSdk;
using UnityEngine;

namespace Gambonanza.Coop
{
    /// <summary>
    /// Two-player co-op over Steam P2P. One player hosts, the other joins.
    /// Both share one board and one shop; each owns their own pieces (red P1 / blue P2).
    /// Turn order per round: P1 moves, P2 moves, then the enemy moves twice.
    /// Post-battle income is halved to keep the economy fair for two.
    /// </summary>
    public sealed class CoopMod : IMod, IModLifecycle
    {
        public const string ModVersion = "0.0.1";

        private IModContext _context;
        private CoopRunner _runner;

        public void OnLoad(IModContext context)
        {
            _context = context;
            CoopLog.Console = context?.Console;
            context?.LogLine($"co-op v{ModVersion} loaded. Use the CO-OP button in the main menu.");
        }

        public void OnEnable()
        {
            if (_runner != null) return;
            var go = new GameObject("__GambonanzaCoopRunner");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            _runner = go.AddComponent<CoopRunner>();
            _runner.Bind(_context);
            RegisterCommands();
            _context?.LogLine("co-op enabled.");
        }

        public void OnDisable()
        {
            UnregisterCommands();
            if (_runner != null)
            {
                _runner.TearDown();
                UnityEngine.Object.Destroy(_runner.gameObject);
                _runner = null;
            }
            _context?.LogLine("co-op disabled.");
        }

        private void RegisterCommands()
        {
            var c = _context?.Console;
            if (c == null) return;

            c.RegisterCommand("coop", "co-op: menu | host | join <lobbyId> | invite | start | status | leave | verbose",
                args =>
                {
                    if (_runner == null) { c.PrintError("co-op runner is not active."); return; }
                    if (args.Length == 0) { c.PrintInfo(_runner.Session.Status()); PrintHelp(c); return; }

                    switch (args[0].ToLowerInvariant())
                    {
                        case "host":
                            _runner.Net.HostLobby();
                            break;
                        case "join":
                            if (args.Length < 2 || !ulong.TryParse(args[1], out var lobby))
                                c.PrintError("usage: coop join <lobbyId>  (or just accept a Steam invite)");
                            else _runner.Net.JoinLobby(lobby);
                            break;
                        case "invite":
                            _runner.Net.OpenInviteDialog();
                            break;
                        case "start":
                            _runner.Session.HostStartRun();
                            break;
                        case "menu":
                            _runner.OpenMenu();
                            break;
                        case "status":
                            c.PrintInfo(_runner.Session.Status());
                            break;
                        case "leave":
                            _runner.Session.EndSession(restoreSave: true);
                            _runner.Net.LeaveLobby();
                            break;
                        case "verbose":
                            CoopLog.Verbose = !CoopLog.Verbose;
                            c.PrintInfo($"co-op verbose logging: {(CoopLog.Verbose ? "on" : "off")}");
                            break;
                        default:
                            PrintHelp(c);
                            break;
                    }
                },
                (args, idx) => idx == 0
                    ? new[] { "menu", "host", "join", "invite", "start", "status", "leave", "verbose" }
                    : Array.Empty<string>());
        }

        private static void PrintHelp(IConsoleApi c)
        {
            c.PrintInfo("coop menu            - open the co-op panel (also the CO-OP button in the main menu)");
            c.PrintInfo("coop host            - create a friends-only Steam lobby");
            c.PrintInfo("coop invite          - open the Steam invite overlay");
            c.PrintInfo("coop join <lobbyId>  - join by id (accepting an invite works too)");
            c.PrintInfo("coop start           - host only: begin a synced run");
            c.PrintInfo("coop status          - show session state");
            c.PrintInfo("coop leave           - end the session and restore your solo save");
        }

        private void UnregisterCommands() => _context?.Console?.UnregisterCommand("coop");
    }

    /// <summary>Drives the session: pumps Steam, gates input, re-applies tints after the game's own writes.</summary>
    [DefaultExecutionOrder(int.MaxValue - 32)]
    internal sealed class CoopRunner : MonoBehaviour
    {
        public CoopNet Net { get; private set; }
        public CoopSession Session { get; private set; }
        private CoopVisuals _visuals;
        private CoopMenu _menu;
        private Action<BasePieceBehaviour> _selectHandler;
        private bool _hookedSelect;

        public void Bind(IModContext context)
        {
            _visuals = new CoopVisuals();
            _visuals.Build();

            Net = new CoopNet { OnLog = CoopLog.Info };
            Session = new CoopSession(Net, _visuals, this);
            _menu = new CoopMenu(Net, Session);
            Net.Install();

            // Diagnostic: GAMBONANZA_COOP_AUTOHOST=1 creates a lobby at boot so the Steam
            // path can be verified from the log without opening the console.
            // Diagnostic marker file works under a Steam launch, where env vars do not
            // propagate to the game process.
            bool autoHost = Environment.GetEnvironmentVariable("GAMBONANZA_COOP_AUTOHOST") == "1";
            if (!autoHost && context != null && !string.IsNullOrEmpty(context.ModDirectory))
                autoHost = System.IO.File.Exists(System.IO.Path.Combine(context.ModDirectory, "autohost"));
            if (autoHost) StartCoroutine(AutoHost());
        }

        private IEnumerator AutoHost()
        {
            yield return new WaitForSeconds(6f);
            CoopLog.Info("co-op autohost enabled - creating a lobby for diagnostics.");
            Net.HostLobby();
            yield return new WaitForSeconds(6f);
            CoopLog.Info($"autohost result: {Session.Status()}");
        }

        private void Update()
        {
            Net?.EnsureInstalled();
            Net?.Pump();
            Session?.Tick();
            _menu?.Tick();
            HookSelectionOnce();
        }

        private void LateUpdate()
        {
            // PieceVisualEffect.Update() repaints the piece sprite every frame, so tints
            // have to be re-applied after it - LateUpdate always wins that race.
            _visuals?.ApplyTints();
        }

        private void HookSelectionOnce()
        {
            if (_hookedSelect) return;
            var sel = SingletonMonoBehaviour<SelectionManager>.Instance;
            if (sel == null) return;
            _selectHandler = piece => Session?.NotePickup(piece);
            sel.OnSelect = (Action<BasePieceBehaviour>)Delegate.Combine(sel.OnSelect, _selectHandler);
            _hookedSelect = true;
        }

        public void OpenMenu() => _menu?.Open();

        public void TearDown()
        {
            var sel = SingletonMonoBehaviour<SelectionManager>.Instance;
            if (sel != null && _selectHandler != null)
                sel.OnSelect = (Action<BasePieceBehaviour>)Delegate.Remove(sel.OnSelect, _selectHandler);
            _selectHandler = null;
            _hookedSelect = false;

            _menu?.Teardown();
            Session?.EndSession(restoreSave: true);
            Net?.Teardown();
            _visuals?.Teardown();
        }

        private void OnDestroy() => TearDown();
    }
}
