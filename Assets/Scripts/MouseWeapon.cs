using UnityEngine;
using UnityEngine.InputSystem;

namespace WeaponExperiment
{
    /// <summary>
    /// Weapon Endpoint Drag (0.1) — position guidance + mouse-velocity motion input.
    ///
    /// TWO things drive the weapon and are combined every frame:
    ///
    ///   POSITION (guidance) : the cursor's position tells the weapon roughly which
    ///                         bearing and reach it should be at. Tracked with
    ///                         Mathf.SmoothDamp toward that target (carries its own
    ///                         velocity, no speed ceiling). Unchanged from before.
    ///
    ///   VELOCITY (motion)   : the cursor's world-space velocity vector is measured
    ///                         each frame, low-passed, and split against the CURRENT
    ///                         facing into a radial part (-> feeds _reachVel) and a
    ///                         tangential part (-> converted v/r to deg/s and feeds
    ///                         _aimYawVel). So the same final cursor spot feels
    ///                         different depending on how the cursor got there.
    ///
    /// The split is pure geometry, not a classifier: a diagonal cursor move feeds both
    /// channels at once, so the weapon turns and extends/retracts in one continuous
    /// motion. There are no slash/thrust/swing states.
    ///
    /// Preserved: _aimYaw / _reach are separate state (no radial-pull flip), turnDeadZone
    /// (no flip / no jitter when the cursor crosses the body), min/max reach, fixed club
    /// mesh length. NOT here: rigidbodies, real mass/inertia, balance/force transfer,
    /// springs, arms/IK, attack states, damage, combos, animation.
    /// </summary>
    public class MouseWeapon : MonoBehaviour
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
        [Tooltip("Cursor closer than this to the player does NOT change facing " +
                 "(no 180 degree flip / no jitter when the cursor crosses the body).")]
        [SerializeField] private float turnDeadZone = 0.35f;

        [Header("Mouse velocity input")]
        [Tooltip("How strongly the cursor's own motion (not just its position) is injected " +
                 "into the weapon's velocity each second. 0 = position-only (old behaviour). " +
                 "Higher = the weapon more literally inherits how you moved the mouse.")]
        [SerializeField] private float velInfluence = 18f;
        [Tooltip("Low-pass on the measured cursor velocity, per second. Higher = more " +
                 "responsive but more jitter; lower = smoother but laggier.")]
        [SerializeField] private float mouseVelSmoothing = 20f;

        [Header("Club visual")]
        [Tooltip("Fixed world length of the club mesh. Never stretched; only slid.")]
        [SerializeField] private float clubLength = 1.2f;

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

            if (club != null)
            {
                // Lay the cylinder (2 units tall on local Y) along the pivot's local +Z,
                // and bake the fixed length into its scale ONCE. Nothing after this touches scale.
                club.localRotation = Quaternion.Euler(90f, 0f, 0f);
                Vector3 s = club.localScale;
                club.localScale = new Vector3(s.x, clubLength * 0.5f, s.z);
            }
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

            // --- Split the cursor velocity against the CURRENT (pre-update) facing.
            Vector3 perp = new Vector3(_aimDir.z, 0f, -_aimDir.x); // _aimDir rotated -90 deg on XZ
            float vRadial = Vector3.Dot(_mouseVel, _aimDir);        // m/s, + = pushing the tip out
            float vTangent = Vector3.Dot(_mouseVel, perp);          // m/s, + = sweeping toward +yaw
            float ffYawVel = (vTangent / Mathf.Max(_reach, 0.05f)) * Mathf.Rad2Deg; // v/r -> deg/s

            float k = 1f - Mathf.Exp(-velInfluence * dt); // this frame's blend of motion input into carried velocity

            // --- DIRECTION: position guidance unchanged; carried velocity pre-biased by
            //     the tangential cursor motion, then SmoothDamp eases toward the bearing.
            if (mLen > turnDeadZone)
                _targetYaw = Mathf.Atan2(m.x, m.z) * Mathf.Rad2Deg;
            _aimYawVel = Mathf.Lerp(_aimYawVel, ffYawVel, k);
            _aimYaw = Mathf.SmoothDampAngle(_aimYaw, _targetYaw, ref _aimYawVel, turnSmoothTime, Mathf.Infinity, dt);
            _aimDir = YawDir(_aimYaw);

            // --- REACH: position guidance unchanged; carried velocity pre-biased by the
            //     radial cursor motion. Zero the velocity at the clamps so it cannot wind up.
            _reachVel = Mathf.Lerp(_reachVel, vRadial, k);
            float targetReach = Mathf.Clamp(Vector3.Dot(m, _aimDir), minReach, maxReach);
            _reach = Mathf.SmoothDamp(_reach, targetReach, ref _reachVel, reachSmoothTime, Mathf.Infinity, dt);
            if (_reach <= minReach) { _reach = minReach; if (_reachVel < 0f) _reachVel = 0f; }
            else if (_reach >= maxReach) { _reach = maxReach; if (_reachVel > 0f) _reachVel = 0f; }

            // --- Compose: aim the pivot, slide the fixed-length club so its tip is at B.
            pivot.rotation = Quaternion.Euler(0f, _aimYaw, 0f);
            if (club != null)
                club.localPosition = new Vector3(0f, 0f, _reach - clubLength * 0.5f);
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

            Vector3 dir = _aimDir.sqrMagnitude > 1e-6f ? _aimDir : Vector3.forward;
            float r = Application.isPlaying ? _reach : Mathf.Lerp(minReach, maxReach, 0.5f);
            Vector3 b = o + dir * r;
            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(o, b);
            Gizmos.DrawWireSphere(b, 0.06f);

            if (Application.isPlaying)
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
