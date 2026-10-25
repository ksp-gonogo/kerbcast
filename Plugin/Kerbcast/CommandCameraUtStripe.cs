using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Kerbcast
{
    /*
     * Burns the UT a command camera frame was rendered at into its bottom edge, so a
     * recording can be lined up with game time frame by frame. Layout (see
     * CommandCameraDebugUtStripe in KerbcastSettings): from the bottom-left
     * corner, a row of BlockPx x BlockPx blocks, white for 1 and black for 0:
     * the start pattern 1,0,1,0, then round(UT * 1000) as 48 bits, most
     * significant first.
     */
    internal sealed class CommandCameraUtStripe : IDisposable
    {
        internal const int BlockPx = 8;
        private static readonly bool[] StartPattern = { true, false, true, false };

        private readonly Material _mat;

        private CommandCameraUtStripe(Material mat)
        {
            _mat = mat;
        }

        public static CommandCameraUtStripe Create(uint flightId)
        {
            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                Debug.LogWarning($"[Kerbcast][CMDCAM] cam={flightId} UT stripe off: Hidden/Internal-Colored not found");
                return null;
            }
            var mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            mat.SetInt("_SrcBlend", (int)BlendMode.One);
            mat.SetInt("_DstBlend", (int)BlendMode.Zero);
            mat.SetInt("_Cull", (int)CullMode.Off);
            mat.SetInt("_ZWrite", 0);
            mat.SetInt("_ZTest", (int)CompareFunction.Always);
            Debug.Log($"[Kerbcast][CMDCAM] cam={flightId} UT stripe on: {StartPattern.Length}+{CommandCameraMath.UtStripeBits} "
                + $"blocks of {BlockPx}px along the bottom edge, round(UT*1000) MSB first");
            return new CommandCameraUtStripe(mat);
        }

        public void Draw(RenderTexture target, double ut)
        {
            if (target == null || _mat == null) return;
            ulong value = CommandCameraMath.UtStripeValue(ut);
            var prev = RenderTexture.active;
            RenderTexture.active = target;
            try
            {
                _mat.SetPass(0);
                GL.PushMatrix();
                GL.LoadPixelMatrix(0, target.width, 0, target.height);
                GL.Begin(GL.QUADS);
                int block = 0;
                for (int i = 0; i < StartPattern.Length; i++) Block(block++, StartPattern[i]);
                for (int i = 0; i < CommandCameraMath.UtStripeBits; i++) Block(block++, CommandCameraMath.UtStripeBit(value, i));
                GL.End();
                GL.PopMatrix();
            }
            finally
            {
                RenderTexture.active = prev;
            }
        }

        private static void Block(int index, bool on)
        {
            GL.Color(on ? Color.white : Color.black);
            float x = index * BlockPx;
            GL.Vertex3(x, 0f, 0f);
            GL.Vertex3(x + BlockPx, 0f, 0f);
            GL.Vertex3(x + BlockPx, BlockPx, 0f);
            GL.Vertex3(x, BlockPx, 0f);
        }

        public void Dispose()
        {
            if (_mat != null) UnityEngine.Object.Destroy(_mat);
        }
    }
}
