using UnityEngine;
using UnityEngine.InputSystem;

namespace WeaponExperiment
{
    /// <summary>
    /// Weapon Endpoint Drag (0.1) + Weapon Stow Zone (0.1).
    ///
    /// ACTIVE (cursor away from the player): unchanged direct manipulation. The cursor's
    /// position guides a bearing/reach target (SmoothDamp, no speed ceiling) and the
    /// cursor's own velocity is split against the current facing into a radial part
    /// (-> _reachVel) and a tangential part (v/r -> deg/s -> _aimYawVel). Near the very
    /// centre the yaw feed-forward and target refresh are suppressed (turnDeadZone) so a
    /// fast pass over the player cannot spin the retracted weapon.
    ///
    /// STOW ZONE (cursor on top of the player): the player centre is a "safe stow"
    /// region, NOT a stab input. With hysteresis (stowRadius to enter, unstowRadius to
    /// leave) the weapon toggles between Active and Stowed. While Stowed, ALL motion
    /// input is ignored (no yaw/reach feed-forward, carried velocities cleared) and the
    /// weapon eases to a fixed holster pose. Drawing back out resyncs the bearing to the
    /// current cursor and resets the motion baseline so no accumulated velocity is
    /// dumped into the first active frame; subsequent motion swings normally again.
    ///
    /// So sweeping the cursor straight through the player reads as swing -> stow -> draw
    /// -> swing, never as a thrust. There are no attack states, no gesture/trajectory
    /// classification, no slash/stab methods. NOT here: rigidbodies, mass/inertia,
    /// balance/force transfer, springs, arms/IK, inventory, damage, combos, animation.
    /// </summary>
    public class MouseWeapon : MonoBehaviour, IWeapon, IWeaponRecoil
    {
        [Header("References")]
        [Tooltip("Weapon origin A. Empty transform at the player. Auto-found as child \"WeaponPivot\".")]
        [SerializeField] private Transform pivot;
        [Tooltip("The fixed-length club visual. Auto-found as the pivot's first child.")]
        [SerializeField] private Transform club;
        [Tooltip("Camera used to project the cursor onto the ground. Defaults to Camera.main.")]
        [SerializeField] private Camera cam;

        [Header("Reach (pull in / push out)")]
        [Tooltip("Closest the weapon tip may sit to the player (pulled in to the body).")]
        [SerializeField] private float minReach = 0.6f;
        [Tooltip("Farthest the weapon tip may sit from the player (fully extended).")]
        [SerializeField] private float maxReach = 2.0f;
        [Tooltip("Roughly the time (seconds) for reach to catch up to the cursor position. " +
                 "Smaller = snappier; larger = mushier. No speed ceiling either way.")]
        [SerializeField] private float reachSmoothTime = 0.07f;

        [Header("Direction (swing)")]
        [Tooltip("Roughly the time (seconds) for the facing to catch up to the cursor bearing. " +
                 "Smaller = whippier; larger = heavier.")]
        [SerializeField] private float turnSmoothTime = 0.08f;
        [Tooltip("Active-only inner guard: within this distance of the player the " +
                 "Player->cursor bearing is meaningless, so facing is not updated and the " +
                 "yaw feed-forward is suppressed. Separate from the stow radii below.")]
        [SerializeField] private float turnDeadZone = 0.35f;

        [Header("Mouse velocity input")]
        [Tooltip("How strongly the cursor's own motion (not just its position) is injected " +
                 "into the weapon's velocity each second. 0 = position-only. Higher = the " +
                 "weapon more literally inherits how you moved the mouse.")]
        [SerializeField] private float velInfluence = 18f;
        [Tooltip("Low-pass on the measured cursor velocity, per second. Higher = more " +
                 "responsive but more jitter; lower = smoother but laggier.")]
        [SerializeField] private float mouseVelSmoothing = 20f;

        [Header("Stow zone")]
        [Tooltip("Cursor-to-player distance at/below which the weapon STOWS. Enter threshold.")]
        [SerializeField] private float stowRadius = 0.5f;
        [Tooltip("Cursor-to-player distance at/above which the weapon DRAWS again. Leave " +
                 "threshold. Keep it comfortably larger than stowRadius (hysteresis band).")]
        [SerializeField] private float unstowRadius = 0.95f;
        [Tooltip("Seconds for the stow / draw pose transition and for the holster settle.")]
        [SerializeField] private float stowSmoothTime = 0.12f;
        [Tooltip("Fixed world yaw (deg) the pivot rests at while stowed. Just picks which " +
                 "side the stowed weapon sits; the player has no facing of its own.")]
        [SerializeField] private float stowYaw = 135f;
        [Tooltip("Club local position (in the stow-yaw pivot frame) while fully stowed. " +
                 "Small offsets that read as 'clipped to the body'.")]
        [SerializeField] private Vector3 stowClubLocalPos = new Vector3(0f, 0.25f, -0.25f);
        [Tooltip("Club local euler while fully stowed (drawn pose is 90,0,0 = horizontal).")]
        [SerializeField] private Vector3 stowClubLocalEuler = new Vector3(50f, 0f, 0f);

        [Header("Club visual")]
        [Tooltip("Fixed world length of the club mesh. Never stretched; only slid.")]
        [SerializeField] private float clubLength = 1.2f;

        [Header("Overswing detection")]
        [Tooltip("Recent-motion window (seconds) whose angular SPAN is evaluated for OverSwing.")]
        [SerializeField] private float overswingWindow = 0.18f;
        [Tooltip("Cursor speed (m/s, world-plane) that counts as 'fast' (speed factor = 1).")]
        [SerializeField] private float overswingSpeedRef = 12f;
        [Tooltip("AngularSpan (widest bearing arc, deg, swept within the window) that counts as " +
                 "'wide' (angle factor = 1). NOT cumulative travel - a fast narrow wiggle stays small.")]
        [SerializeField] private float overswingAngleRef = 130f;
        [Tooltip("swingLoad (= speedFactor * angleFactor) at/above which OverSwing fires. Both " +
                 "factors must be sizeable at once: fast+narrow or slow+wide stays below.")]
        [SerializeField] private float overswingThreshold = 1f;

        [Header("Weapon motion (read by WeaponHitSensor for Damage)")]
        [Tooltip("Recent window (s) over which the ACTUAL weapon facing's angular span is measured.")]
        [SerializeField] private float weaponSpanWindow = 0.25f;

        [Header("Contact solver (weapon vs shield)")]
        [SerializeField] private WeaponContactConfig contact = new WeaponContactConfig();

        [Header("Engage (LMB) + Side Ready")]
        [Tooltip("Provides the Player facing + LMB engage flag. Auto: GetComponent. If missing, the " +
                 "weapon is always engaged (old always-on behaviour).")]
        [SerializeField] private PlayerFacing playerFacing;
        [Tooltip("Degrees the resting weapon is held off the Player facing (a side carry, not an aim).")]
        [SerializeField] private float readyYawOffset = -50f;
        [Tooltip("Reach the weapon pulls in to while in Side Ready (compact, tip not far in front).")]
        [SerializeField] private float readyReach = 0.9f;
        [Tooltip("Seconds to ease into / out of the Side Ready pose.")]
        [SerializeField] private float readySmoothTime = 0.15f;

        [Header("Debug")]
        [Tooltip("On-screen readout of mLen / targetReach / _reach / yaw error / stowed / swing load.")]
        [SerializeField] private bool showDebug = true;

        private const float MaxMouseSpeed = 40f; // safety rail against cursor warps / alt-tab

        private float _aimYaw;     // degrees
        private float _aimYawVel;  // deg/s, carried by the follower
        private float _targetYaw;  // last cursor bearing seen outside the dead zone
        private float _reach;
        private float _reachVel;   // m/s, carried by the follower
        private Vector3 _aimDir = Vector3.forward; // derived from _aimYaw, on the XZ plane

        private Vector3 _prevMouseOffset; // cursor offset from origin last frame (XZ)
        private Vector3 _mouseVel;        // low-passed cursor velocity (XZ, m/s)
        private bool _mousePrimed;

        private bool _stowed;
        private float _stowBlend; // 0 = fully drawn, 1 = fully stowed (visual pose blend)

        private float _prevBearing;          // deg, last frame's raw bearing (for the debug leaky travel)
        private float _angTravelWindow;      // deg cumulatively swept over ~overswingWindow (DEBUG ONLY now)

        // AngularSpan: widest bearing arc actually covered in the recent window. Unwrapped
        // bearing (continuous, no 360 jump) sampled into a small ring buffer; span = max - min
        // over samples newer than overswingWindow. A fast narrow wiggle stays ~its amplitude.
        private const int SpanSampleCap = 256; // covers up to a ~1s window at 256 fps; degrades gracefully above
        private readonly float[] _spanT = new float[SpanSampleCap]; // timestamps
        private readonly float[] _spanA = new float[SpanSampleCap]; // unwrapped bearing (deg)
        private int _spanHead;               // next write index
        private int _spanCount;
        private float _prevRawBearing;       // deg, for unwrapping
        private float _unwrappedBearing;     // deg, continuous
        private float _angularSpan;          // deg, drives angleFactor

        // WeaponSwingSpan: widest arc the ACTUAL weapon facing (_aimYaw - already a continuous
        // unwrapped angle) covered in the recent weaponSpanWindow. For Damage; never mouse-sourced.
        private readonly float[] _wSpanT = new float[SpanSampleCap];
        private readonly float[] _wSpanA = new float[SpanSampleCap];
        private int _wSpanHead, _wSpanCount;
        private float _weaponSpan;           // deg

        private float _swingLoad;            // speedFactor * angleFactor
        private Vector3 _prevTipOffset;      // tip position relative to origin, last frame
        private Vector3 _tipVel;             // low-passed tip velocity relative to origin (excludes walking)
        private Vector3 _tipVelDir = Vector3.forward;
        private bool _suspended;             // OverSwing consequence: all control blocked, weapon forced to a safe stow

        private WeaponContactSolver _solver; // resolves the tip vs foreign shields before the pose commits
        private Object _selfOwner;
        private bool _engaged;               // LMB held -> direct mouse control; else Side Ready
        private bool _prevEngaged;

        private float _dbgMLen, _dbgTargetReach, _dbgYawErr; // read-only snapshot for the debug HUD

        private void Awake()
        {
            if (pivot == null)
                pivot = transform.Find("WeaponPivot");
            if (club == null && pivot != null && pivot.childCount > 0)
                club = pivot.GetChild(0);
            if (cam == null)
                cam = Camera.main;

            _aimYaw = _targetYaw = pivot != null ? pivot.eulerAngles.y : 0f;
            _aimDir = YawDir(_aimYaw);
            _reach = Mathf.Lerp(minReach, maxReach, 0.5f);
            _prevBearing = _aimYaw;
            _prevRawBearing = _aimYaw;
            _prevTipOffset = _aimDir * _reach;
            _tipVelDir = _aimDir;
            _selfOwner = GetComponentInParent<DamageTarget>();
            _solver = new WeaponContactSolver(contact);
            _solver.Reset(_aimYaw, _reach, (pivot != null ? pivot.position : transform.position));
            if (playerFacing == null) playerFacing = GetComponent<PlayerFacing>();
            _prevEngaged = _engaged = playerFacing == null;

            // NOTE: `club` is the PHYSICAL reach representation only (its localPosition.z is
            // written every frame below to place the tip at _reach) - its rotation/scale stay
            // identity. Any mesh alignment (e.g. laying a cylinder along +Z, stretching it to
            // clubLength) is scene-authored on club's own "VisualRoot/Placeholder" child instead,
            // so swapping in an external weapon mesh never touches this gameplay-owned transform.
        }

        private void LateUpdate()
        {
            if (pivot == null || cam == null)
                return;

            float dt = Time.deltaTime;
            Vector3 origin = pivot.position;

            // Cursor offset from the weapon origin, flattened onto the XZ plane.
            Vector3 m = MouseOnPlane(origin.y) - origin;
            m.y = 0f;
            float mLen = m.magnitude;

            // --- Cursor world velocity, measured relative to the origin so that walking
            //     (origin moving) does not inject a phantom velocity. Low-passed + clamped.
            //     Always measured so the value never goes stale; only APPLIED while Active.
            if (!_mousePrimed)
            {
                _prevMouseOffset = m;
                _mousePrimed = true;
            }
            Vector3 rawVel = (m - _prevMouseOffset) / Mathf.Max(dt, 1e-5f);
            _prevMouseOffset = m;
            if (rawVel.magnitude > MaxMouseSpeed)
                rawVel = rawVel.normalized * MaxMouseSpeed;
            _mouseVel = Vector3.Lerp(_mouseVel, rawVel, 1f - Mathf.Exp(-mouseVelSmoothing * dt));

            float bearing = Mathf.Atan2(m.x, m.z) * Mathf.Rad2Deg;
            float k = 1f - Mathf.Exp(-velInfluence * dt); // this frame's blend of motion input into carried velocity
            float winDecay = Mathf.Exp(-dt / Mathf.Max(overswingWindow, 1e-4f));
            float committedYawPrev = _aimYaw;

            // --- LMB gate: engaged = direct mouse weapon control; otherwise Side Ready. ---
            _engaged = playerFacing == null || playerFacing.WeaponEngaged;
            if (_engaged != _prevEngaged)
            {
                if (_engaged)
                {
                    // ENGAGE: hand the mouse back to the weapon. It rises from Side Ready toward
                    // the cursor via the normal ACTIVE branch (low mouse speed -> no fake OverSwing).
                    _stowed = false;
                    _targetYaw = bearing;
                    _aimYawVel = 0f;
                    _reachVel = 0f;
                    _mouseVel = Vector3.zero;
                    _mousePrimed = false;
                    ClearSwingHistory(bearing);
                    _solver.Reset(_aimYaw, _reach, origin);
                }
                else
                {
                    // DISENGAGE: drop carried motion; the Side Ready branch eases the weapon back.
                    _aimYawVel = 0f;
                    _reachVel = 0f;
                    _mouseVel = Vector3.zero;
                    _mousePrimed = false;
                    ClearSwingHistory(bearing);
                }
                _prevEngaged = _engaged;
            }

            if (_suspended)
            {
                // ===== FALL: no control at all; hold a safe stowed pose =====
                _mouseVel = Vector3.zero;
                _aimYawVel = 0f;
                _reachVel = 0f;
                ClearSwingHistory(bearing);
                _stowed = true;
                _targetYaw = stowYaw;
                _aimYaw = Mathf.SmoothDampAngle(_aimYaw, _targetYaw, ref _aimYawVel, stowSmoothTime, Mathf.Infinity, dt);
                _aimDir = YawDir(_aimYaw);
                _reach = Mathf.SmoothDamp(_reach, minReach, ref _reachVel, stowSmoothTime, Mathf.Infinity, dt);
            }
            else if (_solver.InImpactResponse)
            {
                // ===== IMPACT RESPONSE: a real shield impact owns the yaw for a brief window.
                //       Mouse ignored; _solver.Step (below) drives _aimYaw. A mere resting
                //       contact does NOT land here - the mouse keeps control and Step just
                //       clamps the yaw so the shaft can slide along the shield without going
                //       through it.
                _mouseVel = Vector3.zero;
                _aimYawVel = 0f;
                _reachVel = 0f;
                ClearSwingHistory(bearing);
            }
            else if (!_engaged)
            {
                // ===== SIDE READY: LMB up. Mouse does NOT drive the weapon (it drives the
                //       Player facing instead). Hold the weapon at a compact side pose;
                //       no swing / no WeaponSpeed as attack / no OverSwing accumulation.
                _mouseVel = Vector3.zero;
                _stowed = false;
                ClearSwingHistory(bearing);
                float readyYaw = (playerFacing != null ? playerFacing.FacingYaw : _aimYaw) + readyYawOffset;
                _aimYaw = Mathf.SmoothDampAngle(_aimYaw, readyYaw, ref _aimYawVel, readySmoothTime, Mathf.Infinity, dt);
                _aimDir = YawDir(_aimYaw);
                _reach = Mathf.SmoothDamp(_reach, readyReach, ref _reachVel, readySmoothTime, Mathf.Infinity, dt);
            }
            else
            {
                // --- STOW ZONE state machine (hysteresis). stowRadius to enter, unstowRadius
                //     to leave; between them the current state is held so it cannot flip-flop.
                float drawRadius = Mathf.Max(unstowRadius, stowRadius + 0.01f);
                if (_stowed)
                {
                    if (mLen >= drawRadius)
                    {
                        // DRAW: resync to the current cursor bearing and wipe any carried /
                        //       accumulated motion so nothing bursts on the first active frame.
                        _stowed = false;
                        _targetYaw = bearing;
                        _aimYaw = _targetYaw;
                        _aimYawVel = 0f;
                        _reachVel = 0f;
                        _mouseVel = Vector3.zero;
                        _prevMouseOffset = m;
                        _aimDir = YawDir(_aimYaw);
                        ClearSwingHistory(bearing);
                    }
                }
                else if (mLen <= stowRadius)
                {
                    // STOW: clear residual velocity, aim for the holster yaw.
                    _stowed = true;
                    _aimYawVel = 0f;
                    _reachVel = 0f;
                    _targetYaw = stowYaw;
                    ClearSwingHistory(bearing);
                }

                if (!_stowed)
                {
                    // ===== ACTIVE: direct weapon manipulation =====

                    // Split the measured cursor velocity on the CURSOR-BEARING basis (mDir),
                    // NOT the weapon facing (_aimDir). During a fast spin _aimDir lags the
                    // cursor by a large angle; splitting on _aimDir bled the circular
                    // (tangential) cursor motion into vRadial and, together with the old
                    // Dot(m, _aimDir) reach target, collapsed reach purely from yaw lag.
                    Vector3 mDir = mLen > 1e-4f ? m / mLen : _aimDir;
                    Vector3 mPerp = new Vector3(mDir.z, 0f, -mDir.x); // mDir rotated -90 deg on XZ
                    float vRadial = Vector3.Dot(_mouseVel, mDir);     // m/s, + = cursor moving away from player
                    float vTangent = Vector3.Dot(_mouseVel, mPerp);   // m/s, + = cursor sweeping toward +yaw
                    float ffYawVel = (vTangent / Mathf.Max(mLen, minReach)) * Mathf.Rad2Deg; // v/r -> deg/s

                    // DIRECTION: inside turnDeadZone the Player->cursor bearing is meaningless,
                    // so we neither refresh the target NOR inject the feed-forward.
                    if (mLen > turnDeadZone)
                    {
                        _targetYaw = bearing;
                        _aimYawVel = Mathf.Lerp(_aimYawVel, ffYawVel, k);
                    }
                    else
                    {
                        _aimYawVel = Mathf.Lerp(_aimYawVel, 0f, k);
                    }
                    _aimYaw = Mathf.SmoothDampAngle(_aimYaw, _targetYaw, ref _aimYawVel, turnSmoothTime, Mathf.Infinity, dt);
                    _aimDir = YawDir(_aimYaw);

                    // REACH: driven by the cursor's DISTANCE (mLen), not its projection onto
                    // the lagging weapon facing, so a lagging yaw does not pull it into the body.
                    _reachVel = Mathf.Lerp(_reachVel, vRadial, k);
                    float targetReach = Mathf.Clamp(mLen, minReach, maxReach);
                    _reach = Mathf.SmoothDamp(_reach, targetReach, ref _reachVel, reachSmoothTime, Mathf.Infinity, dt);
                    if (_reach <= minReach) { _reach = minReach; if (_reachVel < 0f) _reachVel = 0f; }
                    else if (_reach >= maxReach) { _reach = maxReach; if (_reachVel > 0f) _reachVel = 0f; }

                    // OverSwing metrics.
                    // (debug/old) cumulative travel over the window - a fast narrow wiggle
                    // inflates this without limit, which is why it no longer drives SwingLoad.
                    float angDelta = Mathf.Abs(Mathf.DeltaAngle(_prevBearing, bearing));
                    _prevBearing = bearing;
                    _angTravelWindow = _angTravelWindow * winDecay + angDelta;

                    // AngularSpan: widest arc actually covered in the recent window. Unwrap the
                    // bearing (no 360 jump), push into the ring buffer, span = max - min of
                    // samples newer than overswingWindow. Repetition does NOT grow it.
                    _unwrappedBearing += Mathf.DeltaAngle(_prevRawBearing, bearing);
                    _prevRawBearing = bearing;
                    _spanT[_spanHead] = Time.time;
                    _spanA[_spanHead] = _unwrappedBearing;
                    _spanHead = (_spanHead + 1) % SpanSampleCap;
                    if (_spanCount < SpanSampleCap) _spanCount++;
                    float cutoff = Time.time - Mathf.Max(overswingWindow, 1e-4f);
                    float lo = _unwrappedBearing, hi = _unwrappedBearing;
                    for (int i = 0; i < _spanCount; i++)
                    {
                        int idx = (_spanHead - 1 - i + SpanSampleCap) % SpanSampleCap; // newest -> oldest
                        if (_spanT[idx] < cutoff) break;                                // rest are older
                        float a = _spanA[idx];
                        if (a < lo) lo = a; else if (a > hi) hi = a;
                    }
                    _angularSpan = hi - lo;

                    // WeaponSwingSpan: same ring-buffer max-min, but on the ACTUAL weapon
                    // facing _aimYaw (post-SmoothDamp, so it carries the lag/inertia) - which
                    // is already a continuous unwrapped angle, no unwrap step needed.
                    _wSpanT[_wSpanHead] = Time.time;
                    _wSpanA[_wSpanHead] = _aimYaw;
                    _wSpanHead = (_wSpanHead + 1) % SpanSampleCap;
                    if (_wSpanCount < SpanSampleCap) _wSpanCount++;
                    float wCut = Time.time - Mathf.Max(weaponSpanWindow, 1e-4f);
                    float wLo = _aimYaw, wHi = _aimYaw;
                    for (int i = 0; i < _wSpanCount; i++)
                    {
                        int wi = (_wSpanHead - 1 - i + SpanSampleCap) % SpanSampleCap;
                        if (_wSpanT[wi] < wCut) break;
                        float a = _wSpanA[wi];
                        if (a < wLo) wLo = a; else if (a > wHi) wHi = a;
                    }
                    _weaponSpan = wHi - wLo;

                    float speedFactor = _mouseVel.magnitude / Mathf.Max(overswingSpeedRef, 1e-3f);
                    float angleFactor = _angularSpan / Mathf.Max(overswingAngleRef, 1e-3f);
                    _swingLoad = speedFactor * angleFactor;

                    _dbgMLen = mLen;
                    _dbgTargetReach = targetReach;
                    _dbgYawErr = Mathf.DeltaAngle(_aimYaw, _targetYaw);
                }
                else
                {
                    // ===== STOWED: no motion input at all; ease to a fixed holster pose =====
                    ClearSwingHistory(bearing);
                    _targetYaw = stowYaw;
                    _aimYaw = Mathf.SmoothDampAngle(_aimYaw, _targetYaw, ref _aimYawVel, stowSmoothTime, Mathf.Infinity, dt);
                    _aimDir = YawDir(_aimYaw);
                    _reach = Mathf.SmoothDamp(_reach, minReach, ref _reachVel, stowSmoothTime, Mathf.Infinity, dt);
                }
            }

            // --- Contact solve: clamp _aimYaw so the tip never penetrates a foreign shield;
            //     on contact an incoming-velocity response owns the yaw for a brief window.
            //     _tipVel here still holds the PREVIOUS frame's velocity = the incoming velocity.
            _aimYaw = _solver.Step(_aimYaw, committedYawPrev, _reach, origin, _tipVel, _selfOwner, dt);
            _aimDir = YawDir(_aimYaw);
            if (_solver.JustEnded)
                _mousePrimed = false; // resync the cursor baseline so control resumes without a spike

            // --- Weapon tip motion in world space, measured RELATIVE TO ORIGIN so that
            //     walking is excluded - this is the actual direction the weapon head is
            //     sweeping (yaw follow-through included), used as the Stagger drag direction.
            Vector3 tipOffset = _aimDir * _reach;
            Vector3 tipRawVel = (tipOffset - _prevTipOffset) / Mathf.Max(dt, 1e-5f);
            _prevTipOffset = tipOffset;
            _tipVel = Vector3.Lerp(_tipVel, tipRawVel, 1f - Mathf.Exp(-mouseVelSmoothing * dt));
            if (_tipVel.sqrMagnitude > 1e-4f)
                _tipVelDir = _tipVel.normalized;

            // --- Visual pose blend between drawn and stowed.
            float blendT = 1f - Mathf.Exp(-dt / Mathf.Max(stowSmoothTime, 1e-4f));
            _stowBlend = Mathf.Lerp(_stowBlend, _stowed ? 1f : 0f, blendT);

            // --- Compose: aim the pivot, place the fixed-length club.
            pivot.rotation = Quaternion.Euler(0f, _aimYaw, 0f);
            if (club != null)
            {
                Vector3 drawnPos = new Vector3(0f, 0f, _reach - clubLength * 0.5f); // tip sits at B
                club.localPosition = Vector3.Lerp(drawnPos, stowClubLocalPos, _stowBlend);
                // NOTE: unlike EntityWeapon (which never touches club.localRotation - EntityClub
                // has no stow pose), this IS the cylinder-alignment + stow-tilt blend, written every
                // frame. It doubles as "visual alignment" for the Player weapon, so club's
                // "VisualRoot/Placeholder" child must stay at IDENTITY rotation - baking any
                // alignment there too would compose on top of this and rotate the mesh twice.
                club.localRotation = Quaternion.Slerp(
                    Quaternion.Euler(90f, 0f, 0f), Quaternion.Euler(stowClubLocalEuler), _stowBlend);
            }
        }

        // ---- OverSwing / Fall interface (read/driven by PlayerOverswing) ----

        /// <summary>Low-passed cursor speed, world-plane metres per second.</summary>
        public float MouseSpeed => _mouseVel.magnitude;
        /// <summary>DEBUG/old: cumulative |bearing delta| over the window (repetition inflates it).</summary>
        public float AngularTravel => _angTravelWindow;
        /// <summary>Widest bearing arc actually covered in the last overswingWindow. Drives angleFactor.</summary>
        public float AngularSpan => _angularSpan;
        /// <summary>speedFactor * (AngularSpan / overswingAngleRef). >= overswingThreshold => OverSwingActive.</summary>
        public float SwingLoad => _swingLoad;
        /// <summary>True only while Active (drawn, not suspended) and the swing load is over threshold.</summary>
        public bool OverSwingActive => _engaged && !_stowed && !_suspended && _swingLoad >= overswingThreshold;
        /// <summary>Current weapon facing (unit, XZ).</summary>
        public Vector3 AimDir => _aimDir;
        /// <summary>Actual direction the weapon tip is moving (unit, XZ, relative to the player).</summary>
        public Vector3 WeaponMotionDir => _tipVelDir;

        // ---- Damage interface (read by WeaponHitSensor) - ACTUAL weapon motion, not mouse ----

        /// <summary>Actual weapon tip speed (m/s), measured relative to the player - Run / walking
        /// is excluded because _tipVel differentiates the origin-relative tip offset.</summary>
        public float WeaponSpeed => _tipVel.magnitude;
        /// <summary>Actual weapon tip velocity vector (m/s, player-relative). Same source as WeaponSpeed.</summary>
        public Vector3 WeaponTipVelocity => _tipVel;
        /// <summary>Widest arc the ACTUAL (lagged/inertial) weapon facing swept in the recent
        /// weaponSpanWindow (deg). NOT the mouse AngularSpan.</summary>
        public float WeaponSwingSpan => _weaponSpan;
        /// <summary>Weapon may deal damage: LMB-engaged, drawn, not Stumble/Fall-suspended, not in an Impact response.</summary>
        public bool WeaponCanHit => _engaged && !_stowed && !_suspended && !(_solver != null && _solver.InImpactResponse);
        /// <summary>World position of the weapon tip (business end).</summary>
        public Vector3 WeaponTipWorld => (pivot != null ? pivot.position : transform.position) + _aimDir * _reach;

        // ---- IWeaponRecoil (contact response) ----
        // True ONLY during a real Impact response window - never for a resting/sliding contact.
        public bool IsRecoiling => _solver != null && _solver.InImpactResponse;

        public void ApplyRecoil(Vector3 worldRecoilVelocity, float interruptSeconds)
        {
            _aimYawVel = 0f;
            _reachVel = 0f;
            _solver?.InjectResponse(worldRecoilVelocity, interruptSeconds, _reach, _aimYaw);
        }

        /// <summary>Stumble / Fall: block all control and force the weapon to a safe stow.</summary>
        public void SetSuspended(bool value) => _suspended = value;

        /// <summary>Recovery: wipe every carried velocity / history and leave the weapon Stowed,
        /// with the mouse baseline re-primed so the first live frame injects nothing.</summary>
        public void FullReset()
        {
            _suspended = false;
            _stowed = true;
            _stowBlend = 1f;
            _aimYawVel = 0f;
            _reachVel = 0f;
            _reach = minReach;
            _targetYaw = stowYaw;
            _mouseVel = Vector3.zero;
            _tipVel = Vector3.zero;
            _prevTipOffset = _aimDir * _reach;
            _mousePrimed = false;        // re-primes _prevMouseOffset from the live cursor next frame
            ClearSwingHistory(_aimYaw);
            _solver?.Reset(_aimYaw, _reach, pivot != null ? pivot.position : transform.position);
        }

        /// <summary>Wipe the OverSwing angular history (debug travel + span ring buffer), re-based at
        /// <paramref name="bearing"/> so nothing counts across a stow / draw / reset boundary.</summary>
        private void ClearSwingHistory(float bearing)
        {
            _prevBearing = bearing;
            _prevRawBearing = bearing;
            _unwrappedBearing = 0f;
            _spanHead = 0;
            _spanCount = 0;
            _angTravelWindow = 0f;
            _angularSpan = 0f;
            _swingLoad = 0f;
            _wSpanHead = 0;
            _wSpanCount = 0;
            _weaponSpan = 0f;
        }

        private void OnGUI()
        {
            if (!showDebug) return;
            GUI.color = _stowed ? Color.gray : (_dbgTargetReach <= minReach + 0.02f ? Color.red : Color.white);
            GUI.Label(new Rect(12f, 12f, 580f, 212f),
                $"mLen        {_dbgMLen:F2}\n" +
                $"targetReach {_dbgTargetReach:F2}   (minReach {minReach:F2})\n" +
                $"_reach      {_reach:F2}\n" +
                $"yaw error   {_dbgYawErr:F0} deg\n" +
                $"_stowed     {_stowed}\n" +
                $"mouseSpeed  {MouseSpeed:F1} m/s   / {overswingSpeedRef:F0}\n" +
                $"angSpan     {_angularSpan:F0} deg      / {overswingAngleRef:F0}   <- drives load\n" +
                $"angTravel   {_angTravelWindow:F0} deg      (debug/old, cumulative)\n" +
                $"swingLoad   {_swingLoad:F2}         / {overswingThreshold:F2}\n" +
                $"weaponSpd   {WeaponSpeed:F1} m/s   weaponSpan {_weaponSpan:F0} deg  (Damage)");
        }

        /// <summary>Unit vector on the XZ plane for a yaw in degrees (matches Quaternion.Euler(0, yaw, 0) * forward).</summary>
        private static Vector3 YawDir(float yawDeg)
        {
            float r = yawDeg * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(r), 0f, Mathf.Cos(r));
        }

        /// <summary>Cursor position projected onto the horizontal plane at height <paramref name="y"/>.</summary>
        private Vector3 MouseOnPlane(float y)
        {
            Vector2 screenPos = Mouse.current != null
                ? Mouse.current.position.ReadValue()
                : new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);

            Ray ray = cam.ScreenPointToRay(screenPos);
            Plane plane = new Plane(Vector3.up, new Vector3(0f, y, 0f));

            return plane.Raycast(ray, out float enter)
                ? ray.GetPoint(enter)
                : pivot.position + _aimDir * _reach;
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Transform p = pivot != null ? pivot : transform.Find("WeaponPivot");
            if (p == null) return;
            Vector3 o = p.position;

            Gizmos.color = new Color(1f, 1f, 1f, 0.35f);
            DrawRing(o, minReach);
            DrawRing(o, maxReach);
            Gizmos.color = new Color(1f, 1f, 1f, 0.15f);
            DrawRing(o, turnDeadZone);

            // Stow zone: red = stow threshold, green = draw threshold.
            Gizmos.color = new Color(1f, 0.3f, 0.3f, 0.6f);
            DrawRing(o, stowRadius);
            Gizmos.color = new Color(0.3f, 1f, 0.4f, 0.6f);
            DrawRing(o, unstowRadius);

            Vector3 dir = _aimDir.sqrMagnitude > 1e-6f ? _aimDir : Vector3.forward;
            float r = Application.isPlaying ? _reach : Mathf.Lerp(minReach, maxReach, 0.5f);
            Vector3 b = o + dir * r;
            Gizmos.color = Application.isPlaying && _stowed ? Color.gray : Color.yellow;
            Gizmos.DrawLine(o, b);
            Gizmos.DrawWireSphere(b, 0.06f);

            if (Application.isPlaying && !_stowed)
            {
                Gizmos.color = Color.cyan;
                Gizmos.DrawLine(b, b + _mouseVel * 0.1f); // cursor velocity, scaled for view
            }
        }

        private static void DrawRing(Vector3 c, float radius)
        {
            const int seg = 48;
            Vector3 prev = c + new Vector3(radius, 0f, 0f);
            for (int i = 1; i <= seg; i++)
            {
                float a = i / (float)seg * Mathf.PI * 2f;
                Vector3 cur = c + new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
                Gizmos.DrawLine(prev, cur);
                prev = cur;
            }
        }
#endif
    }
}
