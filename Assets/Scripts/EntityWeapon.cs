using UnityEngine;

namespace WeaponExperiment
{
    /// <summary>
    /// AI-commanded weapon. Same physical output as <see cref="MouseWeapon"/> (drives a
    /// pivot + fixed-length club by transform, exposes real tip speed / swing span / tip
    /// position through <see cref="IWeapon"/>) so it feeds the SAME <see cref="WeaponHitSensor"/>
    /// / <see cref="DamageTarget"/> pipeline. The only difference from the player weapon is
    /// the input: <see cref="StartSwing"/> instead of a mouse.
    ///
    /// One swing = wind-up -> ~swingArc strike -> return to ready. The hit window is only
    /// the strike phase, so a missed swing (player stepped out of the arc) deals nothing.
    /// No combo / stab / feint / prediction.
    /// </summary>
    public class EntityWeapon : MonoBehaviour, IWeapon, IWeaponRecoil
    {
        [Header("References")]
        [SerializeField] private Transform pivot;
        [SerializeField] private Transform club;

        [Header("Geometry")]
        [Tooltip("Reach while swinging (tip fully extended). Ready pose pulls in to readyReach.")]
        [SerializeField] private float reach = 1.5f;
        [Tooltip("Reach while in Side Ready (compact; tip not out in front of the body).")]
        [SerializeField] private float readyReach = 0.9f;
        [SerializeField] private float clubLength = 1.2f;

        [Header("Swing")]
        [Tooltip("Arc of one strike, degrees (wind-up start to strike end).")]
        [SerializeField] private float swingArc = 85f;
        [Tooltip("Seconds for a full swing: wind-up + strike + return.")]
        [SerializeField] private float swingDuration = 0.6f;
        [Range(0f, 1f)] [SerializeField] private float windupFrac = 0.28f;
        [Range(0f, 1f)] [SerializeField] private float strikeFrac = 0.34f;
        [Tooltip("Seconds for the weapon to ease back to the ready pose (facing the body's forward).")]
        [SerializeField] private float readySmoothTime = 0.15f;
        [Tooltip("Degrees the RESTING weapon is held off the body's facing - a SIDE carry (~75-90), " +
                 "not an aim. The strike still snapshots the real target bearing.")]
        [SerializeField] private float readyYawOffset = 78f;

        [Header("Weapon motion metric (for Damage)")]
        [SerializeField] private float weaponSpanWindow = 0.25f;

        [Header("Contact solver (weapon vs shield)")]
        [SerializeField] private WeaponContactConfig contact = new WeaponContactConfig();

        private WeaponContactSolver _solver;
        private Object _selfOwner;

        private float _aimYaw;                 // continuous (unwrapped) world yaw of the weapon
        private float _curReach;              // eased between readyReach (idle) and reach (swinging)
        private Vector3 _aimDir = Vector3.forward;

        private bool _swinging;
        private bool _strikeWindow;
        private float _swingT;                 // 0..1 through swingDuration
        private float _startYaw, _fromYaw, _toYaw;

        private Vector3 _prevTipOffset;
        private Vector3 _tipVel;

        private const int SpanCap = 256;
        private readonly float[] _spanT = new float[SpanCap];
        private readonly float[] _spanA = new float[SpanCap];
        private int _spanHead, _spanCount;
        private float _weaponSpan;

        private bool _alive = true;

        public bool IsSwinging => _swinging;

        // ---- IWeapon ----
        public float WeaponSpeed => _tipVel.magnitude;
        public Vector3 WeaponTipVelocity => _tipVel;
        public float WeaponSwingSpan => _weaponSpan;
        public bool WeaponCanHit => _alive && _strikeWindow && !(_solver != null && _solver.InImpactResponse);
        public Vector3 WeaponTipWorld => (pivot != null ? pivot.position : transform.position) + _aimDir * _curReach;

        // ---- IWeaponRecoil (contact response) ----
        // True ONLY while a real Impact response window owns the yaw. A weapon merely RESTING
        // against a shield (no closing speed) does NOT set this - so the enemy AI, which only
        // consults IsRecoiling, keeps attacking normally in front of an extended shield.
        public bool IsRecoiling => _solver != null && _solver.InImpactResponse;

        public void ApplyRecoil(Vector3 worldRecoilVelocity, float interruptSeconds)
        {
            _swinging = false;
            _strikeWindow = false;
            _solver?.InjectResponse(worldRecoilVelocity, interruptSeconds, _curReach, _aimYaw);
        }

        public void SetAlive(bool value)
        {
            _alive = value;
            if (!value) { _swinging = false; _strikeWindow = false; }
        }

        private void Awake()
        {
            if (pivot == null) pivot = transform.Find("EntityWeaponPivot");
            if (club == null && pivot != null && pivot.childCount > 0) club = pivot.GetChild(0);

            _aimYaw = pivot != null ? pivot.eulerAngles.y : transform.eulerAngles.y;
            _aimDir = YawDir(_aimYaw);
            _curReach = readyReach;
            _prevTipOffset = _aimDir * _curReach;
            _selfOwner = GetComponentInParent<DamageTarget>();
            _solver = new WeaponContactSolver(contact);
            _solver.Reset(_aimYaw, _curReach, (pivot != null ? pivot.position : transform.position));

            if (club != null)
            {
                club.localRotation = Quaternion.Euler(90f, 0f, 0f);
                Vector3 s = club.localScale;
                club.localScale = new Vector3(s.x, clubLength * 0.5f, s.z);
            }
        }

        /// <summary>Begin one swing whose strike sweeps across <paramref name="centreYawDeg"/>
        /// (world yaw toward the target).</summary>
        public void StartSwing(float centreYawDeg)
        {
            // Allowed exception: don't start a new swing while a real Impact response is running.
            if (!_alive || _swinging || (_solver != null && _solver.InImpactResponse)) return;
            _swinging = true;
            _strikeWindow = false;
            _swingT = 0f;
            _startYaw = _aimYaw;
            _fromYaw = centreYawDeg - swingArc * 0.5f;
            _toYaw = centreYawDeg + swingArc * 0.5f;
        }

        private void LateUpdate()
        {
            float dt = Time.deltaTime;
            float readyYaw = transform.eulerAngles.y + readyYawOffset; // held guard, off the body's facing
            float committedPrev = _aimYaw;

            // ----- (1) INTENT: what the swing/idle wants this frame (skipped while a real
            //           Impact response is dominating - the solver drives the yaw then) -----
            if (_solver != null && _solver.InImpactResponse)
            {
                _strikeWindow = false;
            }
            else if (_swinging)
            {
                _swingT += dt / Mathf.Max(swingDuration, 1e-3f);
                float wEnd = Mathf.Clamp01(windupFrac);
                float sEnd = Mathf.Clamp01(windupFrac + strikeFrac);

                if (_swingT < wEnd)
                {
                    _aimYaw = Mathf.LerpAngle(_startYaw, _fromYaw, wEnd > 1e-4f ? _swingT / wEnd : 1f);
                    _strikeWindow = false;
                }
                else if (_swingT < sEnd)
                {
                    _aimYaw = Mathf.LerpAngle(_fromYaw, _toYaw, Mathf.InverseLerp(wEnd, sEnd, _swingT));
                    _strikeWindow = true;
                }
                else if (_swingT < 1f)
                {
                    _aimYaw = Mathf.LerpAngle(_toYaw, readyYaw, Mathf.InverseLerp(sEnd, 1f, _swingT));
                    _strikeWindow = false;
                }
                else
                {
                    _swinging = false;
                    _strikeWindow = false;
                }
            }
            else
            {
                float t = 1f - Mathf.Exp(-dt / Mathf.Max(readySmoothTime, 1e-4f));
                _aimYaw = Mathf.LerpAngle(_aimYaw, readyYaw, t);
                _strikeWindow = false;
            }

            // Reach eases in to readyReach at rest, out to full reach while swinging so the
            // strike travels / accelerates out of the Side Ready pose instead of teleporting.
            _curReach = Mathf.Lerp(_curReach, _swinging ? reach : readyReach,
                                   1f - Mathf.Exp(-dt / Mathf.Max(readySmoothTime, 1e-4f)));

            // ----- (2) CONTACT SOLVE: sub-step the whole weapon capsule across the yaw and
            //           clamp it so the SHAFT never penetrates a shield. A resting contact
            //           only clamps (the swing keeps running against the block and returns
            //           on its own); a real Impact aborts the swing and owns the trajectory.
            Vector3 origin = pivot != null ? pivot.position : transform.position;
            _aimYaw = _solver.Step(_aimYaw, committedPrev, _curReach, origin, _tipVel, _selfOwner, dt);
            if (_solver.ImpactThisFrame) { _swinging = false; _strikeWindow = false; }

            _aimDir = YawDir(_aimYaw);

            // Tip velocity relative to the entity (excludes the entity's own move / dash).
            Vector3 tipOffset = _aimDir * _curReach;
            Vector3 rawVel = (tipOffset - _prevTipOffset) / Mathf.Max(dt, 1e-5f);
            _prevTipOffset = tipOffset;
            _tipVel = Vector3.Lerp(_tipVel, rawVel, 1f - Mathf.Exp(-20f * dt));

            // Weapon swing span = max-min of the continuous _aimYaw over the recent window.
            _spanT[_spanHead] = Time.time;
            _spanA[_spanHead] = _aimYaw;
            _spanHead = (_spanHead + 1) % SpanCap;
            if (_spanCount < SpanCap) _spanCount++;
            float cut = Time.time - Mathf.Max(weaponSpanWindow, 1e-4f);
            float lo = _aimYaw, hi = _aimYaw;
            for (int i = 0; i < _spanCount; i++)
            {
                int idx = (_spanHead - 1 - i + SpanCap) % SpanCap;
                if (_spanT[idx] < cut) break;
                float a = _spanA[idx];
                if (a < lo) lo = a; else if (a > hi) hi = a;
            }
            _weaponSpan = hi - lo;

            // Compose (same shape as MouseWeapon's compose step).
            if (pivot != null)
                pivot.rotation = Quaternion.Euler(0f, _aimYaw, 0f);
            if (club != null)
                club.localPosition = new Vector3(0f, 0f, _curReach - clubLength * 0.5f);
        }

        private static Vector3 YawDir(float deg)
        {
            float r = deg * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(r), 0f, Mathf.Cos(r));
        }
    }
}
