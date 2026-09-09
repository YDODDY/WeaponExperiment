using UnityEngine;

namespace WeaponExperiment
{
    /// <summary>
    /// OverSwing consequence (0.1) - Stumble or Fall.
    ///
    /// Not a combat or general state framework: one small enum + one timer coordinating
    /// <see cref="PlayerMovement"/> and <see cref="MouseWeapon"/>.
    ///
    ///   Normal --(MouseWeapon.SwingLoad >= threshold: fast AND wide swing)--> one random
    ///   roll decides the result:
    ///
    ///     Stumble (most of the time): WASD/Run AND mouse weapon control blocked; the
    ///       player is dragged a short distance along the weapon's ACTUAL motion
    ///       direction; the body only lurches; a short lockout, then recover.
    ///
    ///     Fall (fallChance of the time): same lockouts; same short drag; the body goes
    ///       right down; a clearly longer lockout, then recover.
    ///
    ///   Both --(timer)--> Recovery (shared): body rights, weapon un-suspends via
    ///   MouseWeapon.FullReset() (all carried velocity / feed-forward / mouse baseline /
    ///   overswing history cleared, weapon left Stowed) --> Normal.
    ///
    /// There is NO post-OverSwing input check: the result is fixed at the OverSwing
    /// instant. Runs before PlayerMovement/MouseWeapon so InputBlocked / SetSuspended
    /// apply the same frame.
    /// </summary>
    [DefaultExecutionOrder(-10)]
    public class PlayerOverswing : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private MouseWeapon weapon;
        [SerializeField] private PlayerMovement move;
        [Tooltip("Child transform holding the body mesh. Lurched / downed here; the gameplay " +
                 "root stays upright so mouse-plane / movement / weapon-pivot math is untouched.")]
        [SerializeField] private Transform bodyVisual;

        [Header("Result roll")]
        [Tooltip("Probability that an OverSwing becomes a Fall instead of a Stumble. " +
                 "Rolled ONCE per OverSwing. 0 = always Stumble, 1 = always Fall.")]
        [Range(0f, 1f)]
        [SerializeField] private float fallChance = 0.25f;

        [Header("Drag (shared by Stumble and Fall)")]
        [Tooltip("How far (m) the player is dragged along the captured weapon-motion direction.")]
        [SerializeField] private float dragDistance = 0.5f;
        [Tooltip("Seconds the drag is spread over (linear ease-out).")]
        [SerializeField] private float dragDuration = 0.5f;

        [Header("Stumble")]
        [Tooltip("Total control lockout (s) for a Stumble. WASD/Run + mouse weapon blocked.")]
        [SerializeField] private float stumbleDuration = 0.6f;
        [Tooltip("Fraction of the full downed body pose a Stumble shows (a lurch, not a fall).")]
        [Range(0f, 1f)]
        [SerializeField] private float stumbleLean = 0.35f;

        [Header("Fall")]
        [Tooltip("Total control lockout (s) for a Fall - clearly longer than a Stumble.")]
        [SerializeField] private float fallDuration = 1.7f;

        [Header("Body visual (fully downed pose)")]
        [Tooltip("Body-visual local euler at the full downed pose (Z = roll toward the drag side).")]
        [SerializeField] private Vector3 downedBodyEuler = new Vector3(0f, 0f, 85f);
        [Tooltip("How far the body visual sinks (local -Y, m) at the full downed pose.")]
        [SerializeField] private float downedBodySink = 0.3f;
        [Tooltip("Local Y scale multiplier at the full downed pose (squashed flat). X/Z widen to compensate.")]
        [SerializeField] private float downedBodySquashY = 0.5f;
        [Tooltip("Seconds for the body to lurch / go down / get back up.")]
        [SerializeField] private float bodyPoseSmoothTime = 0.12f;

        [Header("Recovery")]
        [Tooltip("Short settle after the lockout before Normal resumes (weapon stays suspended during it).")]
        [SerializeField] private float recoveryGuard = 0.15f;

        [Header("Debug")]
        [SerializeField] private bool showDebug = true;

        private enum State { Normal, Stumble, Fall, Recovery }
        private enum Result { None, Stumble, Fall }

        private State _state = State.Normal;
        private Result _lastResult = Result.None;
        private float _lastRoll = -1f;

        private float _timer;      // control lockout / recovery countdown
        private float _dragTimer;  // drag ease-out countdown (shared by Stumble and Fall)
        private Vector3 _dragDir = Vector3.forward;
        private float _dragSpeed0; // peak of the linear ease-out ramp
        private float _leanSign = 1f;

        private Vector3 _startPos;      // player position when the state began
        private float _travelledDist;   // world distance moved since (debug)

        private float _poseBlend;      // 0 = upright, 1 = fully downed
        private Vector3 _bodyRestPos;
        private Vector3 _bodyRestScale;

        private void Awake()
        {
            if (weapon == null) weapon = GetComponent<MouseWeapon>();
            if (move == null) move = GetComponent<PlayerMovement>();
            if (bodyVisual != null)
            {
                _bodyRestPos = bodyVisual.localPosition;
                _bodyRestScale = bodyVisual.localScale;
            }
        }

        private void Update()
        {
            float dt = Time.deltaTime;

            switch (_state)
            {
                case State.Normal:
                    if (weapon != null && weapon.OverSwingActive)
                        OnOverSwing();
                    break;

                case State.Stumble:
                case State.Fall:
                    _timer -= dt;
                    ApplyDrag(dt);
                    _travelledDist = Vector3.Distance(_startPos, transform.position);
                    if (_timer <= 0f)
                        EnterRecovery();
                    break;

                case State.Recovery:
                    // Body is righting and WASD is still blocked; when the guard elapses the
                    // weapon is fully reset (clean mouse baseline, no spike, no fake OverSwing)
                    // and control returns.
                    _timer -= dt;
                    if (_timer <= 0f)
                    {
                        if (weapon != null) weapon.FullReset();
                        if (move != null) move.InputBlocked = false;
                        _state = State.Normal;
                    }
                    break;
            }

            UpdateBodyPose(dt);
        }

        private void OnOverSwing()
        {
            _lastRoll = Random.value;
            bool fall = _lastRoll < fallChance;
            _lastResult = fall ? Result.Fall : Result.Stumble;

            _state = fall ? State.Fall : State.Stumble;
            _timer = fall ? fallDuration : stumbleDuration;
            _dragTimer = dragDuration;
            _startPos = transform.position;
            _travelledDist = 0f;

            if (move != null) { move.InputBlocked = true; move.ResetMotion(); }
            if (weapon != null) weapon.SetSuspended(true); // mouse weapon blocked for BOTH results

            Vector3 d = weapon != null ? weapon.WeaponMotionDir : Vector3.forward;
            if (d.sqrMagnitude < 1e-4f) d = weapon != null ? weapon.AimDir : Vector3.forward;
            _dragDir = d.normalized;
            _dragSpeed0 = 2f * dragDistance / Mathf.Max(dragDuration, 1e-3f);
            _leanSign = _dragDir.x >= 0f ? 1f : -1f; // roll toward the side we are dragged
        }

        private void ApplyDrag(float dt)
        {
            if (_dragTimer <= 0f || move == null) return;
            _dragTimer -= dt;
            float rem = Mathf.Clamp01(_dragTimer / Mathf.Max(dragDuration, 1e-3f)); // 1 -> 0 linear
            move.AddExternalVelocity(_dragDir * (_dragSpeed0 * rem));
        }

        private void EnterRecovery()
        {
            _state = State.Recovery;
            _timer = recoveryGuard;
            if (move != null) move.ResetMotion();
            // move.InputBlocked stays true, weapon stays SetSuspended(true) until the guard ends.
        }

        private void UpdateBodyPose(float dt)
        {
            if (bodyVisual == null) return;

            float target = _state == State.Fall ? 1f
                         : _state == State.Stumble ? Mathf.Clamp01(stumbleLean)
                         : 0f;
            _poseBlend = Mathf.Lerp(_poseBlend, target, 1f - Mathf.Exp(-dt / Mathf.Max(bodyPoseSmoothTime, 1e-4f)));

            bodyVisual.localRotation = Quaternion.Slerp(
                Quaternion.identity, Quaternion.Euler(downedBodyEuler * _leanSign), _poseBlend);
            bodyVisual.localPosition = _bodyRestPos + Vector3.down * (downedBodySink * _poseBlend);
            float sy = Mathf.Lerp(1f, Mathf.Max(downedBodySquashY, 1e-3f), _poseBlend);
            float sxz = Mathf.Lerp(1f, 1f / Mathf.Sqrt(Mathf.Max(downedBodySquashY, 1e-3f)), _poseBlend);
            bodyVisual.localScale = new Vector3(_bodyRestScale.x * sxz, _bodyRestScale.y * sy, _bodyRestScale.z * sxz);
        }

        private void OnGUI()
        {
            if (!showDebug) return;
            GUI.color = _state == State.Normal ? Color.white
                      : _state == State.Fall ? Color.red
                      : Color.yellow;
            GUI.Label(new Rect(12f, 228f, 560f, 68f),
                $"state       {_state}\n" +
                $"lastResult  {_lastResult}   roll {_lastRoll:F2}  / fallChance {fallChance:F2}\n" +
                $"lockTimer   {Mathf.Max(_timer, 0f):F2}\n" +
                $"dragged     {_travelledDist:F2} m   (target {dragDistance:F2})");
        }
    }
}
