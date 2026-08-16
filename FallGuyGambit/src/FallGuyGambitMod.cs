using System.IO;
using Blukulele.CHE;
using Gambonanza.GambitApi;
using Gambonanza.ModSdk;
using UnityEngine;

namespace Gambonanza.FallGuyGambit
{
    /// <summary>
    /// Mod entry point. ModHost constructs this from mod.json and calls OnLoad.
    ///
    /// This file only declares the card - name, tooltip, rarity, price, art. All
    /// of the gameplay lives in <see cref="GambitFallGuy"/>.
    /// </summary>
    public sealed class FallGuyGambitMod : IMod
    {
        public void OnLoad(IModContext context)
        {
            context.LogLine("[FallGuyGambit] registering Fall Guy's Gambit.");

            // Optional custom art: fallguy.png sits next to mod.json, and
            // build.sh copies it beside the DLL on install. Regenerate it with
            // tools/make_art.py. If it goes missing we draw a crude pawn-over-a-
            // safety-net in code so the card is still recognisable.
            var spritePath = Path.Combine(context.ModDirectory, "fallguy.png");
            var sprite = ModGambitApi.LoadSprite(spritePath) ?? GenerateFallbackSprite();

            var def = GambitBuilder.Create("fallguy")
                .WithName("Fall Guy's Gambit")
                // The § marker is the game's wooden colour - the boards' falling
                // tiles are the wood in question. Two lines, like vanilla cards,
                // with the user-facing rules in priority order.
                .WithDescription(
                    "Once per game, a piece about to <color=§>FALL</color> is saved to the nearest free square, or the stash.<br>" +
                    "<i>(Nowhere to go? It dies lol)</i>")
                .WithRarity(Rarity.EPIC)
                .WithFocus(Gambit_Focus.CRUMBLE, Gambit_Focus.UTILITY)
                // Same price as the other EPIC in the family (Kamikaze): a life
                // saved once per game is strong but passive.
                .WithPrice(8)
                .WithVisual(sprite)
                // The art uses the vanilla template's exact canvas (28x32,
                // inked edge-to-edge, bottom-heavy) so GambitApi's rescale and
                // pivot copy land it precisely where a vanilla card sits. The
                // template (Addiction) is the largest card in the game; 0.9
                // puts this one mid-pack among its rail neighbours.
                .WithVisualScale(0.9f)
                .WithBaseGambit<GambitFallGuy>()
                .AutoUnlock(true)
                .Register();

            context.LogLine($"[FallGuyGambit] registered '{def.Id}'.");
        }

        /// <summary>
        /// Crude stand-in used only when fallguy.png is missing: a pawn caught
        /// in a rescue net slung between two poles. Same 28x32 canvas as the
        /// vanilla template sprite and inked edge-to-edge like vanilla cards,
        /// so a missing file does not also change where the card hangs.
        /// </summary>
        private static Sprite GenerateFallbackSprite()
        {
            const int w = 28;
            const int h = 32;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var pixels = new Color[w * h];

            var ivory = new Color(0.95f, 0.92f, 0.82f, 1f);
            var shade = new Color(0.78f, 0.72f, 0.58f, 1f);
            var wood = new Color(0.58f, 0.37f, 0.18f, 1f);
            var net = new Color(0.85f, 0.23f, 0.23f, 1f);

            // Texture rows run bottom-up (y=0 is the baseline the card stands on).
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var c = Color.clear;

                    // Two full-height poles with flared feet on the baseline.
                    if (y <= 1 && (x <= 4 || x >= 23)) c = wood;
                    else if (y >= 2 && y <= 17 && ((x >= 1 && x <= 3) || (x >= 24 && x <= 26))) c = wood;

                    // The net: a parabola resting on the pole tops (y=17),
                    // sagging 7 rows in the middle where the pawn sits.
                    var t = (x - 13.5f) / 9.5f;
                    var sag = Mathf.RoundToInt(7f * Mathf.Max(0f, 1f - t * t));
                    var netY = 17 - sag;
                    if (x >= 4 && x <= 23 && y <= netY && y >= netY - 2) c = net;

                    // The pawn, base sunk into the sag.
                    if (y >= 9 && y <= 11 && x >= 9 && x <= 18) c = ivory;       // base
                    else if (y >= 12 && y <= 15 && x >= 11 && x <= 16) c = ivory; // body
                    else if (y >= 16 && y <= 17 && x >= 10 && x <= 17) c = shade; // collar
                    var dx = x - 14;
                    var dy = y - 21;
                    if (dx * dx + dy * dy <= 10) c = ivory;                      // head

                    pixels[y * w + x] = c;
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
            tex.filterMode = FilterMode.Point;
            return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f);
        }
    }
}
