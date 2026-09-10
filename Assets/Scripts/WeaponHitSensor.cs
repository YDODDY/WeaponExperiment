using System.Collections.Generic;
using UnityEngine;

namespace WeaponExperiment
{
    /// <summary>
    /// Damage + Block (0.1). Turns ACTUAL weapon motion + real body contact into a damage
    /// event. Shared by the player and CombatEntity via <see cref="IWeapon"/>.
    ///
    /// Blocking is NOT decided here any more. The weapon's <see cref="WeaponContactSolver"/>
    /// (run inside the weapon's own LateUpdate, before its pose commits) clamps the tip so
    /// it cannot penetrate a shield - so the tip physically never reaches the body behind
    /// the shield and no damage is even attempted. This sensor keeps only a light safety
    /// net: while the weapon reports it is resolving a contact (IsRecoiling), the whole
    /// strike is consumed - no damage until the swing ends.
    ///
    ///   damage = clamp(minimumContactDamage + motionLoad * motionDamageScale, min, max)
    ///   motionLoad = (WeaponSpeed / damageSpeedRef) * (WeaponSwingSpan / damageAngleRef)
    /// One hit per contact episode (never per-frame).
    /// </summary>
    [DefaultExecutionOrder(20)]
    public class WeaponHitSensor : MonoBehaviour
    {
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
        private readonly Collider[] _overlap = new Collider[24];
        private Vector3 _prevTip;
        private bool _tipPrimed;
        private bool _strikeConsumed; // the contact solver blocked this strike -> no damage until the swing ends

        private IWeapon weapon;              // MouseWeapon or EntityWeapon, on this GameObject
        private IWeaponRecoil recoilWeapon;  // same object, if it resolves contacts
        private DamageTarget _selfTarget;    // this wielder's own body - never damage it

        // debug (last hit)
        private float _lastDmg, _lastSpd, _lastSpan, _lastLoad;
        private bool _loggedBlock;

        private void Awake()
        {
            weapon = GetComponent<IWeapon>();
            recoilWeapon = GetComponent<IWeaponRecoil>();
            _selfTarget = GetComponentInParent<DamageTarget>();
        }

        private void LateUpdate()
        {
            if (weapon == null) return;

            Vector3 tip = weapon.WeaponTipWorld;
            if (!_tipPrimed) { _prevTip = tip; _tipPrimed = true; }

            if (!weapon.WeaponCanHit)
            {
                _contacts.Clear();
                _strikeConsumed = false;
                _loggedBlock = false;
                _prevTip = tip;
                return;
            }

            // The solver resolved a shield contact -> the tip was clamped, the strike is spent.
            if (recoilWeapon != null && recoilWeapon.IsRecoiling)
            {
                if (!_loggedBlock)
                {
                    Debug.Log($"[{name}] strike BLOCKED - tip resolved against a shield (no penetration, no damage)", this);
                    _loggedBlock = true;
                }
                _strikeConsumed = true;
            }
            if (_strikeConsumed)
            {
                _prevTip = tip;
                return;
            }

            _frameSet.Clear();
            int n = Physics.OverlapCapsuleNonAlloc(
                _prevTip, tip, hitRadius, _overlap, hitMask, QueryTriggerInteraction.Collide);

            for (int i = 0; i < n; i++)
            {
                if (!_overlap[i].TryGetComponent(out DamageTarget target) || target.IsDead || target == _selfTarget)
                    continue;

                _frameSet.Add(target);
                if (_contacts.Contains(target)) continue; // same ongoing episode - already hit once

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
            GUI.Label(new Rect(12f, 300f, 620f, 90f),
                $"motionLoad(live) {liveLoad:F2}   " +
                $"(spd {(weapon != null ? weapon.WeaponSpeed : 0f):F1}/{damageSpeedRef:F0}  x  " +
                $"span {(weapon != null ? weapon.WeaponSwingSpan : 0f):F0}/{damageAngleRef:F0})\n" +
                $"lastHit  -{_lastDmg:F1}   (spd {_lastSpd:F1}, span {_lastSpan:F0}, load {_lastLoad:F2}, " +
                $"min {minimumContactDamage:F0}, max {maxDamage:F0})\n" +
                $"contacts {_contacts.Count}   consumed {_strikeConsumed}");
        }
    }
}
