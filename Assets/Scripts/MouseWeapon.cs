using UnityEngine;
using UnityEngine.InputSystem;

namespace WeaponExperiment
{
    /// <summary>
    /// Drives the club directly from the mouse — no attack button, no canned animation.
    ///
    /// The mouse cursor is projected onto the ground plane. The club sits on a pivot at
    /// the player and always points from the player toward that ground point, at a fixed
    /// reach. Sweeping the mouse around the player sweeps the club around the player.
    ///
    /// This is the deliberately simplest mapping: cursor direction -> club direction.
    /// No physics, no inertia, no per-weapon tuning. <see cref="trackSharpness"/> is the
    /// one knob for adding lag/weight if the instant version feels too stiff.
    /// </summary>
    public class MouseWeapon : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Empty transform at the player that yaws to aim the club. Auto-found as child \"WeaponPivot\".")]
        [SerializeField] private Transform pivot;
        [Tooltip("The club visual. Auto-found as the pivot's first child.")]
        [SerializeField] private Transform club;
        [Tooltip("Camera used to project the cursor onto the ground. Defaults to Camera.main.")]
        [SerializeField] private Camera cam;

        [Header("Feel")]
        [Tooltip("Club distance from the player centre.")]
        [SerializeField] private float reach = 1.4f;
        [Tooltip("How fast the club catches up to the cursor. 0 = instant / 1:1. " +
                 "Lower positive values = laggy, heavier-feeling swing.")]
        [SerializeField] private float trackSharpness = 0f;
        [Tooltip("Ignore the cursor while it is closer than this to the player " +
                 "(stops the club spinning wildly when the cursor is on top of the player).")]
        [SerializeField] private float centreDeadZone = 0.2f;

        private float _aimYaw;

        private void Awake()
        {
            if (pivot == null)
                pivot = transform.Find("WeaponPivot");
            if (club == null && pivot != null && pivot.childCount > 0)
                club = pivot.GetChild(0);
            if (cam == null)
                cam = Camera.main;

            if (pivot != null)
                _aimYaw = pivot.eulerAngles.y;
            if (club != null)
                club.localRotation = Quaternion.Euler(90f, 0f, 0f); // lay the cylinder along the pivot's forward axis
        }

        private void Update()
        {
            if (pivot == null || cam == null)
                return;

            Vector3 toCursor = GroundCursor() - pivot.position;
            toCursor.y = 0f;

            if (toCursor.magnitude >= centreDeadZone)
            {
                float targetYaw = Mathf.Atan2(toCursor.x, toCursor.z) * Mathf.Rad2Deg;
                _aimYaw = trackSharpness <= 0f
                    ? targetYaw
                    : Mathf.LerpAngle(_aimYaw, targetYaw, 1f - Mathf.Exp(-trackSharpness * Time.deltaTime));
            }

            pivot.rotation = Quaternion.Euler(0f, _aimYaw, 0f);

            if (club != null)
                club.localPosition = new Vector3(0f, 0f, reach);
        }

        /// <summary>Cursor position projected onto the horizontal plane at the pivot's height.</summary>
        private Vector3 GroundCursor()
        {
            Vector2 screenPos = Mouse.current != null
                ? Mouse.current.position.ReadValue()
                : new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);

            Ray ray = cam.ScreenPointToRay(screenPos);
            Plane ground = new Plane(Vector3.up, new Vector3(0f, pivot.position.y, 0f));

            return ground.Raycast(ray, out float enter)
                ? ray.GetPoint(enter)
                : pivot.position + pivot.forward;
        }
    }
}
