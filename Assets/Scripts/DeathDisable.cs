using UnityEngine;

namespace WeaponExperiment
{
    /// <summary>
    /// Death 0.1 - generic. When this GameObject's <see cref="DamageTarget"/> fires Died
    /// (HP first reaches 0), disable a list of Behaviours. Used identically on the Player
    /// (PlayerMovement / MouseWeapon / PlayerOverswing / WeaponHitSensor) and on a
    /// <see cref="CombatEntity"/> (CombatEntity / EntityWeapon / WeaponHitSensor). No
    /// ragdoll, no death animation.
    /// </summary>
    public class DeathDisable : MonoBehaviour
    {
        [SerializeField] private DamageTarget target;
        [Tooltip("Behaviours switched off the instant HP hits 0.")]
        [SerializeField] private Behaviour[] disableOnDeath;
        [Tooltip("Optional: tell a MouseWeapon to safe-stow before it is disabled (stops the club dangling).")]
        [SerializeField] private MouseWeapon suspendWeaponOnDeath;

        private void Awake()
        {
            if (target == null) target = GetComponent<DamageTarget>();
            if (target != null) target.Died += HandleDeath;
        }

        private void OnDestroy()
        {
            if (target != null) target.Died -= HandleDeath;
        }

        private void HandleDeath()
        {
            if (suspendWeaponOnDeath != null)
                suspendWeaponOnDeath.SetSuspended(true);

            if (disableOnDeath == null) return;
            foreach (var b in disableOnDeath)
                if (b != null) b.enabled = false;
        }
    }
}
