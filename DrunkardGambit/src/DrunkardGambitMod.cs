using System.IO;
using Blukulele.CHE;
using Gambonanza.GambitApi;
using Gambonanza.ModSdk;
using UnityEngine;

namespace Gambonanza.DrunkardGambit
{
    /// <summary>
    /// Mod entry point. ModHost constructs this from mod.json and calls OnLoad.
    ///
    /// This file only declares the card - name, tooltip, rarity, price, art. All
    /// of the gameplay lives in <see cref="GambitDrunkard"/>.
    /// </summary>
    public sealed class DrunkardGambitMod : IMod
    {
        public void OnLoad(IModContext context)
        {
            context.LogLine("[DrunkardGambit] registering Drunkard's Gambit.");

            // Optional custom art: drunkard.png sits next to mod.json, and
            // build.sh copies it beside the DLL on install. Regenerate it with
            // tools/make_art.py. If it goes missing we draw a crude tipsy pawn
            // in code so the card is still recognisable.
            var spritePath = Path.Combine(context.ModDirectory, "drunkard.png");
            var sprite = ModGambitApi.LoadSprite(spritePath) ?? GenerateFallbackSprite();

            var def = GambitBuilder.Create("drunkard")
                .WithName("Drunkard's Gambit")
                // The © marker is the game's landing orange - where the piece
                // lands is the whole card. ∆ is the bright green: SAFE means
                // the stagger never lands on an enemy-threatened square.
                .WithDescription(
                    "After <b>CAPTURING</b>, the piece staggers<br>to a <color=©>RANDOM</color> <color=∆>SAFE</color> empty tile.")
                .WithRarity(Rarity.RARE)
                .WithFocus(Gambit_Focus.LANDING, Gambit_Focus.UTILITY)
                // Priced like a coin-flip: the stagger dodges retaliation as
                // often as it walks the piece into it.
                .WithPrice(6)
                .WithVisual(sprite)
                .WithVisualScale(0.9f)
                .WithBaseGambit<GambitDrunkard>()
                .AutoUnlock(true)
                .Register();

            context.LogLine($"[DrunkardGambit] registered '{def.Id}'.");
        }

        /// <summary>
        /// Crude stand-in used only when drunkard.png is missing: a pawn caught
        /// mid-lean with a bottle at its side. Same canvas proportions as the
        /// real art so a missing file does not also change how the card hangs.
        /// </summary>
        private static Sprite GenerateFallbackSprite()
        {
            const int w = 24;
            const int h = 26;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var pixels = new Color[w * h];

            var ivory = new Color(0.95f, 0.92f, 0.82f, 1f);
            var shade = new Color(0.78f, 0.72f, 0.58f, 1f);
            var rosy = new Color(0.91f, 0.54f, 0.48f, 1f);
            var glass = new Color(0.24f, 0.56f, 0.29f, 1f);

            // Texture rows run bottom-up (y=0 is the baseline the card stands on).
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var c = Color.clear;

                    // The pawn, leaning right: head offset from the body axis.
                    var dx = x - 13;
                    var dy = y - 18;
                    if (dx * dx + dy * dy <= 12) c = ivory;                        // head, off-axis
                    else if (y >= 13 && y <= 14 && x >= 9 && x <= 16) c = shade;   // collar
                    else if (y >= 5 && y <= 12 && x >= 10 + (12 - y) / 4 && x <= 13 + (12 - y) / 4) c = ivory; // slanted body
                    else if (y >= 1 && y <= 4 && x >= 7 && x <= 16) c = ivory;     // base
                    if (y >= 17 && y <= 18 && x == 15) c = rosy;                   // flushed cheek

                    // The bottle, standing on the base beside the pawn.
                    if (x >= 19 && x <= 21 && y >= 2 && y <= 8) c = glass;
                    else if (x == 20 && y >= 9 && y <= 11) c = glass;

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
