using UnityEngine;
using UnityEngine.InputSystem;

namespace WeaponExperiment
{
    /// <summary>
    /// Minimal WASD movement for the placeholder sphere player, with hold-Left-Shift to run.
    /// Moves on the flat XZ ground plane in world axes (the camera angle is fixed),
    /// no gravity, no physics. Y position is left untouched.
    /// Run is a plain speed swap: no acceleration burst, no stamina, no dash.
    /// </summary>
    public class PlayerMovement : MonoBehaviour
    {
        [Header("Feel")]
        [SerializeField] private float moveSpeed = 6f;
        [Tooltip("Speed while Left Shift is held. Applies only when WASD is also pressed.")]
        [SerializeField] private float runSpeed = 11f;
        [Tooltip("How fast the sphere reaches full speed. Lower = floatier start/stop.")]
        [SerializeField] private float acceleration = 40f;

        private Vector3 _velocity;
        private Vector3 _externalVelocity; // forced motion (e.g. stagger drag), applied once then cleared

        /// <summary>While true, WASD/Run are ignored. External velocity still applies.</summary>
        public bool InputBlocked { get; set; }

        /// <summary>Add a forced velocity for the next Update only (call every frame to sustain).</summary>
        public void AddExternalVelocity(Vector3 v) => _externalVelocity += v;

        /// <summary>Zero the player's own movement velocity (used on Fall / Recovery).</summary>
        public void ResetMotion()
        {
            _velocity = Vector3.zero;
            _externalVelocity = Vector3.zero;
        }

        private void Update()
        {
            var kb = Keyboard.current;

            Vector2 input = InputBlocked ? Vector2.zero : ReadInput(kb);
            bool running = !InputBlocked && kb != null && kb.leftShiftKey.isPressed;
            float speed = running ? runSpeed : moveSpeed;

            Vector3 target = new Vector3(input.x, 0f, input.y) * speed;

            _velocity = Vector3.MoveTowards(_velocity, target, acceleration * Time.deltaTime);

            transform.position += (_velocity + _externalVelocity) * Time.deltaTime;
            _externalVelocity = Vector3.zero;
        }

        private static Vector2 ReadInput(Keyboard kb)
        {
            if (kb == null)
                return Vector2.zero;

            Vector2 v = Vector2.zero;
            if (kb.wKey.isPressed) v.y += 1f;
            if (kb.sKey.isPressed) v.y -= 1f;
            if (kb.dKey.isPressed) v.x += 1f;
            if (kb.aKey.isPressed) v.x -= 1f;

            return v.sqrMagnitude > 1f ? v.normalized : v;
        }
    }
}
