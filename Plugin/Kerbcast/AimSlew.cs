using System;

namespace Kerbcast
{
    /*
     * Turns a command camera onto a moved aim point instead of snapping to it.
     * The caller says when the aim may have jumped (a new aim, a new subject,
     * a new world-model publish, a new vantage); between jumps the view
     * follows the aim exactly, however fast it moves. At a jump the view
     * carries on with the aim's own motion from the last smooth step, and
     * whatever separates that from the new aim becomes an offset, turned out
     * at a speed that eases in, is capped, and eases out onto the aim. A
     * publish that agrees with the motion leaves no offset, and one under a
     * quarter of a pixel is taken at once. A view idle
     * for longer than SnapAfterS starts on its aim, as a first frame does.
     * A long frame turns it no further than a usual one (see FrameStep).
     * Real time drives the turn, so warp does not hurry it. The aim's own
     * motion is learned and carried per UT instead: the subject moves only
     * on a physics tick, so a rendered frame between ticks would learn it as
     * stopped, and one after a tick as too fast. The rig's framing offset is
     * the exception: it eases on every rendered frame, so a carried rate can
     * miss its ease since the last tick. That miss is small against the
     * field and is turned out like any other offset, never snapped to.
     */
    internal sealed class AimSlew
    {
        public const double MaxRateDegPerS = 45.0;
        public const double AccelDegPerS2 = 90.0;
        public const double SnapAfterS = 1.0;
        /* An offset under this fraction of the vertical field (about a quarter pixel at 1080 rows) is not slewed. */
        public const double MinOffsetFieldFraction = 1.0 / 4320.0;
        /*
         * A jump that leaves less than this fraction of the field over is the
         * aim's own motion as much as a correction, so the rate is learned from
         * it too: a world model publishing on every solve leaves no smooth step
         * to learn from. Before any rate is known, a jump of FirstLearnFields
         * fields or less is learned from and followed; a larger one is slewed
         * as from a still view.
         */
        public const double LearnFieldFraction = 0.02;
        public const double FirstLearnFields = 0.5;
        private const double Deg2Rad = Math.PI / 180.0;

        private bool _has;
        private DVec3 _lastAim, _lastView;
        /* The aim's own turn, as a rotation vector per UT second, from its last step that was not a jump or was one close to the motion. */
        private DVec3 _rate;
        private bool _rateKnown;
        /* The view's offset from the aim, as a rotation vector, and the speed it is being turned out at. */
        private DVec3 _offset;
        private double _speed;
        private readonly FrameStep _frames;
        private readonly bool _learnsFrames;

        public AimSlew() : this(new FrameStep(SnapAfterS), true) { }

        /* A slew whose usual frame `frames` learns elsewhere, from the rendered frames only. */
        public AimSlew(FrameStep frames) : this(frames, false) { }

        private AimSlew(FrameStep frames, bool learns)
        {
            _frames = frames;
            _learnsFrames = learns;
        }

        /* Radians the view is still off its aim. */
        public double OffsetRad => _offset.Magnitude;
        public bool Slewing => _offset.Magnitude > 0;

        public void Reset()
        {
            _has = false;
            _offset = _rate = default;
            _rateKnown = false;
            _speed = 0;
        }

        /* The direction to look along this step, given the unit direction to the aim, `dt` real seconds and `dut` UT seconds since the last step, whether the aim may have jumped since then, and the field in degrees. */
        public DVec3 Step(DVec3 aim, double dt, double dut, bool jumped, double fovDeg)
        {
            if (!_has || !(dt >= 0) || dt > SnapAfterS)
            {
                Reset();
                _has = true;
                _lastAim = _lastView = aim;
                return aim;
            }
            double field = fovDeg * Deg2Rad;
            var moved = RotationVector(_lastAim, aim);
            /* UT that stood still or went back carries nothing and teaches nothing. */
            bool ticked = dut > 0;
            if (jumped && !_rateKnown && moved.Magnitude <= FirstLearnFields * field)
            {
                /*
                 * Nothing to carry the view on with yet: a step the size of
                 * motion is learned from and followed. One made while UT
                 * stands, as in a pause, is still followed at once, as it was
                 * when this went by real time; it just teaches nothing.
                 */
                if (ticked) Learn(moved, dut);
                _offset = default;
                _speed = 0;
            }
            else if (jumped)
            {
                /* The view carries on with the aim's own motion; only what is left over is turned out, keeping any speed already turning. */
                var carried = ticked ? Turn(_lastView, _rate * dut) : _lastView;
                _offset = RotationVector(aim, carried);
                if (ticked && _offset.Magnitude < LearnFieldFraction * field) Learn(moved, dut);
                if (_offset.Magnitude < MinOffsetFieldFraction * field) _offset = default;
                if (_offset.Magnitude == 0) _speed = 0;
            }
            else if (ticked) Learn(moved, dut);

            /* The aim's own motion is carried for the whole gap; the offset turns out by a usual frame at most, so a hitch never finishes a turn at once. */
            double step = _learnsFrames ? _frames.Next(dt) : _frames.Clip(dt);
            double left = _offset.Magnitude;
            if (left > 0)
            {
                double accel = AccelDegPerS2 * Deg2Rad;
                double cap = Math.Min(MaxRateDegPerS * Deg2Rad, Math.Sqrt(2.0 * accel * left));
                _speed = Math.Min(_speed + accel * step, cap);
                double after = left - _speed * step;
                if (after <= 0)
                {
                    _offset = default;
                    _speed = 0;
                }
                else _offset = _offset * (after / left);
            }

            var view = Turn(aim, _offset);
            _lastAim = aim;
            _lastView = view;
            return view;
        }

        private void Learn(DVec3 moved, double dut)
        {
            _rate = moved * (1.0 / dut);
            _rateKnown = true;
        }

        /* The rotation vector taking unit `from` to unit `to`. */
        internal static DVec3 RotationVector(DVec3 from, DVec3 to)
        {
            var axis = DVec3.Cross(from, to);
            double s = axis.Magnitude, c = DVec3.Dot(from, to);
            double angle = Math.Atan2(s, c);
            if (angle == 0) return default;
            if (s < 1e-12)
            {
                /* Opposite: any axis square to `from` will do. */
                axis = DVec3.Cross(from, Math.Abs(from.X) < 0.9 ? new DVec3(1, 0, 0) : new DVec3(0, 1, 0));
                s = axis.Magnitude;
            }
            return axis * (angle / s);
        }

        private static DVec3 Turn(DVec3 v, DVec3 rotation)
        {
            double angle = rotation.Magnitude;
            return angle > 0 ? CommandCameraMath.RotateAbout(v, rotation * (1.0 / angle), angle) : v;
        }
    }
}
