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
                // GambitApi sizes a modded card against the vanilla template
                // (28x32), which is at the large end of the real cards. 0.9
                // keeps it in line with its rail neighbours.
                .WithVisualScale(0.9f)
                .WithBaseGambit<GambitFallGuy>()
                .AutoUnlock(true)
                .Register();

            context.LogLine($"[FallGuyGambit] registered '{def.Id}'.");
        }

        /// <summary>
        /// Crude stand-in used only when fallguy.png is missing: a pawn dropping
        /// toward a rescue net stretched between two poles. Portrait, like the
        /// real art, so a missing file does not also produce a card twice the
        /// width of its neighbours.
        /// </summary>
        private static Sprite GenerateFallbackSprite()
        {
            const int w = 24;
            const int h = 28;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var pixels = new Color[w * h];

            var ivory = new Color(0.95f, 0.92f, 0.82f, 1f);
            var shade = new Color(0.78f, 0.72f, 0.58f, 1f);
            var wood = new Color(0.58f, 0.37f, 0.18f, 1f);
            var net = new Color(0.85f, 0.23f, 0.23f, 1f);

            // Texture rows run bottom-up: net first, pawn above it.
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var c = Color.clear;

                    // Two poles holding the net.
                    if (x >= 2 && x <= 3 && y >= 1 && y <= 8) c = wood;
                    else if (x >= 20 && x <= 21 && y >= 1 && y <= 8) c = wood;
                    // The net itself, sagging one pixel in the middle.
                    else if (y >= 5 && y <= 7 && x >= 4 && x <= 19)
                    {
                        var sag = (x >= 9 && x <= 14) ? 1 : 0;
                        if (y == 7 - sag || ((x + y) % 2 == 0 && y == 6 - sag)) c = net;
                    }

                    // The falling pawn: head, collar, body, base.
                    var dx = x - 12;
                    if (dx * dx + (y - 23) * (y - 23) <= 6) c = ivory;          // head
                    else if (y >= 19 && y <= 20 && x >= 9 && x <= 15) c = shade; // collar
                    else if (y >= 14 && y <= 18 && x >= 10 && x <= 14) c = ivory; // body
                    else if (y >= 12 && y <= 13 && x >= 8 && x <= 16) c = shade; // base

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
