using System;
using System.IO;
using System.Runtime.CompilerServices;
using Blukulele.CHE;
using Gambonanza.GambitApi;
using Gambonanza.ModSdk;
using UnityEngine;

namespace Gambonanza.BedrockGambit
{
    /// <summary>
    /// Mod entry point. ModHost constructs this from mod.json and calls OnLoad.
    ///
    /// This file only declares the card - name, tooltip, rarity, price, art. The
    /// gameplay, all six lines of it, lives in <see cref="GambitBedrock"/>.
    /// </summary>
    public sealed class BedrockGambitMod : IMod
    {
        public void OnLoad(IModContext context)
        {
            // The card is one Crumble.Freeze() handle, so without the Crumble Control
            // API there is nothing to register. ModHost loads dependencies first when
            // they are installed; this is for when they are not.
            if (!CrumbleApiPresent())
            {
                context.LogLine("[BedrockGambit] the CrumbleApi mod is not installed - Bedrock's Gambit needs it. Not registering the card.");
                context.Console?.PrintWarn("Bedrock's Gambit needs the Crumble Control API mod (CrumbleApi). Install it and restart the game.");
                return;
            }

            context.LogLine("[BedrockGambit] registering Bedrock's Gambit.");

            // bedrock.png sits next to mod.json, and build.sh copies it beside the DLL
            // on install. Regenerate it with tools/make_art.py. If it goes missing we
            // draw a plain gray block in code so the card is still recognisable.
            var spritePath = Path.Combine(context.ModDirectory, "bedrock.png");
            var sprite = ModGambitApi.LoadSprite(spritePath) ?? GenerateFallbackSprite();

            var def = GambitBuilder.Create("bedrock")
                .WithName("Bedrock's Gambit")
                // The § marker is the game's wooden colour - the tiles that would
                // have crumbled are the wood in question, same as Fall Guy's FALL.
                .WithDescription("The board never <color=§>CRUMBLES</color> while you hold this.")
                .WithRarity(Rarity.RARE)
                .WithFocus(Gambit_Focus.CRUMBLE, Gambit_Focus.UTILITY)
                // It switches a whole pressure mechanic off for as long as it is held,
                // which is stronger than Fall Guy's rescue (Epic, 8) is passive - so it
                // sits at the top of the Rare band rather than down with the commons.
                .WithPrice(6)
                .WithVisual(sprite)
                // Same canvas conventions as the other cards in this repo (ink
                // cropped tight, bottom flush); 0.9 lands it mid-pack on the rail.
                .WithVisualScale(0.9f)
                .WithBaseGambit<GambitBedrock>()
                .AutoUnlock(true)
                .Register();

            context.LogLine($"[BedrockGambit] registered '{def.Id}'.");
        }

        private static bool CrumbleApiPresent()
        {
            try { ProbeCrumbleApi(); return true; }
            catch (Exception) { return false; }
        }

        // Kept in its own non-inlined method: the runtime only resolves the
        // Gambonanza.CrumbleApi assembly when it compiles this method, so a missing DLL
        // surfaces here as a catchable exception instead of tearing down OnLoad.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ProbeCrumbleApi()
        {
            _ = Gambonanza.CrumbleApi.Crumble.IsBound;
        }

        /// <summary>
        /// Stand-in used only when bedrock.png is missing: a plain isometric gray
        /// block with the three faces shaded the way the real art is, so a missing
        /// file does not also change how the card hangs on the rail.
        /// </summary>
        private static Sprite GenerateFallbackSprite()
        {
            const int w = 24;
            const int h = 27;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var pixels = new Color[w * h];

            var top = new Color(0.45f, 0.45f, 0.47f, 1f);
            var left = new Color(0.34f, 0.34f, 0.36f, 1f);
            var right = new Color(0.24f, 0.24f, 0.26f, 1f);
            var outline = new Color(0.09f, 0.07f, 0.11f, 1f);

            const int cx = 11;   // front vertical edge
            const int half = 9;  // half width of the top rhombus
            const int side = 13; // height of the vertical faces

            // Texture rows run bottom-up (y=0 is the baseline the card stands on).
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var c = Color.clear;
                    var dx = Mathf.Abs(x - cx);
                    var fromTop = (h - 1) - y - 2;              // rows below the 2px top padding
                    if (dx <= half)
                    {
                        var t0 = (dx + 1) / 2;
                        var t1 = half - (dx + 1) / 2;
                        if (fromTop >= t0 && fromTop <= t1) c = top;
                        else if (fromTop > t1 && fromTop <= t1 + side) c = x < cx ? left : right;
                    }
                    pixels[y * w + x] = c;
                }
            }

            // One dark outline around the silhouette, as every vanilla card has.
            var solid = new bool[w * h];
            for (var i = 0; i < pixels.Length; i++) solid[i] = pixels[i].a > 0f;
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    if (solid[y * w + x]) continue;
                    if ((x > 0 && solid[y * w + x - 1]) || (x < w - 1 && solid[y * w + x + 1]) ||
                        (y > 0 && solid[(y - 1) * w + x]) || (y < h - 1 && solid[(y + 1) * w + x]))
                        pixels[y * w + x] = outline;
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
            tex.filterMode = FilterMode.Point;
            return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f);
        }
    }
}
