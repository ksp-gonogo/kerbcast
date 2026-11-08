namespace Kerbcast
{
    /* A sight line's answer: whether the target can be seen, and if not what is in the way. */
    internal struct LineOfSightAnswer
    {
        public bool Known;
        public bool Visible;
        /* The first body in the way, or null. */
        public string Occluder;
        /* That body is the one the vantage stands on or orbits. */
        public bool BelowHorizon;
        public double RangeM;
    }

    /*
     * Line of sight from a vantage to a craft at one instant, with every
     * body and the vantage's skyline placed at that instant. Unity-free, so
     * the geometry is tested without the game.
     */
    internal static class LineOfSightMath
    {
        /* Nothing can be said: the vantage or the craft has no place. */
        public static LineOfSightAnswer Unknown => new LineOfSightAnswer { RangeM = double.NaN };

        /* The answer for a line from `station` to `target` whose first blocking body is `occluder` (null for none). */
        public static LineOfSightAnswer Answer(DVec3 station, DVec3 target, string occluder, string standsOn) =>
            new LineOfSightAnswer
            {
                Known = true,
                Visible = occluder == null,
                Occluder = occluder,
                BelowHorizon = occluder != null && occluder == standsOn,
                RangeM = (target - station).Magnitude,
            };

        /*
         * Whether the station's own skyline hides `target`, both placed at
         * now + dt with the body's centre then at `centreAtT`: each point is
         * turned back by the body's spin to the body as it stands now, where
         * the skyline was measured. `axis` is the body's north in world axes.
         */
        public static bool SkylineBlocks(HorizonMask mask, DVec3 station, DVec3 target, DVec3 centreAtT, DVec3 centreNow,
            DVec3 spin, double dt, DVec3 axis, double targetRadius)
        {
            var relS = CommandCameraMath.ToBodyFrameNow(station, centreAtT, centreNow, spin, dt) - centreNow;
            var relT = CommandCameraMath.ToBodyFrameNow(target, centreAtT, centreNow, spin, dt) - centreNow;
            return mask.Blocks(relS, relT, axis, targetRadius);
        }
    }
}
