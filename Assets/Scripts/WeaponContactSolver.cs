using UnityEngine;

namespace WeaponExperiment
{
    /// <summary>Tuning for <see cref="WeaponContactSolver"/>. One serialized block per weapon.</summary>
    [System.Serializable]
    public class WeaponContactConfig
    {
        [Header("Shape (weapon collision segment)")]
        [Tooltip("Layers the weapon shape tests against. Only colliders with a PlayerShield count.")]
        public LayerMask mask = ~0;
        [Tooltip("Length of the collision segment measured back from the tip (roughly the club length).")]
        public float shaftLength = 1.2f;
        [Tooltip("Segment start never gets closer to the pivot than this (grip does not go behind the hand).")]
        public float shaftStartOffset = 0.15f;
        [Tooltip("Radius of the segment capsule.")]
        public float shaftRadius = 0.12f;

        [Header("Rotating sweep")]
        [Tooltip("Max degrees of yaw checked per sub-step - small enough that the shaft cannot tunnel a shield in one frame.")]
        public float maxAngularStep = 6f;
        [Tooltip("Binary-refine iterations between the last-valid and first-invalid sub-step.")]
        public int refineIters = 4;
        [Tooltip("If the previous pose already overlaps, this many small steps are tried to nudge back out.")]
        public int pushOutSteps = 6;
        [Tooltip("The resolved pose is held this many degrees short of the contact.")]
        public float skinDegrees = 0.5f;

        [Header("Contact vs Impact")]
        [Tooltip("Closing speed (m/s) at the contact point above which it is an IMPACT (response + IsRecoiling). " +
                 "Below = resting/sliding contact: penetration is still blocked but there is NO recoil.")]
        public float impactThreshold = 1.5f;

        [Header("Impact response")]
        [Range(0f, 1f)] public float restitution = 0.55f;
        [Range(0f, 1f)] public float tangentialRetention = 0.5f;
        public float responseDamping = 10f;
        public float impactSpeedRef = 10f;
        public float dominateMin = 0.05f;
        public float dominateMax = 0.18f;
    }

    /// <summary>
    /// Kinematic contact solver 0.2 - the weapon is a SEGMENT (capsule), not a point, and
    /// its whole rotating sweep is checked, not just the tip.
    ///
    ///   desired yaw  ->  [Step]  ->  resolved yaw  ->  commit to pivot.rotation
    ///
    /// Step sub-steps the yaw from the previous pose to the desired pose in <= maxAngularStep
    /// increments, testing an OverlapCapsule (segment [reach-shaftLength .. reach], radius
    /// shaftRadius) against foreign shields at each pose. The first overlapping pose is
    /// binary-refined against the last clear one to get the contact yaw; the weapon is
    /// committed there so the SHAFT never penetrates.
    ///
    /// Contact vs Impact is decided from the RELATIVE velocity of the weapon contact point
    /// and the shield contact point along the contact normal:
    ///   closing &lt;= impactThreshold  -> resting/sliding contact: clamp only, NO recoil,
    ///                                    InImpactResponse = false (AI unaffected).
    ///   closing &gt;  impactThreshold  -> impact: a normal/tangent response owns the yaw for
    ///                                    a short window, InImpactResponse = true.
    ///
    /// Not a rigidbody: no forces, no mass. 1-DOF (yaw) only.
    /// </summary>
    public class WeaponContactSolver
    {
        private readonly WeaponContactConfig _cfg;
        private readonly Collider[] _ovl = new Collider[16];

        private bool _primed;
        private Vector3 _prevOrigin; // pivot world pos last frame - for carrier (body) translation velocity

        private float _responseYawVel;   // deg/s
        private float _responseTimer;    // s
        private float _impactRefractory; // s - after a response ends, contact is treated as resting-only
        private bool _contactThisFrame;
        private bool _impactThisFrame;
        private bool _justEnded;

        // scratch for the active Step
        private float _segStart, _segEnd;
        private Vector3 _origin;
        private Object _self;

        public WeaponContactSolver(WeaponContactConfig cfg) { _cfg = cfg; }

        /// <summary>An IMPACT response window is running (it owns the yaw; AI swing-start blocked).</summary>
        public bool InImpactResponse => _responseTimer > 0f;
        /// <summary>The shaft was clamped against a shield this frame (resting OR impact). Sensor backstop.</summary>
        public bool ContactThisFrame => _contactThisFrame;
        /// <summary>An impact response was started this frame (weapon should abandon its swing).</summary>
        public bool ImpactThisFrame => _impactThisFrame;
        /// <summary>True the single frame an impact response window closed.</summary>
        public bool JustEnded => _justEnded;

        public void Reset(float yaw, float reach, Vector3 origin)
        {
            _responseTimer = 0f;
            _responseYawVel = 0f;
            _impactRefractory = 0f;
            _contactThisFrame = _impactThisFrame = _justEnded = false;
            _prevOrigin = origin;
            _primed = true;
        }

        /// <summary>External inject (future shield-side call). Rare; normal path is Step detecting it.</summary>
        public void InjectResponse(Vector3 worldResponseVel, float dominateSeconds, float reach, float yaw)
        {
            Vector3 tan = Perp(YawDir(yaw));
            _responseYawVel = Vector3.Dot(worldResponseVel, tan) / Mathf.Max(reach, 0.1f) * Mathf.Rad2Deg;
            _responseTimer = Mathf.Max(_responseTimer, dominateSeconds);
        }

        /// <summary>Resolve one frame. Returns the yaw to actually commit.</summary>
        public float Step(float desiredYaw, float committedYawPrev, float reach, Vector3 origin,
                          Vector3 incomingTipVel, Object selfOwner, float dt)
        {
            _justEnded = false;
            _contactThisFrame = false;
            _impactThisFrame = false;
            if (_impactRefractory > 0f) _impactRefractory -= dt;
            if (!_primed) { _prevOrigin = origin; _primed = true; }

            _segEnd = reach;
            _segStart = Mathf.Max(_cfg.shaftStartOffset, reach - _cfg.shaftLength);
            _origin = origin;
            _self = selfOwner;

            // Where does the yaw want to go this frame?
            bool responseActive = _responseTimer > 0f;
            float targetYaw;
            if (responseActive)
            {
                _responseTimer -= dt;
                targetYaw = committedYawPrev + _responseYawVel * dt;
                _responseYawVel = Mathf.Lerp(_responseYawVel, 0f, 1f - Mathf.Exp(-_cfg.responseDamping * dt));
                if (_responseTimer <= 0f) { _justEnded = true; _impactRefractory = _cfg.dominateMax; }
            }
            else
            {
                targetYaw = desiredYaw;
            }

            // --- Broad phase: any foreign shield within the weapon's reach? ---
            if (!AnyForeignShieldNear(origin, reach + _cfg.shaftRadius + 0.1f))
            {
                _prevOrigin = origin;
                return targetYaw;
            }

            // --- If the previous pose already overlaps, nudge out (resting shield pushed into us). ---
            float startYaw = committedYawPrev;
            if (ShaftHits(startYaw, out _))
            {
                bool cleared = false;
                for (int s = 1; s <= _cfg.pushOutSteps && !cleared; s++)
                {
                    float a = committedYawPrev + _cfg.maxAngularStep * s;
                    float b = committedYawPrev - _cfg.maxAngularStep * s;
                    if (!ShaftHits(a, out _)) { startYaw = a; cleared = true; }
                    else if (!ShaftHits(b, out _)) { startYaw = b; cleared = true; }
                }
                if (!cleared)
                {
                    // Engulfed - freeze. Still a contact (constraint), not an impact.
                    _contactThisFrame = true;
                    _prevOrigin = origin;
                    return committedYawPrev;
                }
            }

            // --- Sub-step sweep startYaw -> targetYaw ---
            float delta = Mathf.DeltaAngle(startYaw, targetYaw);
            int steps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Abs(delta) / Mathf.Max(_cfg.maxAngularStep, 0.5f)));
            float lastValid = startYaw;
            float firstInvalid = targetYaw;
            Collider hitCol = null;
            PlayerShield hitShield = null;
            bool contacted = false;

            for (int i = 1; i <= steps; i++)
            {
                float y = startYaw + delta * (i / (float)steps);
                if (ShaftHits(y, out Collider c))
                {
                    hitCol = c;
                    c.TryGetComponent(out hitShield);
                    firstInvalid = y;
                    contacted = true;
                    break;
                }
                lastValid = y;
            }

            if (!contacted)
            {
                _prevOrigin = origin;
                return targetYaw;
            }

            // --- Binary refine to the contact yaw ---
            float lo = lastValid, hi = firstInvalid;
            for (int r = 0; r < _cfg.refineIters; r++)
            {
                float mid = 0.5f * (lo + hi);
                if (ShaftHits(mid, out _)) hi = mid; else lo = mid;
            }
            float contactYaw = lo + Mathf.Sign(hi - lo) * (-Mathf.Min(_cfg.skinDegrees, Mathf.Abs(hi - lo)));
            _contactThisFrame = true;

            // --- Contact point + normal on the resolved pose (weapon/shield points kept for
            //     future debug draw; only the normal + moment arm are needed for the response) ---
            GetContact(contactYaw, hitCol, out _, out _, out Vector3 n, out float radiusAtContact);

            // --- Contact vs Impact (only classify when not already in a response) ---
            if (!responseActive)
            {
                float omegaDeg = Mathf.DeltaAngle(committedYawPrev, desiredYaw) / Mathf.Max(dt, 1e-5f); // intent ang vel
                Vector3 weaponContactVel =
                    Perp(YawDir(contactYaw)) * (omegaDeg * Mathf.Deg2Rad * radiusAtContact)   // swing tangential at that radius
                    + (origin - _prevOrigin) / Mathf.Max(dt, 1e-5f);                          // carrier translation
                Vector3 shieldContactVel = hitShield != null ? hitShield.ShieldVelocity : Vector3.zero;

                Vector3 relVel = weaponContactVel - shieldContactVel;
                float closing = Mathf.Max(0f, -Vector3.Dot(relVel, n)); // > 0 = moving into each other

                if (closing > _cfg.impactThreshold && _impactRefractory <= 0f)
                {
                    Vector3 vN = Vector3.Dot(relVel, n) * n;
                    Vector3 vT = relVel - vN;
                    Vector3 responseVel = (-_cfg.restitution) * vN + _cfg.tangentialRetention * vT;
                    _responseYawVel = Vector3.Dot(responseVel, Perp(YawDir(contactYaw)))
                                      / Mathf.Max(radiusAtContact, 0.1f) * Mathf.Rad2Deg;
                    _responseTimer = Mathf.Lerp(_cfg.dominateMin, _cfg.dominateMax,
                        Mathf.Clamp01(closing / Mathf.Max(_cfg.impactSpeedRef, 1e-3f)));
                    _impactThisFrame = true;
                }
                // else: resting / sliding contact -> the clamp is the whole response.
            }

            _prevOrigin = origin;
            return contactYaw;
        }

        // ---- helpers ----

        private bool ShaftHits(float yaw, out Collider shield)
        {
            Vector3 dir = YawDir(yaw);
            Vector3 p0 = _origin + dir * _segStart;
            Vector3 p1 = _origin + dir * _segEnd;
            int n = Physics.OverlapCapsuleNonAlloc(p0, p1, _cfg.shaftRadius, _ovl, _cfg.mask, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                if (IsForeignShield(_ovl[i], _self)) { shield = _ovl[i]; return true; }
            }
            shield = null;
            return false;
        }

        private bool AnyForeignShieldNear(Vector3 origin, float radius)
        {
            int n = Physics.OverlapSphereNonAlloc(origin, radius, _ovl, _cfg.mask, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
                if (IsForeignShield(_ovl[i], _self)) return true;
            return false;
        }

        /// <summary>Closest point between the weapon segment (at <paramref name="yaw"/>) and the shield.</summary>
        private void GetContact(float yaw, Collider shieldCol, out Vector3 weaponPt, out Vector3 shieldPt,
                                out Vector3 normal, out float radiusAtContact)
        {
            Vector3 dir = YawDir(yaw);
            Vector3 a = _origin + dir * _segStart;
            Vector3 b = _origin + dir * _segEnd;

            const int samples = 7;
            float bestSq = float.MaxValue;
            weaponPt = b; shieldPt = b; radiusAtContact = _segEnd;
            for (int i = 0; i <= samples; i++)
            {
                float t = i / (float)samples;
                Vector3 wp = Vector3.Lerp(a, b, t);
                Vector3 sp = shieldCol.ClosestPoint(wp);
                float d = (wp - sp).sqrMagnitude;
                if (d < bestSq)
                {
                    bestSq = d;
                    weaponPt = wp;
                    shieldPt = sp;
                    radiusAtContact = Mathf.Lerp(_segStart, _segEnd, t);
                }
            }

            normal = weaponPt - shieldPt;
            if (normal.sqrMagnitude < 1e-8f) normal = weaponPt - shieldCol.bounds.center; // deep overlap
            normal = normal.sqrMagnitude > 1e-10f ? normal.normalized : Vector3.up;
        }

        private static bool IsForeignShield(Collider c, Object selfOwner)
        {
            if (c == null || !c.TryGetComponent(out PlayerShield sh)) return false;
            DamageTarget owner = sh.GetComponentInParent<DamageTarget>();
            return owner != null && !owner.IsDead && (Object)owner != selfOwner;
        }

        private static Vector3 Perp(Vector3 dir) => new Vector3(dir.z, 0f, -dir.x); // -90 deg on XZ

        private static Vector3 YawDir(float deg)
        {
            float r = deg * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(r), 0f, Mathf.Cos(r));
        }
    }
}
