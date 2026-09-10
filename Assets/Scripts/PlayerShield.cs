using UnityEngine;
using UnityEngine.InputSystem;

namespace WeaponExperiment
{
    /// <summary>
    /// Shield / Block 0.1. Always a real object with a live collider.
    ///
    /// FACING: follows <see cref="PlayerFacing.FacingYaw"/> (the shared body facing), not
    /// the mouse and not the weapon. So while LMB is held the shield is frozen at the
    /// attack-facing snapshot even as MouseWeapon swings wildly.
    ///
    /// RMB: position only, never a block toggle.
    ///   RMB up   -> Rest     (restDist along the facing)
    ///   RMB held -> Extended (extendedDist along the facing)
    ///
    /// Whether an incoming weapon is stopped is decided by the weapon's contact solver -
    /// not from RMB. (Contact solver / collision unchanged this pass.)
    /// </summary>
    public class PlayerShield : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Player root (pivot for position). Auto: parent.")]
        [SerializeField] private Transform owner;
        [Tooltip("Shared body facing. Auto: on the owner.")]
        [SerializeField] private PlayerFacing facing;

        [Header("Facing")]
        [Tooltip("Seconds for the shield to catch up to the body facing. A little lag is fine.")]
        [SerializeField] private float faceSmoothTime = 0.12f;

        [Header("Rest / Extended (metres along the facing)")]
        [SerializeField] private float restDist = 0.55f;
        [SerializeField] private float extendedDist = 1.1f;
        [SerializeField] private float heightOffset = 0f;
        [Tooltip("Seconds for the shield to slide between Rest and Extended.")]
        [SerializeField] private float moveSmoothTime = 0.12f;

        private float _yaw;
        private float _yawVel;
        private float _dist;
        private Vector3 _prevWorldPos;
        private Vector3 _worldVel;

        /// <summary>Shield's own world velocity (m/s). Future parry can use weaponVel - shieldVel.</summary>
        public Vector3 ShieldVelocity => _worldVel;

        private void Awake()
        {
            if (owner == null) owner = transform.parent != null ? transform.parent : transform;
            if (facing == null) facing = owner.GetComponent<PlayerFacing>();
            _yaw = facing != null ? facing.FacingYaw : owner.eulerAngles.y;
            _dist = restDist;
            Apply();
            _prevWorldPos = transform.position;
        }

        private void Update()
        {
            float dt = Time.deltaTime;

            float targetYaw = facing != null ? facing.FacingYaw : owner.eulerAngles.y;
            _yaw = Mathf.SmoothDampAngle(_yaw, targetYaw, ref _yawVel, Mathf.Max(faceSmoothTime, 1e-4f));

            bool extend = Mouse.current != null && Mouse.current.rightButton.isPressed;
            float dTarget = extend ? extendedDist : restDist;
            _dist = Mathf.Lerp(_dist, dTarget, 1f - Mathf.Exp(-dt / Mathf.Max(moveSmoothTime, 1e-4f)));

            Apply();

            _worldVel = (transform.position - _prevWorldPos) / Mathf.Max(dt, 1e-5f);
            _prevWorldPos = transform.position;
        }

        private void Apply()
        {
            // Drive WORLD transform so the owner root's own rotation does not compound.
            Vector3 fwd = new Vector3(Mathf.Sin(_yaw * Mathf.Deg2Rad), 0f, Mathf.Cos(_yaw * Mathf.Deg2Rad));
            transform.position = owner.position + fwd * _dist + Vector3.up * heightOffset;
            transform.rotation = Quaternion.Euler(0f, _yaw, 0f); // slab face -> outward along the facing
        }
    }
}
