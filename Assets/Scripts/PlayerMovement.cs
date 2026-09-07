using UnityEngine;
using UnityEngine.InputSystem;

namespace WeaponExperiment
{
    /// <summary>
    /// Minimal WASD movement for the placeholder sphere player.
    /// Moves on the flat XZ ground plane in world axes (the camera angle is fixed),
    /// no gravity, no physics. Y position is left untouched.
    /// </summary>
    public class PlayerMovement : MonoBehaviour
    {
        [Header("Feel")]
        [SerializeField] private float moveSpeed = 6f;
        [Tooltip("How fast the sphere reaches full speed. Lower = floatier start/stop.")]
        [SerializeField] private float acceleration = 40f;

        private Vector3 _velocity;

        private void Update()
        {
            Vector2 input = ReadInput();
            Vector3 target = new Vector3(input.x, 0f, input.y) * moveSpeed;

            _velocity = Vector3.MoveTowards(_velocity, target, acceleration * Time.deltaTime);

            transform.position += _velocity * Time.deltaTime;
        }

        private static Vector2 ReadInput()
        {
            var kb = Keyboard.current;
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
