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
        /// background gives the frame its render would, this frame.</summary>
        public static bool CanClear(Camera galaxyCam)
        {
            var cube = GalaxyCubeControl.Instance;
            if (cube == null || !cube.isActiveAndEnabled) return false;
            if (cube != s_cube)
            {
                s_cube = cube;
                s_faces = cube.GetComponentsInChildren<Renderer>();
                s_scanned = false;
            }
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
            return GalaxyBlackout.CanClear(galaxyCam.clearFlags == CameraClearFlags.SolidColor, bg.r, bg.g, bg.b,
                s_alone, s_tinted, s_tint.r, s_tint.g, s_tint.b);
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
