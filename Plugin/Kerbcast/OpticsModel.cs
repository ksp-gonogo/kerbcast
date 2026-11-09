using System;

namespace Kerbcast
{
    /* A command feed's optics knobs. NaN in a field keeps the default for it,
       so a host's override and the settings combine field by field. */
    internal struct OpticsKnobs
    {
        /* The lens's own blur (FWHM) in arcseconds; 0 is a perfect lens. */
        public double ResolutionArcsec;
        /* Seeing (FWHM) at the zenith for a lens at sea level on Kerbin; 0 turns seeing off. */
        public double SeeingArcsec;
        /* How fast seeing grows with airmass: seeing x airmass^power. */
        public double SeeingAirmassPower;
        /* 0 keeps a blurred object's light strictly, 1 keeps its surface brightness. */
        public double BrightnessHold;
        /* How strongly a daylit sky washes over everything, 0 for none. */
        public double DaySkyStrength;

        public static OpticsKnobs Unset => new OpticsKnobs
        {
            ResolutionArcsec = double.NaN,
            SeeingArcsec = double.NaN,
            SeeingAirmassPower = double.NaN,
            BrightnessHold = double.NaN,
            DaySkyStrength = double.NaN,
        };

        public bool IsUnset =>
            double.IsNaN(ResolutionArcsec) && double.IsNaN(SeeingArcsec) && double.IsNaN(SeeingAirmassPower)
            && double.IsNaN(BrightnessHold) && double.IsNaN(DaySkyStrength);

        /* Each field set here wins over `defaults`. */
        public OpticsKnobs Over(OpticsKnobs defaults) => new OpticsKnobs
        {
            ResolutionArcsec = double.IsNaN(ResolutionArcsec) ? defaults.ResolutionArcsec : ResolutionArcsec,
            SeeingArcsec = double.IsNaN(SeeingArcsec) ? defaults.SeeingArcsec : SeeingArcsec,
            SeeingAirmassPower = double.IsNaN(SeeingAirmassPower) ? defaults.SeeingAirmassPower : SeeingAirmassPower,
            BrightnessHold = double.IsNaN(BrightnessHold) ? defaults.BrightnessHold : BrightnessHold,
            DaySkyStrength = double.IsNaN(DaySkyStrength) ? defaults.DaySkyStrength : DaySkyStrength,
        };

        /* The same values field by field, NaN matching NaN. */
        public bool SameAs(OpticsKnobs o) =>
            Same(ResolutionArcsec, o.ResolutionArcsec) && Same(SeeingArcsec, o.SeeingArcsec) && Same(SeeingAirmassPower, o.SeeingAirmassPower)
            && Same(BrightnessHold, o.BrightnessHold) && Same(DaySkyStrength, o.DaySkyStrength);

        private static bool Same(double a, double b) => a == b || (double.IsNaN(a) && double.IsNaN(b));

        /* Every field NaN or a finite number in its range. */
        public bool Valid =>
            NaNOrAtLeast(ResolutionArcsec, 0) && NaNOrAtLeast(SeeingArcsec, 0) && NaNOrAtLeast(SeeingAirmassPower, 0)
            && (double.IsNaN(BrightnessHold) || (BrightnessHold >= 0 && BrightnessHold <= 1))
            && NaNOrAtLeast(DaySkyStrength, 0);

        private static bool NaNOrAtLeast(double v, double min) =>
            double.IsNaN(v) || (!double.IsInfinity(v) && v >= min);

        public override string ToString() =>
            $"res={Show(ResolutionArcsec)} seeing={Show(SeeingArcsec)} power={Show(SeeingAirmassPower)} "
            + $"hold={Show(BrightnessHold)} daySky={Show(DaySkyStrength)}";

        private static string Show(double v) =>
            double.IsNaN(v) ? "default" : v.ToString("G4", System.Globalization.CultureInfo.InvariantCulture);
    }

    /* A feed's lens as the game sets it, before any host override: the knobs and what chose them, for the log. */
    internal struct OpticsLens
    {
        public OpticsKnobs Defaults;
        public string Label;

        public OpticsLens(OpticsKnobs defaults, string label)
        {
            Defaults = defaults;
            Label = label;
        }
    }

    /* Where a lens stands and what it looks at, for OpticsModel.Assess. */
    internal struct OpticsSite
    {
        public double ResolutionArcsec;
        public double ZenithSeeingArcsec;
        public double SeeingAirmassPower;
        /* The air around the lens: 0 for none (space, or an airless body), 1 for Kerbin at sea level. */
        public double AirFactor;
        /* The target's elevation above the lens's horizon. */
        public double ElevationDeg;
        /* The Sun's elevation above the lens's horizon. */
        public double SunElevationDeg;
    }

    /* What a lens makes of a target. Every number depends only on the lens,
       the range and the air at the lens, so it reads the same whichever body
       the lens stands on and whichever way it looks. */
    internal struct OpticsReading
    {
        public double ResolutionArcsec;
        /* 1 with no air at the lens. */
        public double Airmass;
        /* Seeing at this airmass; 0 with no air. */
        public double SeeingArcsec;
        /* Lens and seeing combined (FWHM). */
        public double BlurArcsec;
        /* How many metres on the target one blur covers; NaN without a range. */
        public double BlurOnTargetM;
        /* Light lost to the air on the way down, in stops. */
        public double ExtinctionStops;
        /* 0 at night or with no air, 1 in full day under a full atmosphere. */
        public double DaySky;
        /* Picture quality in stops (one stop is a factor of two in sharpness
           or light), for ranking vantages: higher is clearer. */
        public double ClarityStops;
    }

    /*
     * Deep-space optics for command feeds: one blur per lens, the same angle
     * for everything it sees. A body much wider than the blur only softens; one
     * smaller than it becomes the blur's own shape. Unity-free so the tests
     * and any vantage scoring can use it.
     */
    internal static class OpticsModel
    {
        public const double ArcsecPerRadian = 206264.80624709636;
        public const double FwhmPerSigma = 2.3548200450309493;
        /* Below this many pixels of blur the picture is left alone. */
        public const double MinBlurPx = 1.0;
        /* Kerbin's sea-level pressure, which AirFactor measures against. */
        public const double ReferencePressureKPa = 101.325;
        /* What full daylight costs a vantage, in stops. */
        public const double DaySkyStops = 1.5;
        /* Extinction per airmass in magnitudes at sea level, red, green and blue: blue is lost first, so low objects redden. */
        public static readonly double[] ExtinctionPerAirmass = { 0.10, 0.18, 0.30 };

        /* Planets: hold the surface brightness and add a little punch, capped so a speck never becomes a beacon. */
        public const double BodyBoost = 2.2;
        public const double BodyGainCap = 10.0;
        /* Craft are held back harder than planets. */
        public const double CraftHoldShare = 0.55;
        public const double CraftGainCap = 6.0;

        /* The deepest level of the blur pyramid. */
        public const int MaxPyramidLevel = 8;

        public static OpticsKnobs Defaults => new OpticsKnobs
        {
            ResolutionArcsec = 1.0,
            SeeingArcsec = 1.0,
            SeeingAirmassPower = 0.6,
            BrightnessHold = 1.0,
            DaySkyStrength = 0.3,
        };

        /* Air around a lens at `pressureKPa`: the square root of the pressure
           against Kerbin's sea level, capped at 1, so thin air sees better and
           no air sees perfectly. */
        public static double AirFactor(double pressureKPa)
        {
            if (!(pressureKPa > 0) || double.IsInfinity(pressureKPa)) return 0.0;
            return Math.Sqrt(Math.Min(1.0, pressureKPa / ReferencePressureKPa));
        }

        /* Kasten and Young (1989): 1 at the zenith, about 38 at the horizon,
           finite below it. */
        public static double Airmass(double elevationDeg)
        {
            double e = double.IsNaN(elevationDeg) ? 90.0 : Math.Max(0.0, Math.Min(90.0, elevationDeg));
            return 1.0 / (Math.Sin(e * Math.PI / 180.0) + 0.50572 * Math.Pow(e + 6.07995, -1.6364));
        }

        public static double Seeing(double zenithArcsec, double airmass, double power, double airFactor)
        {
            if (!(zenithArcsec > 0) || !(airFactor > 0)) return 0.0;
            return zenithArcsec * airFactor * Math.Pow(Math.Max(1.0, airmass), power);
        }

        public static double CombinedBlur(double resolutionArcsec, double seeingArcsec) =>
            Math.Sqrt(Sq(Math.Max(0.0, resolutionArcsec)) + Sq(Math.Max(0.0, seeingArcsec)));

        public static double BlurOnTarget(double rangeM, double blurArcsec) =>
            rangeM > 0 && !double.IsInfinity(rangeM) ? rangeM * blurArcsec / ArcsecPerRadian : double.NaN;

        /* Light lost below the zenith's, in stops, for one channel (0 red, 1 green, 2 blue). */
        public static double ExtinctionStops(double airmass, double airFactor, int channel = 1)
        {
            if (!(airFactor > 0)) return 0.0;
            double k = ExtinctionPerAirmass[Math.Max(0, Math.Min(2, channel))] * airFactor * airFactor;
            return 0.4 * k * Math.Max(0.0, airmass - 1.0) * Math.Log(10.0, 2.0);
        }

        /* The share of light left after extinction, for one channel. */
        public static double Transmission(double airmass, double airFactor, int channel) =>
            Math.Pow(2.0, -ExtinctionStops(airmass, airFactor, channel));

        /* 0 with the Sun 6 degrees below the horizon or lower, 1 at 6 above, smooth between; scaled by the air. */
        public static double DaySky(double sunElevationDeg, double airFactor)
        {
            if (!(airFactor > 0) || double.IsNaN(sunElevationDeg)) return 0.0;
            double t = Math.Max(0.0, Math.Min(1.0, (sunElevationDeg + 6.0) / 12.0));
            return t * t * (3.0 - 2.0 * t) * Math.Min(1.0, airFactor);
        }

        public static OpticsReading Assess(OpticsSite site, double rangeM)
        {
            double airmass = site.AirFactor > 0 ? Airmass(site.ElevationDeg) : 1.0;
            double seeing = Seeing(site.ZenithSeeingArcsec, airmass, site.SeeingAirmassPower, site.AirFactor);
            double blur = CombinedBlur(site.ResolutionArcsec, seeing);
            double onTarget = BlurOnTarget(rangeM, blur);
            double extinction = ExtinctionStops(airmass, site.AirFactor);
            double day = DaySky(site.SunElevationDeg, site.AirFactor);
            return new OpticsReading
            {
                ResolutionArcsec = Math.Max(0.0, site.ResolutionArcsec),
                Airmass = airmass,
                SeeingArcsec = seeing,
                BlurArcsec = blur,
                BlurOnTargetM = onTarget,
                ExtinctionStops = extinction,
                DaySky = day,
                ClarityStops = -Math.Log(Math.Max(double.IsNaN(onTarget) ? 1.0 : onTarget, 1e-9), 2.0) - extinction - DaySkyStops * day,
            };
        }

        /* The lens at normalised Tracking Station level `normLevel` (0 the lowest, 1 the top), interpolated in log space through the three levels' values. */
        public static double ResolutionForLevel(double normLevel, double level1, double level2, double level3)
        {
            if (double.IsNaN(normLevel)) normLevel = 0;
            double t = Math.Max(0.0, Math.Min(1.0, normLevel));
            level3 = Math.Max(level3, 0.0);
            level2 = Math.Max(level2, level3);
            level1 = Math.Max(level1, level2);
            double from = t <= 0.5 ? level1 : level2;
            double to = t <= 0.5 ? level2 : level3;
            double f = t <= 0.5 ? t * 2.0 : (t - 0.5) * 2.0;
            if (from <= 0 || to <= 0) return from + (to - from) * f;
            return from * Math.Pow(to / from, f);
        }

        /* Arcseconds one pixel covers at the frame's centre. */
        public static double PixelArcsec(double verticalFovDeg, int rows) =>
            rows > 0 && verticalFovDeg > 0
                ? 2.0 * Math.Tan(verticalFovDeg * 0.5 * Math.PI / 180.0) / rows * ArcsecPerRadian
                : double.NaN;

        /* The angular radius, radians, of the body a lens stands on: a lens at or under its datum (ground below sea level) has it filling half the sky. */
        public static double HomeAngularRadius(double fromCentreM, double radiusM) =>
            fromCentreM > radiusM ? Math.Asin(radiusM / fromCentreM) : Math.PI * 0.5;

        /*
         * One axis of the quad that adds a layer shifted by `shift` pixels over
         * a target `size` pixels across. The quad runs past both edges with
         * texture coordinates outside 0 to 1, so the shift never bares a strip
         * of the frame; a clamped target repeats its edge out there.
         */
        public static void OverscanAxis(float shift, float size, out float x0, out float x1, out float u0, out float u1)
        {
            float pad = shift != 0f ? Math.Abs(shift) + 1f : 0f;
            x0 = shift - pad;
            x1 = size + shift + pad;
            u0 = -pad / size;
            u1 = 1f + pad / size;
        }

        /* Whether a blur of `blurPx` changes the picture at all. */
        public static bool Worthwhile(double blurPx) => blurPx >= MinBlurPx && !double.IsInfinity(blurPx);

        /* Pixels of blur over which the effect fades in past MinBlurPx. */
        public const double RampPx = 1.5;

        /* How far the effect has faded in at `blurPx`: 0 at MinBlurPx, 1 by RampPx more, smooth between, so zooming across the threshold never jumps. */
        public static double Ramp(double blurPx)
        {
            if (!(blurPx > MinBlurPx)) return 0.0;
            double t = Math.Min(1.0, (blurPx - MinBlurPx) / RampPx);
            return t * t * (3.0 - 2.0 * t);
        }

        /* The deepest pyramid level a target of `width` x `height` can use. */
        public static int MaxLevelFor(int width, int height)
        {
            int level = 1;
            while (level < MaxPyramidLevel && (width >> (level + 1)) >= 2 && (height >> (level + 1)) >= 2) level++;
            return level;
        }

        /* A uniform disc of radius `radius` under a Gaussian of `sigma` (same units): the light left at its centre. */
        public static double DiscPeak(double radius, double sigma)
        {
            if (!(sigma > 0)) return radius > 0 ? 1.0 : 0.0;
            return 1.0 - Math.Exp(-Sq(radius) / (2.0 * Sq(sigma)));
        }

        /* The blurred disc's coverage at `r` from its centre. */
        public static double DiscCoverage(double r, double radius, double sigma)
        {
            r = Math.Abs(r);
            if (!(radius > 0)) return 0.0;
            if (!(sigma > 0)) return r < radius ? 1.0 : r == radius ? 0.5 : 0.0;
            double lo = Math.Max(0.0, r - 7.0 * sigma), hi = Math.Min(radius, r + 7.0 * sigma);
            if (hi <= lo) return 0.0;
            /* Radial convolution of the disc with a 2D Gaussian; I0e keeps the Bessel factor finite far out. */
            const int steps = 64;
            double h = (hi - lo) / steps, s2 = sigma * sigma, sum = 0.0;
            for (int i = 0; i <= steps; i++)
            {
                double rho = lo + i * h;
                double f = rho / s2 * Math.Exp(-Sq(r - rho) / (2.0 * s2)) * BesselI0e(r * rho / s2);
                sum += f * (i == 0 || i == steps ? 1.0 : (i % 2 == 1 ? 4.0 : 2.0));
            }
            return Math.Max(0.0, Math.Min(1.0, sum * h / 3.0));
        }

        /* Light handed back to an object the blur spreads out: `peak` is its blurred peak against its own surface brightness. */
        public static double Gain(double peak, double hold, double boost, double cap)
        {
            peak = Math.Max(peak, 1e-6);
            double g = Math.Pow(peak, -Math.Max(0.0, hold)) * (1.0 + boost * Sq(1.0 - Math.Min(peak, 1.0)));
            return Math.Max(1.0, Math.Min(cap, g));
        }

        /* A gain that fades out in daylight, where the sky would be boosted with the object. */
        public static double DayGain(double gain, double daySky) =>
            1.0 + (gain - 1.0) * (1.0 - Math.Max(0.0, Math.Min(1.0, daySky)));

        /* Blur width (sigma, pixels) of a pyramid that halves `level` times
           with 2x2 box averages and doubles back with bilinear filtering. */
        public static double PyramidSigma(int level) =>
            level <= 0 ? 0.0 : Math.Sqrt((Math.Pow(4.0, level) - 1.0) / 4.0);

        /* The pyramid level and the share of the next one that together blur
           by `sigmaPx`, matched by variance. */
        public static void PyramidPlan(double sigmaPx, int maxLevel, out int level, out double nextShare)
        {
            maxLevel = Math.Max(1, maxLevel);
            level = 0;
            nextShare = 0.0;
            if (!(sigmaPx > 0)) return;
            double v = sigmaPx * sigmaPx;
            for (int k = 0; k < maxLevel; k++)
            {
                double a = Sq(PyramidSigma(k)), b = Sq(PyramidSigma(k + 1));
                if (v < b || k == maxLevel - 1)
                {
                    level = k;
                    nextShare = Math.Max(0.0, Math.Min(1.0, (v - a) / (b - a)));
                    return;
                }
            }
        }

        /* Level 1, 2 and 3 lenses and their motion, by eye. */
        private static readonly double[] MotionLens = { 15.0, 4.0, 1.0 };
        private static readonly double[] MotionWobble = { 1.57, 1.26, 1.0 };
        private static readonly double[] MotionBoil = { 0.7, 1.0, 1.3 };
        private static readonly double[] MotionTwinkle = { 0.25, 0.12, 0.05 };

        /* Seeing's motion for a lens of `resolutionArcsec`: small lenses dance
           as a whole (wobble) and twinkle, big ones ripple (boil). */
        public static void SeeingMotion(double resolutionArcsec, out double wobble, out double boil, out double twinkle)
        {
            double[] res = MotionLens, w = MotionWobble, b = MotionBoil, s = MotionTwinkle;
            double x = Math.Log(Math.Max(resolutionArcsec, 1e-3));
            int i = x >= Math.Log(res[1]) ? 0 : 1;
            double t = (x - Math.Log(res[i])) / (Math.Log(res[i + 1]) - Math.Log(res[i]));
            t = Math.Max(0.0, Math.Min(1.0, t));
            wobble = w[i] + (w[i + 1] - w[i]) * t;
            boil = b[i] + (b[i + 1] - b[i]) * t;
            twinkle = s[i] + (s[i + 1] - s[i]) * t;
        }

        /* Airy pattern for a diffraction blur of FWHM 1: 1 at the centre, with rings. */
        public static double Airy(double r)
        {
            /* (2 J1(u) / u)^2 falls to one half at u = 1.6163. */
            double u = Math.Abs(r) * 2.0 * 1.6163;
            if (u < 1e-6) return 1.0;
            double j = 2.0 * BesselJ1(u) / u;
            return j * j;
        }

        /*
         * A craft's blur, an Airy core (FWHM `diffFwhm`) under Gaussian seeing
         * (FWHM `seeingFwhm`), sampled at `samples` radii from 0 to `maxRadius`.
         * Units are the caller's; the result peaks at 1. The rings show with
         * no seeing and wash out under it.
         */
        public static double[] PsfProfile(double diffFwhm, double seeingFwhm, double maxRadius, int samples)
        {
            samples = Math.Max(2, samples);
            var p = new double[samples];
            double sigma = Math.Max(0.0, seeingFwhm) / FwhmPerSigma;
            bool hasDiff = diffFwhm > 1e-9;
            for (int i = 0; i < samples; i++)
            {
                double r = maxRadius * i / (samples - 1);
                if (!hasDiff) p[i] = sigma > 0 ? Math.Exp(-r * r / (2 * sigma * sigma)) : (i == 0 ? 1 : 0);
                else if (sigma < diffFwhm * 0.02) p[i] = Airy(r / diffFwhm);
                else p[i] = AiryUnderGauss(r, diffFwhm, sigma);
            }
            double peak = 0;
            foreach (var v in p) peak = Math.Max(peak, v);
            if (peak > 0) for (int i = 0; i < samples; i++) p[i] /= peak;
            return p;
        }

        private static double AiryUnderGauss(double r, double diffFwhm, double sigma)
        {
            double s2 = sigma * sigma;
            double lo = Math.Max(0.0, r - 7.0 * sigma), hi = r + 7.0 * sigma;
            const int steps = 128;
            double h = (hi - lo) / steps, sum = 0.0;
            for (int i = 0; i <= steps; i++)
            {
                double rho = lo + i * h;
                double f = Airy(rho / diffFwhm) * rho / s2 * Math.Exp(-Sq(r - rho) / (2.0 * s2)) * BesselI0e(r * rho / s2);
                sum += f * (i == 0 || i == steps ? 1.0 : (i % 2 == 1 ? 4.0 : 2.0));
            }
            return sum * h / 3.0;
        }

        /* Bessel J1, Numerical Recipes' rational and asymptotic fits. */
        public static double BesselJ1(double x)
        {
            double ax = Math.Abs(x);
            if (ax < 8.0)
            {
                double y = x * x;
                double a = x * (72362614232.0 + y * (-7895059235.0 + y * (242396853.1 + y * (-2972611.439 + y * (15704.48260 + y * -30.16036606)))));
                double b = 144725228442.0 + y * (2300535178.0 + y * (18583304.74 + y * (99447.43394 + y * (376.9991397 + y))));
                return a / b;
            }
            double z = 8.0 / ax, yy = z * z, xx = ax - 2.356194491;
            double p = 1.0 + yy * (0.183105e-2 + yy * (-0.3516396496e-4 + yy * (0.2457520174e-5 + yy * -0.240337019e-6)));
            double q = 0.04687499995 + yy * (-0.2002690873e-3 + yy * (0.8449199096e-5 + yy * (-0.88228987e-6 + yy * 0.105787412e-6)));
            double ans = Math.Sqrt(0.636619772 / ax) * (Math.Cos(xx) * p - z * Math.Sin(xx) * q);
            return x < 0.0 ? -ans : ans;
        }

        /* exp(-|x|) I0(x), Abramowitz and Stegun 9.8.1 and 9.8.2. */
        public static double BesselI0e(double x)
        {
            double ax = Math.Abs(x);
            if (ax < 3.75)
            {
                double y = Sq(x / 3.75);
                double i0 = 1.0 + y * (3.5156229 + y * (3.0899424 + y * (1.2067492 + y * (0.2659732 + y * (0.0360768 + y * 0.0045813)))));
                return i0 * Math.Exp(-ax);
            }
            double t = 3.75 / ax;
            return (0.39894228 + t * (0.01328592 + t * (0.00225319 + t * (-0.00157565 + t * (0.00916281
                + t * (-0.02057706 + t * (0.02635537 + t * (-0.01647633 + t * 0.00392377)))))))) / Math.Sqrt(ax);
        }

        private static double Sq(double v) => v * v;
    }

    /* Smooth, looping noise for seeing's motion: whole-cycle sines at a few
       frequencies with fixed random phases, unit RMS per channel. */
    internal sealed class SeeingNoise
    {
        private static readonly double[] Frequencies = { 0.5, 1.0, 1.5, 2.5 };
        private readonly double[,] _phase;

        public SeeingNoise(int seed)
        {
            var rng = new Random(seed);
            _phase = new double[Frequencies.Length, 3];
            for (int i = 0; i < Frequencies.Length; i++)
                for (int c = 0; c < 3; c++)
                    _phase[i, c] = rng.NextDouble() * 2.0 * Math.PI;
        }

        /* Channel `c` (0 to 2) at time `t` seconds. */
        public double At(double t, int c)
        {
            double sum = 0.0;
            for (int i = 0; i < Frequencies.Length; i++)
                sum += Math.Sin(2.0 * Math.PI * Frequencies[i] * t + _phase[i, c]);
            return sum / Math.Sqrt(Frequencies.Length / 2.0);
        }
    }
}
