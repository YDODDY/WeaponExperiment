using UnityEngine;

namespace WeaponExperiment
{
    /// <summary>
    /// A weapon whose motion can be overridden by a contact response. Implemented by both
    /// <see cref="MouseWeapon"/> and <see cref="EntityWeapon"/>; each owns a
    /// <see cref="WeaponContactSolver"/> that normally detects the shield contact itself
    /// (inside the weapon's LateUpdate, before the pose commits) and drives the response.
    /// This interface is the EXTERNAL seam - a way for something else to inject a response
    /// - plus a read-only "am I resolving a contact right now" flag the sensor watches.
    /// </summary>
    public interface IWeaponRecoil
    {
        /// <summary>
        /// Inject a contact response: hand the solver a world-space response velocity and a
        /// window (seconds) during which the response owns the weapon motion, overriding
        /// input / AI intent. A brief interruption, never a long stun.
        /// </summary>
        void ApplyRecoil(Vector3 worldRecoilVelocity, float interruptSeconds);

        /// <summary>True while a contact is being resolved / a response window is running.</summary>
        bool IsRecoiling { get; }
    }
}
