using System.Collections.Generic;
using Blukulele.CHE;
using Blukulele.Core;
using TMPro;
using UnityEngine;

namespace Gambonanza.Coop
{
    /// <summary>
    /// All co-op rendering: per-tile P1/P2 selection badges, ownership tints on pieces,
    /// and the remote player's cursor. Everything is mod-owned so it never fights the
    /// game's own highlight renderers.
    /// </summary>
    internal sealed class CoopVisuals
    {
        // P1 = red (host), P2 = blue (guest)
        public static readonly Color P1 = new Color(0.93f, 0.28f, 0.28f);
        public static readonly Color P2 = new Color(0.29f, 0.55f, 0.96f);

        private const int BadgeOrder = 9;     // under pieces (10) but above tile feedback (base+2)
        private const int CursorOrder = 300;  // above everything world-space
        private const float TintStrength = 0.16f;

        private GameObject _localBadge, _remoteBadge, _remoteCursor;
        private SpriteRenderer _localBadgeSr, _remoteBadgeSr, _remoteCursorSr;
        private TextMeshPro _localBadgeTxt, _remoteBadgeTxt;
        private Sprite _squareSprite, _dotSprite;
        private TMP_FontAsset _font;

        private readonly Dictionary<BasePieceBehaviour, int> _owners = new Dictionary<BasePieceBehaviour, int>();

        public void Build()
        {
            _squareSprite = MakeFrameSprite(24, 3);
            _dotSprite = MakeDiscSprite(16);
            _font = FindFont();

            _localBadge = MakeBadge("__CoopBadgeLocal", out _localBadgeSr, out _localBadgeTxt);
            _remoteBadge = MakeBadge("__CoopBadgeRemote", out _remoteBadgeSr, out _remoteBadgeTxt);

            _remoteCursor = new GameObject("__CoopRemoteCursor");
            Object.DontDestroyOnLoad(_remoteCursor);
            _remoteCursor.hideFlags = HideFlags.HideAndDontSave;
            _remoteCursorSr = _remoteCursor.AddComponent<SpriteRenderer>();
            _remoteCursorSr.sprite = _dotSprite;
            _remoteCursorSr.sortingOrder = CursorOrder;
            _remoteCursor.SetActive(false);
        }

        public void Teardown()
        {
            ClearTints();
            if (_localBadge) Object.Destroy(_localBadge);
            if (_remoteBadge) Object.Destroy(_remoteBadge);
            if (_remoteCursor) Object.Destroy(_remoteCursor);
            _localBadge = _remoteBadge = _remoteCursor = null;

            // Runtime sprites and their textures are native objects with no owning asset -
            // destroying the renderers does not free them, so each disable/enable cycle would
            // orphan another pair. (_font is a shared game asset: leave it alone.)
            if (_squareSprite != null) { Object.Destroy(_squareSprite.texture); Object.Destroy(_squareSprite); _squareSprite = null; }
            if (_dotSprite != null) { Object.Destroy(_dotSprite.texture); Object.Destroy(_dotSprite); _dotSprite = null; }
        }

        private GameObject MakeBadge(string name, out SpriteRenderer sr, out TextMeshPro txt)
        {
            var go = new GameObject(name);
            Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = _squareSprite;
            sr.sortingOrder = BadgeOrder;

            var label = new GameObject("label");
            label.transform.SetParent(go.transform, false);
            // bottom-left corner of the tile; tile pitch is 1.0 world unit
            label.transform.localPosition = new Vector3(-0.34f, -0.34f, -0.01f);
            txt = label.AddComponent<TextMeshPro>();
            txt.text = "P1";
            txt.fontSize = 2.2f;
            txt.alignment = TextAlignmentOptions.Center;
            txt.textWrappingMode = TextWrappingModes.NoWrap;
            txt.raycastTarget = false;
            if (_font != null) txt.font = _font;
            var mr = label.GetComponent<MeshRenderer>();
            if (mr != null) mr.sortingOrder = BadgeOrder + 1;
            var rt = txt.rectTransform;
            rt.sizeDelta = new Vector2(0.5f, 0.3f);

            go.SetActive(false);
            return go;
        }

        // ---- badges ----

        /// <summary>Shows a seat's badge over a tile; pass null to hide.</summary>
        public void ShowBadge(bool remote, TileBehaviour tile, int seat)
        {
            var go = remote ? _remoteBadge : _localBadge;
            var sr = remote ? _remoteBadgeSr : _localBadgeSr;
            var txt = remote ? _remoteBadgeTxt : _localBadgeTxt;
            if (go == null) return;

            if (tile == null) { go.SetActive(false); return; }

            var color = seat == 0 ? P1 : P2;
            sr.color = new Color(color.r, color.g, color.b, remote ? 0.95f : 0.75f);
            txt.text = seat == 0 ? "P1" : "P2";
            txt.color = color;

            var p = tile.transform.position;
            go.transform.position = new Vector3(p.x, p.y, -3f);
            go.SetActive(true);
        }

        public void HideBadges()
        {
            if (_localBadge) _localBadge.SetActive(false);
            if (_remoteBadge) _remoteBadge.SetActive(false);
        }

        public void ShowRemoteCursor(Vector2 worldPos, int seat)
        {
            if (_remoteCursor == null) return;
            var c = seat == 0 ? P1 : P2;
            _remoteCursorSr.color = new Color(c.r, c.g, c.b, 0.85f);
            _remoteCursor.transform.position = new Vector3(worldPos.x, worldPos.y, -4f);
            if (!_remoteCursor.activeSelf) _remoteCursor.SetActive(true);
        }

        public void HideRemoteCursor()
        {
            if (_remoteCursor) _remoteCursor.SetActive(false);
        }

        // ---- ownership tints ----

        public void SetOwner(BasePieceBehaviour piece, int seat)
        {
            if (piece == null) return;
            _owners[piece] = seat;
        }

        public bool TryGetOwner(BasePieceBehaviour piece, out int seat) => _owners.TryGetValue(piece, out seat);

        public void ForgetOwner(BasePieceBehaviour piece)
        {
            if (piece == null) return;
            _owners.Remove(piece);
            _baseColor.Remove(piece);
            _lastWritten.Remove(piece);
        }

        public void ClearOwners()
        {
            _owners.Clear();
            _baseColor.Clear();
            _lastWritten.Clear();
        }

        // Per-piece memory of the untinted colour and of what we last wrote, so the tint is
        // idempotent. PieceVisualEffect.Update() normally resets the sprite to white every
        // frame (PieceVisualEffect.cs:752-765) - but only while !m_Disappear, and it drives a
        // serialized renderer that is not guaranteed to be the same object as Renderer on
        // every prefab. Without this, lerping from the read-back colour would compound each
        // frame and saturate the piece completely.
        private readonly Dictionary<BasePieceBehaviour, Color> _baseColor = new Dictionary<BasePieceBehaviour, Color>();
        private readonly Dictionary<BasePieceBehaviour, Color> _lastWritten = new Dictionary<BasePieceBehaviour, Color>();

        /// <summary>
        /// Re-applies tints. Must run in LateUpdate so it lands after the game's own
        /// per-frame colour writes.
        /// </summary>
        public void ApplyTints()
        {
            if (_owners.Count == 0) return;
            var dead = ListPool();
            foreach (var kv in _owners)
            {
                var piece = kv.Key;
                if (piece == null || piece.IsDead) { dead.Add(piece); continue; }
                var sr = piece.Renderer;
                if (sr == null) continue;

                var cur = sr.color;

                // If the colour still matches what we wrote, the game did not repaint this
                // frame - reuse the remembered base instead of tinting our own output again.
                Color baseCol;
                if (_lastWritten.TryGetValue(piece, out var prev) && Approximately(cur, prev)
                    && _baseColor.TryGetValue(piece, out var remembered))
                {
                    baseCol = remembered;
                }
                else
                {
                    baseCol = cur;             // the game (or a gambit) set a fresh colour
                    _baseColor[piece] = cur;
                }

                var tint = kv.Value == 0 ? P1 : P2;
                var mixed = Color.Lerp(baseCol, tint, TintStrength);
                var final = new Color(mixed.r, mixed.g, mixed.b, baseCol.a);   // keep phantom alpha
                sr.color = final;
                _lastWritten[piece] = final;
            }
            for (int i = 0; i < dead.Count; i++)
            {
                _owners.Remove(dead[i]);
                _baseColor.Remove(dead[i]);
                _lastWritten.Remove(dead[i]);
            }
            dead.Clear();
        }

        private static bool Approximately(Color a, Color b)
            => Mathf.Abs(a.r - b.r) < 0.002f && Mathf.Abs(a.g - b.g) < 0.002f
            && Mathf.Abs(a.b - b.b) < 0.002f && Mathf.Abs(a.a - b.a) < 0.002f;

        public void ClearTints()
        {
            foreach (var kv in _owners)
            {
                var piece = kv.Key;
                var sr = piece != null ? piece.Renderer : null;
                if (sr == null) continue;
                // restore the colour the game had before we tinted it
                var restore = _baseColor.TryGetValue(piece, out var b) ? b : new Color(1f, 1f, 1f, sr.color.a);
                sr.color = restore;
            }
            _owners.Clear();
            _baseColor.Clear();
            _lastWritten.Clear();
        }

        private static List<BasePieceBehaviour> _scratch;
        private static List<BasePieceBehaviour> ListPool()
            => _scratch ?? (_scratch = new List<BasePieceBehaviour>(8));

        // ---- sprite/font helpers ----

        private static Sprite MakeFrameSprite(int size, int border)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            var clear = new Color(1, 1, 1, 0);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    bool edge = x < border || y < border || x >= size - border || y >= size - border;
                    tex.SetPixel(x, y, edge ? Color.white : clear);
                }
            tex.Apply();
            // 24px across one 1.0-unit tile => 24 pixels per unit
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
        }

        private static Sprite MakeDiscSprite(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            float r = size / 2f, cx = r - 0.5f, cy = r - 0.5f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = x - cx, dy = y - cy;
                    bool inside = dx * dx + dy * dy <= (r - 1f) * (r - 1f);
                    tex.SetPixel(x, y, inside ? Color.white : new Color(1, 1, 1, 0));
                }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size * 2f);
        }

        private static TMP_FontAsset FindFont()
        {
            var all = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            if (all != null && all.Length > 0)
            {
                foreach (var f in all)
                    if (f != null && !f.name.Contains("Japanese") && !f.name.Contains("Korean") && !f.name.Contains("Chinese"))
                        return f;
                return all[0];
            }
            return null;
        }
    }
}
