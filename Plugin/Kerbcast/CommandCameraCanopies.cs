using System;
using System.Collections.Generic;
using UnityEngine;

namespace Kerbcast
{
    /*
     * The open parachute canopies of loaded craft, which reach well past
     * their parts' origins. Stock ModuleParachute and the modules built on
     * it (a kerbal's chute): a canopy counts while its chute is semi or
     * fully deployed and its model is shown, measured by its renderers'
     * world bounds. Every transform carrying the canopy's name counts, since
     * a kerbal's part holds one per chute model and only the fitted one is
     * shown. Each craft's chutes are found once and found again when the
     * cache goes stale (CommandCameraMath.ChutesStale).
     */
    internal sealed class CommandCameraCanopies
    {
        private const int Cap = 64;

        private sealed class Chute
        {
            public ModuleParachute Module;
            public readonly List<Renderer> Renderers = new List<Renderer>();
            public CommandCameraMath.ChuteState LastState;
            public float LastAnimTime = -1f;
        }

        private sealed class CraftChutes
        {
            public int PartCount;
            public Part Root;
            /* What the last measure saw: a cached object destroyed, or a chute newly opened. */
            public bool Gone, Opened;
            public readonly List<Chute> Chutes = new List<Chute>();
        }

        private readonly Dictionary<uint, CraftChutes> _craft = new Dictionary<uint, CraftChutes>();

        /* Canopies open on the craft last measured. */
        public int Open { get; private set; }
        /* One of those is still unfolding, so its reach is still growing. */
        public bool Unfolding { get; private set; }

        /* The farthest any open canopy of `v` reaches from `com`, 0 with none open, with each canopy's bounds put in `extent` about `com` when given. */
        public double Reach(Vessel v, DVec3 com, CraftExtent extent = null)
        {
            Open = 0;
            Unfolding = false;
            extent?.Clear();
            if (v == null || !v.loaded || v.parts == null) return 0;
            var craft = Find(v);
            double reach = Measure(craft, com, extent);
            if (CommandCameraMath.ChutesStale(craft.PartCount, v.parts.Count, craft.Root == v.rootPart, craft.Gone, craft.Opened))
            {
                Rebuild(craft, v);
                reach = Measure(craft, com, extent);
            }
            return reach;
        }

        private double Measure(CraftChutes craft, DVec3 com, CraftExtent extent)
        {
            Open = 0;
            Unfolding = false;
            extent?.Clear();
            craft.Gone = false;
            craft.Opened = false;
            double reach = 0;
            var chutes = craft.Chutes;
            for (int i = 0; i < chutes.Count; i++)
            {
                var chute = chutes[i];
                var m = chute.Module;
                if (m == null)
                {
                    craft.Gone = true;
                    continue;
                }
                var state = StateOf(m.deploymentState);
                bool open = CommandCameraMath.CanopyOpen(state);
                if (open && !CommandCameraMath.CanopyOpen(chute.LastState)) craft.Opened = true;
                chute.LastState = state;
                if (!open) continue;
                bool shown = false;
                var renderers = chute.Renderers;
                for (int r = 0; r < renderers.Count; r++)
                {
                    var rend = renderers[r];
                    if (rend == null)
                    {
                        craft.Gone = true;
                        continue;
                    }
                    /* An inactive renderer reports empty bounds at the world origin. */
                    if (!rend.gameObject.activeInHierarchy) continue;
                    var b = rend.bounds;
                    if (b.extents == Vector3.zero) continue;
                    shown = true;
                    var centre = new DVec3(b.center.x, b.center.y, b.center.z);
                    var extents = new DVec3(b.extents.x, b.extents.y, b.extents.z);
                    reach = Math.Max(reach, CommandCameraMath.BoxReach(centre, extents, com));
                    extent?.AddBox(centre - com, extents);
                }
                if (!shown) continue;
                Open++;
                if (CommandCameraMath.CanopyUnfolding(m.animTime, chute.LastAnimTime)) Unfolding = true;
                chute.LastAnimTime = m.animTime;
            }
            return reach;
        }

        private CraftChutes Find(Vessel v)
        {
            if (_craft.TryGetValue(v.persistentId, out var c)) return c;
            if (_craft.Count >= Cap) _craft.Clear();
            c = new CraftChutes();
            _craft[v.persistentId] = c;
            Rebuild(c, v);
            return c;
        }

        /* A chute is kept with no canopy renderers yet: a kerbal's chute model may be fitted later, and its opening rebuilds the cache. */
        private static void Rebuild(CraftChutes c, Vessel v)
        {
            var parts = v.parts;
            c.PartCount = parts.Count;
            c.Root = v.rootPart;
            c.Gone = false;
            c.Opened = false;
            c.Chutes.Clear();
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p == null) continue;
                var m = p.FindModuleImplementing<ModuleParachute>();
                if (m == null) continue;
                var chute = new Chute { Module = m, LastState = StateOf(m.deploymentState) };
                foreach (var canopy in p.FindModelTransforms(m.canopyName))
                {
                    if (canopy == null) continue;
                    foreach (var rend in canopy.GetComponentsInChildren<Renderer>(true))
                        if (!(rend is ParticleSystemRenderer)) chute.Renderers.Add(rend);
                }
                c.Chutes.Add(chute);
            }
        }

        private static CommandCameraMath.ChuteState StateOf(ModuleParachute.deploymentStates s)
        {
            switch (s)
            {
                case ModuleParachute.deploymentStates.ACTIVE: return CommandCameraMath.ChuteState.Active;
                case ModuleParachute.deploymentStates.SEMIDEPLOYED: return CommandCameraMath.ChuteState.SemiDeployed;
                case ModuleParachute.deploymentStates.DEPLOYED: return CommandCameraMath.ChuteState.Deployed;
                case ModuleParachute.deploymentStates.CUT: return CommandCameraMath.ChuteState.Cut;
                default: return CommandCameraMath.ChuteState.Stowed;
            }
        }
    }
}
