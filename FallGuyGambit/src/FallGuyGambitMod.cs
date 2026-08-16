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
                // tiles are the wood in question.
                .WithDescription(
                    "Pieces about to <color=§>FALL</color> are saved to the nearest free square, or the stash.")
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
        /// Crude stand-in used only when fallguy.png is missing: the guardian
        /// angel pawn - halo, stubby wings, nothing underneath. Same canvas
        /// proportions as the real art so a missing file does not also change
        /// how the card hangs.
        /// </summary>
        private static Sprite GenerateFallbackSprite()
        {
            const int w = 24;
            const int h = 26;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var pixels = new Color[w * h];

            var ivory = new Color(0.95f, 0.92f, 0.82f, 1f);
            var shade = new Color(0.78f, 0.72f, 0.58f, 1f);
            var white = new Color(0.97f, 0.95f, 0.91f, 1f);
            var gold = new Color(0.91f, 0.72f, 0.23f, 1f);

            // Texture rows run bottom-up (y=0 is the baseline the card stands on).
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var c = Color.clear;

                    // Halo floating above everything.
                    if (y >= 22 && y <= 24 && x >= 8 && x <= 16 && !(y == 23 && x >= 10 && x <= 14)) c = gold;

                    // The pawn: wide head, narrow body, flared base.
                    var dx = x - 12;
                    var dy = y - 17;
                    if (dx * dx + dy * dy <= 12) c = ivory;                       // head
                    else if (y >= 12 && y <= 13 && x >= 9 && x <= 16) c = shade;  // collar
                    else if (y >= 5 && y <= 11 && x >= 11 && x <= 14) c = ivory;  // body
                    else if (y >= 1 && y <= 4 && x >= 8 && x <= 17) c = ivory;    // base

                    // Wings, flared out from the collar with a gap to the body.
                    if (y >= 8 && y <= 10 && (x <= 6 || x >= 18)) c = white;
                    else if (y >= 11 && y <= 13 && ((x >= 3 && x <= 7) || (x >= 17 && x <= 21))) c = white;

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
