using UnityEngine;

namespace WeaponExperiment
{
    /// <summary>
    /// Combat Entity 0.1 - the decision layer only. Uses the same physical weapon / damage
    /// rules as the player (via <see cref="EntityWeapon"/> + <see cref="WeaponHitSensor"/>).
    ///
    ///   Neutral : if the player is within faceRadius, rotate to face them. No move, no attack.
    ///   Hostile : turned on PERMANENTLY (0.1) by ANY real damage from the player weapon
    ///             (subscribes to DamageTarget.Damaged). Then keeps COMBAT SPACING:
    ///               too far (> chaseEnter)      -> Chase  (walk in, Dash to close a big gap)
    ///               comfortable band            -> Hold   (stop, face, wait out the attack cd)
    ///               too close (< backoffEnter)  -> BackOff (walk straight back)
    ///             Chase/BackOff only end at 'comfortable' (that gap is the hysteresis).
    ///             Every 3-4 s, when within attackRange, fire ONE ~swingArc EntityWeapon swing.
    ///   Dead    : DamageTarget.IsDead -> this component is disabled by DeathDisable, which
    ///             stops move / dash / attack.
    ///
    /// No behaviour tree / utility AI / navmesh / strafe / dodge / prediction / feint.
    /// </summary>
    [DefaultExecutionOrder(-5)]
    public class CombatEntity : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private Transform player;          // auto: tag "Player"
        [SerializeField] private DamageTarget damageTarget; // auto: GetComponent
        [SerializeField] private EntityWeapon entityWeapon; // auto: GetComponent

        [Header("Neutral")]
        [Tooltip("Player within this range: face the player (do NOT move or attack).")]
        [SerializeField] private float faceRadius = 4f;
        [SerializeField] private float turnSpeed = 360f;

        [Header("Combat spacing")]
        [Tooltip("Closer than this -> BackOff (retreat).")]
        [SerializeField] private float backoffEnter = 1.35f;
        [Tooltip("The spacing the entity settles at. Chase and BackOff both end here (hysteresis pivot). " +
                 "Keep it <= attackRange so Hold-distance swings can actually reach.")]
        [SerializeField] private float comfortable = 2.1f;
        [Tooltip("Farther than this -> Chase (approach).")]
        [SerializeField] private float chaseEnter = 3.1f;
        [SerializeField] private float chaseSpeed = 3f;
        [SerializeField] private float backoffSpeed = 2.5f;

        [Header("Hostile - dash")]
        [Tooltip("In Chase, dash forward to close the gap when farther than this. Dash ends at 'comfortable'.")]
        [SerializeField] private float dashTriggerRange = 4.5f;
        [SerializeField] private float dashSpeed = 12f;
        [SerializeField] private float dashDuration = 0.25f;
        [SerializeField] private float dashCooldown = 2.5f;

        [Header("Hostile - attack")]
        [SerializeField] private float attackRange = 2.3f;
        [SerializeField] private float attackIntervalMin = 3f;
        [SerializeField] private float attackIntervalMax = 4f;

        [Header("Debug")]
        [SerializeField] private bool showDebug = true;

        private enum SpaceState { Chase, Hold, BackOff }

        private bool _hostile;
        private SpaceState _space = SpaceState.Hold;
        private float _attackTimer;
        private float _dashTimer; // > 0 while dashing
        private float _dashCd;
        private Vector3 _dashDir;

        private bool Dead => damageTarget != null && damageTarget.IsDead;

        private void Awake()
        {
            if (player == null)
            {
                var p = GameObject.FindWithTag("Player");
                if (p != null) player = p.transform;
            }
            if (damageTarget == null) damageTarget = GetComponent<DamageTarget>();
            if (entityWeapon == null) entityWeapon = GetComponent<EntityWeapon>();
            if (damageTarget != null) damageTarget.Damaged += OnDamaged;
            _attackTimer = Random.Range(attackIntervalMin, attackIntervalMax);
        }

        private void OnDestroy()
        {
            if (damageTarget != null) damageTarget.Damaged -= OnDamaged;
        }

        private void OnDamaged(DamageInfo info)
        {
            if (_hostile) return;
            _hostile = true; // any real damage -> hostile, permanently for 0.1
            _attackTimer = Random.Range(attackIntervalMin, attackIntervalMax);
        }

        private void Update()
        {
            if (Dead || player == null) return;

            float dt = Time.deltaTime;
            Vector3 to = player.position - transform.position;
            to.y = 0f;
            float dist = to.magnitude;
            Vector3 dir = dist > 1e-3f ? to / dist : transform.forward;

            if (_hostile || dist <= faceRadius)
            {
                Quaternion want = Quaternion.LookRotation(dir, Vector3.up);
                transform.rotation = Quaternion.RotateTowards(transform.rotation, want, turnSpeed * dt);
            }

            if (!_hostile)
                return;

            _dashCd -= dt;

            // --- Combat spacing state (hysteresis: Chase/BackOff only end at 'comfortable') ---
            switch (_space)
            {
                case SpaceState.Hold:
                    if (dist > chaseEnter) _space = SpaceState.Chase;
                    else if (dist < backoffEnter) _space = SpaceState.BackOff;
                    break;
                case SpaceState.Chase:
                    if (dist <= comfortable) { _space = SpaceState.Hold; _dashTimer = 0f; }
                    break;
                case SpaceState.BackOff:
                    if (dist >= comfortable) _space = SpaceState.Hold;
                    break;
            }

            // --- Movement per state ---
            if (_dashTimer > 0f)
            {
                _dashTimer -= dt;
                if (dist <= comfortable) _dashTimer = 0f;              // reached spacing -> stop the dash
                else transform.position += _dashDir * (dashSpeed * dt);
            }
            else if (_space == SpaceState.Chase)
            {
                if (dist > dashTriggerRange && _dashCd <= 0f)
                {
                    _dashTimer = dashDuration;
                    _dashCd = dashCooldown;
                    _dashDir = dir;                                    // snapshot toward player; not re-homed
                }
                else
                {
                    transform.position += dir * (chaseSpeed * dt);
                }
            }
            else if (_space == SpaceState.BackOff)
            {
                transform.position += -dir * (backoffSpeed * dt);     // straight back, no strafe
            }
            // Hold: no movement (body still faces the player, above)

            _attackTimer -= dt;
            if (_attackTimer <= 0f && dist <= attackRange && entityWeapon != null
                && !entityWeapon.IsSwinging && !entityWeapon.IsRecoiling)
            {
                float yawToPlayer = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
                entityWeapon.StartSwing(yawToPlayer);
                _attackTimer = Random.Range(attackIntervalMin, attackIntervalMax);
            }
        }

        private void OnGUI()
        {
            if (!showDebug || Camera.main == null || player == null) return;
            Vector3 sp = Camera.main.WorldToScreenPoint(transform.position + Vector3.up * 1.9f);
            if (sp.z <= 0f) return;

            float d = Vector3.Distance(
                new Vector3(transform.position.x, 0f, transform.position.z),
                new Vector3(player.position.x, 0f, player.position.z));
            GUI.color = Dead ? Color.red : (_hostile ? new Color(1f, 0.5f, 0.3f) : Color.white);
            GUI.Label(new Rect(sp.x - 110f, Screen.height - sp.y - 6f, 240f, 40f),
                $"{(Dead ? "DEAD" : _hostile ? $"HOSTILE [{_space}]" : "neutral")}   d={d:F1}\n" +
                $"{(_hostile ? $"atk in {Mathf.Max(_attackTimer, 0f):F1}s" : "")}");
        }
    }
}
