using System.IO;
using Blukulele.CHE;
using Gambonanza.GambitApi;
using Gambonanza.ModSdk;
using UnityEngine;

namespace Gambonanza.MoonGambit
{
    /// <summary>
    /// Mod entry point. ModHost constructs this from mod.json and calls OnLoad.
    ///
    /// Registers two cards and one secret:
    ///  - "moon": a legendary that does nothing whatsoever. That is the joke,
    ///    and the joke is the hint.
    ///  - "eclipse": starts locked, never explained. Drag the Moon onto the
    ///    vanilla Sun in the gambit tray (or the Sun onto the Moon) and the two
    ///    merge into it - which is also what unlocks it in the collection.
    ///
    /// The card declarations live here; the merge gesture is watched by
    /// <see cref="MergeWatcher"/> and the Eclipse's gameplay lives in
    /// <see cref="GambitEclipse"/>.
    /// </summary>
    public sealed class MoonGambitMod : IMod
    {
        public void OnLoad(IModContext context)
        {
            context.LogLine("[MoonGambit] registering the Moon and Eclipse gambits.");

            var moonSprite = ModGambitApi.LoadSprite(Path.Combine(context.ModDirectory, "moon.png"));
            var eclipseSprite = ModGambitApi.LoadSprite(Path.Combine(context.ModDirectory, "eclipse.png"));

            GambitBuilder.Create(MergeWatcher.MoonId)
                .WithName("Moon Gambit")
                // The whole card. No effect, no condition, no number. Priced
                // like a legendary so buying it stays a real (terrible) decision.
                .WithDescription("Surely this does<br>something... <i>right?</i>")
                .WithRarity(Rarity.LEGENDARY)
                .WithFocus(Gambit_Focus.UTILITY)
                .WithPrice(8)
                .WithVisual(moonSprite)
                .WithVisualScale(0.9f)
                .WithBaseGambit<GambitMoon>()
                .AutoUnlock(true)
                .Register();

            GambitBuilder.Create(MergeWatcher.EclipseId)
                .WithName("Eclipse's Gambit")
                .WithDescription(
                    "Earning a <b>KING</b> turns a tile <color=*>GOLDEN</color>.<br>" +
                    "<color=*>GOLDEN</color> tiles behave like <b>EVERY</b> tile.")
                .WithRarity(Rarity.LEGENDARY)
                .WithFocus(Gambit_Focus.GOLDEN, Gambit_Focus.UTILITY)
                .WithPrice(12)
                .WithVisual(eclipseSprite)
                .WithVisualScale(0.9f)
                .WithBaseGambit<GambitEclipse>()
                // Locked until the first merge: MergeWatcher calls
                // ModGambitApi.Unlock when Moon and Sun first touch, and the
                // game's own unlock notification takes it from there.
                .AutoUnlock(false)
                // The collection info panel explains each tile type the card
                // touches, which doubles as the only documentation of what
                // "EVERY tile" means.
                .ShowGoldenTile().ShowBlessedTile().ShowProtectedTile()
                .ShowTrapTile().ShowPhantomTile()
                .Register();

            // The watcher outlives scenes and runs; it only wakes up while a
            // gambit is actually being dragged.
            var watcher = new GameObject("[MoonGambit] MergeWatcher");
            Object.DontDestroyOnLoad(watcher);
            watcher.AddComponent<MergeWatcher>();

            context.LogLine("[MoonGambit] registered 'moon' and 'eclipse'; merge watcher armed.");
        }
    }
}
