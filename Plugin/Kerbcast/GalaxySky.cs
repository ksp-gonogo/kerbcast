using UnityEngine;

namespace Kerbcast
{
    /* The game's galaxy cube as GalaxyBlackout needs it: the tint KSP gave it
       this frame, and whether it is the only thing a galaxy camera draws. One
       instance serves every camera, so the tint is read once a frame however
       many cameras capture. */
    internal static class GalaxySky
    {
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private static MaterialPropertyBlock s_block;
        private static GalaxyCubeControl s_cube;
        private static Vessel s_vessel;
        private static Renderer[] s_faces;
        private static int s_scannedMask;
        private static bool s_scanned;
        private static bool s_alone;
        private static int s_tintFrame = -1;
        private static bool s_tinted;
        private static Color s_tint;

        /// <summary>Whether clearing <paramref name="galaxyCam"/>'s target to its
        /// background gives the frame its render would, this frame, with the
        /// cube tinted <paramref name="tint"/> instead of as KSP left it when one is given.</summary>
        public static bool CanClear(Camera galaxyCam, Color? tint = null)
        {
            if (!Cube()) return false;
            /* A vessel switch can load renderers the scene did not have when it was scanned. */
            var vessel = FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
            if (vessel != s_vessel)
            {
                s_vessel = vessel;
                s_scanned = false;
            }
            int mask = galaxyCam.cullingMask;
            if (!s_scanned || mask != s_scannedMask)
            {
                s_alone = FacesAloneOn(mask);
                s_scannedMask = mask;
                s_scanned = true;
                Debug.Log($"[Kerbcast] galaxy layer: clear={galaxyCam.clearFlags} background={galaxyCam.backgroundColor}, {s_faces.Length} cube faces, "
                    + (s_alone ? "nothing else on the layer, so a black sky is cleared rather than rendered" : "other renderers on the layer, so it always renders"));
            }
            ReadTint();
            var bg = galaxyCam.backgroundColor;
            bool tinted = tint.HasValue ? s_faces.Length > 0 : s_tinted;
            var t = tint ?? s_tint;
            return GalaxyBlackout.CanClear(galaxyCam.clearFlags == CameraClearFlags.SolidColor, bg.r, bg.g, bg.b,
                s_alone, tinted, t.r, t.g, t.b);
        }

        private static bool Cube()
        {
            var cube = GalaxyCubeControl.Instance;
            if (cube == null || !cube.isActiveAndEnabled) return false;
            if (cube != s_cube)
            {
                s_cube = cube;
                s_faces = cube.GetComponentsInChildren<Renderer>();
                s_scanned = false;
            }
            return true;
        }

        private static readonly System.Collections.Generic.List<Color> s_pushed = new System.Collections.Generic.List<Color>();
        private static MaterialPropertyBlock s_pushBlock;

        /* Tints every cube face `tint` for one render; PopTint puts KSP's tint back. Read the stock tint (CanClear) before this, not between. */
        public static void PushTint(Color tint)
        {
            s_pushed.Clear();
            if (!Cube()) return;
            if (s_pushBlock == null) s_pushBlock = new MaterialPropertyBlock();
            for (int i = 0; i < s_faces.Length; i++)
            {
                var face = s_faces[i];
                if (face == null) { s_pushed.Add(Color.clear); continue; }
                face.GetPropertyBlock(s_pushBlock);
                s_pushed.Add(s_pushBlock.isEmpty ? Color.clear : s_pushBlock.GetColor(ColorId));
                s_pushBlock.SetColor(ColorId, tint);
                face.SetPropertyBlock(s_pushBlock);
            }
        }

        public static void PopTint()
        {
            if (s_faces == null) return;
            for (int i = 0; i < s_pushed.Count && i < s_faces.Length; i++)
            {
                var face = s_faces[i];
                if (face == null) continue;
                face.GetPropertyBlock(s_pushBlock);
                s_pushBlock.SetColor(ColorId, s_pushed[i]);
                face.SetPropertyBlock(s_pushBlock);
            }
            s_pushed.Clear();
        }

        /* The cube's tint for a fade of `fade`, between the colours KSP fades it between; null with no cube. */
        public static Color? TintFor(float fade)
        {
            var cube = GalaxyCubeControl.Instance;
            if (cube == null) return null;
            return Color.Lerp(cube.maxGalaxyColor, cube.minGalaxyColor, fade);
        }

        /* The limits KSP's cube fades the stars by, so a lens fades them as stock would where it stands. */
        public static bool Limits(out float atmos, out float daytime, out float airPressure, out float glare)
        {
            var cube = GalaxyCubeControl.Instance;
            atmos = cube != null ? cube.atmosFadeLimit : 0f;
            daytime = cube != null ? cube.daytimeFadeLimit : 0f;
            airPressure = cube != null ? cube.airPressureFade : 0f;
            glare = cube != null ? cube.glareFadeLimit : 0f;
            return cube != null;
        }

        /* Once per cube, mask and active vessel: a full renderer scan is too slow
           for every frame, and nothing in the stock game joins the galaxy layer
           later. */
        private static bool FacesAloneOn(int mask)
        {
            var faces = new System.Collections.Generic.HashSet<Renderer>(s_faces);
            foreach (var r in Object.FindObjectsOfType<Renderer>())
            {
                if ((mask & (1 << r.gameObject.layer)) == 0) continue;
                if (!faces.Contains(r)) return false;
            }
            return true;
        }

        /* KSP sets the tint on every face in Update, so by the capture in LateUpdate it is this frame's. */
        private static void ReadTint()
        {
            int frame = Time.frameCount;
            if (frame == s_tintFrame) return;
            s_tintFrame = frame;
            s_tinted = false;
            if (s_block == null) s_block = new MaterialPropertyBlock();
            var tint = Color.black;
            for (int i = 0; i < s_faces.Length; i++)
            {
                var face = s_faces[i];
                if (face == null) return;
                face.GetPropertyBlock(s_block);
                if (s_block.isEmpty) return;
                var c = s_block.GetColor(ColorId);
                tint.r = Mathf.Max(tint.r, c.r);
                tint.g = Mathf.Max(tint.g, c.g);
                tint.b = Mathf.Max(tint.b, c.b);
            }
            s_tint = tint;
            s_tinted = s_faces.Length > 0;
        }
    }
}
