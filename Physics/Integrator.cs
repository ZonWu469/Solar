using System;
using Solar.Core;
using Solar.Vessels;

namespace Solar.Physics
{
    /// <summary>RK4 integration of a vessel under gravity, thrust and drag (off-rails flight).</summary>
    public static class Integrator
    {
        public static void Step(Vessel v, double dt)
        {
            double mu = v.Body.Mu;
            double mass = v.TotalMass;
            double thrust = v.CurrentThrust;
            Vec2d thrustAcc = v.Up * (thrust / mass) + v.RcsAccel;  // main engine + RCS translation
            var atmo = v.Body.Atmo;
            double bodyRadius = v.Body.Radius;
            double cda = v.TotalCdA;

            Vec2d Accel(Vec2d r, Vec2d vel)
            {
                double rl = r.Length;
                Vec2d a = r * (-mu / (rl * rl * rl));
                a += thrustAcc;
                if (atmo != null)
                {
                    double rho = atmo.DensityAt(rl - bodyRadius);
                    if (rho > 0)
                    {
                        double speed = vel.Length;
                        a += vel * (-0.5 * rho * speed * cda / mass);
                    }
                }
                return a;
            }

            Vec2d r0 = v.Position, v0 = v.Velocity;
            Vec2d k1r = v0, k1v = Accel(r0, v0);
            Vec2d k2r = v0 + k1v * (dt / 2), k2v = Accel(r0 + k1r * (dt / 2), v0 + k1v * (dt / 2));
            Vec2d k3r = v0 + k2v * (dt / 2), k3v = Accel(r0 + k2r * (dt / 2), v0 + k2v * (dt / 2));
            Vec2d k4r = v0 + k3v * dt, k4v = Accel(r0 + k3r * dt, v0 + k3v * dt);

            v.Position = r0 + (k1r + 2 * k2r + 2 * k3r + k4r) * (dt / 6);
            v.Velocity = v0 + (k1v + 2 * k2v + 2 * k3v + k4v) * (dt / 6);

            v.DrainFuel(dt);
            v.DrainMonoprop(dt);
        }
    }
}
