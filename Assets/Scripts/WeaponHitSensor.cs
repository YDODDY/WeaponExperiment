using System.Collections.Generic;
using UnityEngine;

namespace WeaponExperiment
{
    /// <summary>
    /// Damage 0.1 - turns ACTUAL weapon motion + real target contact into a damage event.
    ///
    /// No Rigidbody, no collider on the weapon (it is driven purely by transform). Every
    /// LateUpdate, AFTER MouseWeapon has moved the weapon, this sweeps a capsule from the
    /// weapon tip's previous world position to its current one and asks Physics for
    /// overlapping <see cref="DamageTarget"/>s.
    ///
    /// Damage uses ONLY MouseWeapon.WeaponSpeed / WeaponSwingSpan - the lagged, inertial
    /// weapon motion measured relative to the player (Run / walking excluded). Mouse speed,
    /// cursor delta, mouse AngularSpan are never read here.
    ///
    ///   damage = clamp(minimumContactDamage + motionLoad * motionDamageScale,
    ///                  minimumContactDamage, maxDamage)
    ///   motionLoad = (WeaponSpeed / damageSpeedRef) * (WeaponSwingSpan / damageAngleRef)
    ///
    /// One hit per contact episode: a target must separate and touch again to be hit again
    /// (never per-frame). No damage while the weapon is Stowed or Stumble/Fall-suspended.
    /// </summary>
    [DefaultExecutionOrder(20)]
    public class WeaponHitSensor : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private MouseWeapon weapon;

        [Header("Contact")]
        [Tooltip("Radius of the capsule swept from the tip's previous to current position.")]
        [SerializeField] private float hitRadius = 0.16f;
        [SerializeField] private LayerMask hitMask = ~0;

        [Header("Damage")]
        [Tooltip("Always dealt on a NEW contact, even with a motionless weapon.")]
        [SerializeField] private float minimumContactDamage = 1f;
        [Tooltip("Weapon tip speed (m/s) that counts as a full 'fast' hit (speed factor = 1). Tune vs the HUD.")]
        [SerializeField] private float damageSpeedRef = 8f;
        [Tooltip("Weapon swing span (deg) that counts as a full 'wide' hit (angle factor = 1). Tune vs the HUD.")]
        [SerializeField] private float damageAngleRef = 120f;
        [Tooltip("Multiplier on motionLoad (= speedFactor * angleFactor).")]
        [SerializeField] private float motionDamageScale = 12f;
        [SerializeField] private float maxDamage = 40f;

        [Header("Debug")]
        [SerializeField] private bool showDebug = true;

        private readonly HashSet<DamageTarget> _contacts = new HashSet<DamageTarget>();
        private readonly HashSet<DamageTarget> _frameSet = new HashSet<DamageTarget>();
        private readonly Collider[] _overlap = new Collider[16];
        private Vector3 _prevTip;
        private bool _tipPrimed;

        // debug (last hit)
        private float _lastDmg, _lastSpd, _lastSpan, _lastLoad;

        private void Awake()
        {
            if (weapon == null) weapon = GetComponent<MouseWeapon>();
        }

        private void LateUpdate()
        {
            if (weapon == null) return;

            Vector3 tip = weapon.WeaponTipWorld;
            if (!_tipPrimed) { _prevTip = tip; _tipPrimed = true; }

            if (!weapon.WeaponCanHit)
            {
                // Stowed / suspended: no damage, and keep _prevTip synced so re-arming does
                // not sweep a stale segment across half the arena.
                _contacts.Clear();
                _prevTip = tip;
                return;
            }

            _frameSet.Clear();
            int n = Physics.OverlapCapsuleNonAlloc(
                _prevTip, tip, hitRadius, _overlap, hitMask, QueryTriggerInteraction.Collide);

            for (int i = 0; i < n; i++)
            {
                if (!_overlap[i].TryGetComponent(out DamageTarget target) || target.IsDead)
                    continue;

                _frameSet.Add(target);
                if (_contacts.Contains(target))
                    continue; // same ongoing episode - already hit once

                float dmg = ComputeDamage(out float spd, out float span, out float load);
                target.TakeDamage(new DamageInfo
                {
                    amount = dmg,
                    hitPoint = _overlap[i].ClosestPoint(tip),
                    weaponSpeed = spd,
                    weaponSwingSpan = span,
                    source = gameObject,
                });
                _lastDmg = dmg; _lastSpd = spd; _lastSpan = span; _lastLoad = load;
            }

            _contacts.Clear();
            foreach (var t in _frameSet)
                _contacts.Add(t);
            _prevTip = tip;
        }

        private float ComputeDamage(out float spd, out float span, out float load)
        {
            spd = weapon.WeaponSpeed;
            span = weapon.WeaponSwingSpan;
            load = MotionLoad(spd, span);
            float motionDamage = load * motionDamageScale;
            return Mathf.Clamp(minimumContactDamage + motionDamage, minimumContactDamage, maxDamage);
        }

        private float MotionLoad(float spd, float span)
        {
            float speedFactor = spd / Mathf.Max(damageSpeedRef, 1e-3f);
            float angleFactor = span / Mathf.Max(damageAngleRef, 1e-3f);
            return speedFactor * angleFactor; // BOTH must be sizeable for a big number
        }

        private void OnGUI()
        {
            if (!showDebug) return;
            float liveLoad = weapon != null ? MotionLoad(weapon.WeaponSpeed, weapon.WeaponSwingSpan) : 0f;
            GUI.color = Color.white;
            GUI.Label(new Rect(12f, 300f, 580f, 74f),
                $"motionLoad(live) {liveLoad:F2}   " +
                $"(spd {(weapon != null ? weapon.WeaponSpeed : 0f):F1}/{damageSpeedRef:F0}  x  " +
                $"span {(weapon != null ? weapon.WeaponSwingSpan : 0f):F0}/{damageAngleRef:F0})\n" +
                $"lastHit  -{_lastDmg:F1}   (spd {_lastSpd:F1}, span {_lastSpan:F0}, load {_lastLoad:F2}, " +
                $"min {minimumContactDamage:F0}, max {maxDamage:F0})\n" +
                $"contacts {_contacts.Count}");
        }
    }
}
