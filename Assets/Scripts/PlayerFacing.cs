using UnityEngine;
using UnityEngine.InputSystem;

namespace WeaponExperiment
{
    /// <summary>
    /// Facing 0.1. Splits "where the player looks" from "moving the weapon":
    ///
    ///   LMB up   -> the Player root turns toward the Player -> Mouse bearing (smoothed).
    ///   LMB down -> snapshot the current facing; the Player root is FROZEN there.
    ///   LMB held -> facing stays frozen (MouseWeapon takes the mouse as weapon input).
    ///   LMB up   -> facing tracks the mouse again.
    ///
    /// WASD movement direction is unchanged (world axes) - only the visual/aim facing
    /// follows the mouse. <see cref="PlayerShield"/> and <see cref="MouseWeapon"/> read
    /// <see cref="FacingYaw"/> / <see cref="WeaponEngaged"/> from here.
    /// </summary>
    [DefaultExecutionOrder(-8)]
    public class PlayerFacing : MonoBehaviour
    {
        [SerializeField] private Camera cam;
        [Tooltip("Seconds for the body to turn to the mouse bearing (short smooth, not a snap).")]
        [SerializeField] private float turnSmoothTime = 0.10f;

        private float _facingYaw;
        private float _facingYawVel;
        private float _snapshotYaw;
        private bool _engaged;

        /// <summary>Current body yaw (deg). Frozen at the snapshot while LMB is held.</summary>
        public float FacingYaw => _facingYaw;
        /// <summary>The facing captured when LMB went down.</summary>
        public float AttackFacingYaw => _snapshotYaw;
        /// <summary>LMB is held: the mouse is weapon input, facing/shield are frozen.</summary>
        public bool WeaponEngaged => _engaged;

        private void Awake()
        {
            if (cam == null) cam = Camera.main;
            _facingYaw = _snapshotYaw = transform.eulerAngles.y;
        }

        private void Update()
        {
            var mouse = Mouse.current;
            if (mouse != null)
            {
                if (mouse.leftButton.wasPressedThisFrame)
                {
                    _engaged = true;
                    _snapshotYaw = _facingYaw; // freeze facing exactly where it is now
                }
                else if (mouse.leftButton.wasReleasedThisFrame)
                {
                    _engaged = false;
                }
            }

            float target;
            if (_engaged)
            {
                target = _snapshotYaw;
            }
            else
            {
                Vector3 m = MouseOnPlane(transform.position.y) - transform.position;
                m.y = 0f;
                target = m.sqrMagnitude > 0.01f ? Mathf.Atan2(m.x, m.z) * Mathf.Rad2Deg : _facingYaw;
            }

            _facingYaw = Mathf.SmoothDampAngle(_facingYaw, target, ref _facingYawVel, Mathf.Max(turnSmoothTime, 1e-4f));
            transform.rotation = Quaternion.Euler(0f, _facingYaw, 0f);
        }

        private Vector3 MouseOnPlane(float y)
        {
            Vector2 screen = Mouse.current != null
                ? Mouse.current.position.ReadValue()
                : new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            if (cam == null) return transform.position + transform.forward;
            Ray ray = cam.ScreenPointToRay(screen);
            Plane plane = new Plane(Vector3.up, new Vector3(0f, y, 0f));
            return plane.Raycast(ray, out float enter) ? ray.GetPoint(enter) : transform.position + transform.forward;
        }
    }
}
