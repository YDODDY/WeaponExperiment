using UnityEngine;

namespace WeaponExperiment
{
    /// <summary>
    /// The read-only surface <see cref="WeaponHitSensor"/> needs from a weapon. Both the
    /// mouse-driven player weapon (<see cref="MouseWeapon"/>) and the AI-driven
    /// <see cref="EntityWeapon"/> implement it, so the hit/damage pipeline is shared while
    /// the input / decision layer differs. No fake mouse input, no weapon refactor.
    /// </summary>
    public interface IWeapon
    {
        /// <summary>Actual weapon tip speed (m/s), relative to the owner - carrier translation excluded.</summary>
        float WeaponSpeed { get; }

        /// <summary>Actual weapon tip velocity vector (world-ish, m/s, owner-relative). Same source as
        /// WeaponSpeed; used to derive a physical recoil direction on a blocked hit.</summary>
        Vector3 WeaponTipVelocity { get; }

        /// <summary>Widest arc the ACTUAL weapon facing swept in a recent window (deg).</summary>
        float WeaponSwingSpan { get; }

        /// <summary>Weapon may deal damage right now (drawn / alive / in a live swing, etc.).</summary>
        bool WeaponCanHit { get; }

        /// <summary>World position of the weapon tip (business end).</summary>
        Vector3 WeaponTipWorld { get; }
    }
}
