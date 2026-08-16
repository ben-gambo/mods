using System.IO;
using Blukulele.CHE;
using Gambonanza.GambitApi;
using Gambonanza.ModSdk;
using UnityEngine;

namespace Gambonanza.ImpatientGambit
{
    /// <summary>
    /// Mod entry point. ModHost constructs this from mod.json and calls OnLoad.
    ///
    /// This file only declares the card - name, tooltip, rarity, price, art. All
    /// of the gameplay lives in <see cref="GambitImpatient"/>.
    /// </summary>
    public sealed class ImpatientGambitMod : IMod
    {
        public void OnLoad(IModContext context)
        {
            context.LogLine("[ImpatientGambit] registering the Impatient Gambit.");

            // Optional custom art: impatient.png sits next to mod.json, and
            // build.sh copies it beside the DLL on install. Regenerate it with
            // tools/make_art.py. If it goes missing we draw a crude hourglass in
            // code so the card is still recognisable.
            var spritePath = Path.Combine(context.ModDirectory, "impatient.png");
            var sprite = ModGambitApi.LoadSprite(spritePath) ?? GenerateFallbackSprite();

            var def = GambitBuilder.Create("impatient")
                .WithName("Impatient Gambit")
                // Two short lines, like the vanilla cards. The "starting with the
                // next game" part is left for the player to discover a few seconds
                // later - spelling it out doubled the length of the tooltip.
                .WithDescription(
                    "Only fight <b>BOSSES</b> from now on.<br>" +
                    "All <color=*>gold</color> earned is <color=*>x4</color>.")
                // Run-defining: it collapses 25 games into 5. Legendary, and priced
                // so it is a real decision rather than an obvious pick-up.
                .WithRarity(Rarity.LEGENDARY)
                .WithFocus(Gambit_Focus.MONEY, Gambit_Focus.UTILITY)
                .WithPrice(10)
                .WithVisual(sprite)
                // GambitApi sizes a modded card against the vanilla *template*
                // (28x32), which is at the large end of the real cards. 0.9
                // brings it back in line with what actually sits either side of
                // it on the gambit rail.
                .WithVisualScale(0.9f)
                .WithBaseGambit<GambitImpatient>()
                .AutoUnlock(true)
                .Register();

            context.LogLine($"[ImpatientGambit] registered '{def.Id}'.");
        }

        /// <summary>
        /// Crude stand-in used only when impatient.png is missing: a wooden slab
        /// with two dials on it. Portrait, like the real art, so a missing file
        /// does not also produce a card twice the width of its neighbours.
        /// </summary>
        private static Sprite GenerateFallbackSprite()
        {
            const int w = 24;
            const int h = 28;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var pixels = new Color[w * h];

            var wood = new Color(0.58f, 0.37f, 0.18f, 1f);
            var stone = new Color(0.43f, 0.45f, 0.52f, 1f);
            var metal = new Color(0.61f, 0.64f, 0.70f, 1f);
            var face = new Color(0.97f, 0.94f, 0.84f, 1f);
            var flag = new Color(0.85f, 0.23f, 0.23f, 1f);

            // Texture rows run bottom-up, so this builds from the plinth upward.
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var c = Color.clear;

                    if (y >= 1 && y <= 4 && x >= 4 && x <= 19) c = stone;
                    else if (y >= 5 && y <= 16 && x >= 2 && x <= 21) c = wood;
                    else if (y >= 17 && y <= 22 && x >= 6 && x <= 9) c = metal;   // plunger, up
                    else if (y >= 17 && y <= 19 && x >= 14 && x <= 17) c = metal; // plunger, slammed
                    else if (y >= 19 && y <= 22 && x >= 18 && x <= 20) c = flag;

                    // Two dials punched into the wooden case.
                    foreach (var cx in new[] { 8, 16 })
                    {
                        var dx = x - cx;
                        var dy = y - 11;
                        if (dx * dx + dy * dy <= 9) c = face;
                    }

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
