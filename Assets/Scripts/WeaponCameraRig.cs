using UnityEngine;

namespace WeaponExperiment
{
    /// <summary>
    /// Fixed-angle oblique top-down camera. The rotation is constant; only the position
    /// tracks the player, keeping a constant offset. Lives on the Main Camera.
    /// </summary>
    public class WeaponCameraRig : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Player to follow. Auto-found by the \"Player\" tag if left empty.")]
        [SerializeField] private Transform target;

        [Header("Fixed angle")]
        [Tooltip("Pitch of the camera. 90 = straight down, ~55 = oblique top-down.")]
        [SerializeField] private float pitch = 55f;
        [SerializeField] private float yaw = 0f;
        [Tooltip("Distance from the player along the view direction.")]
        [SerializeField] private float distance = 16f;

        [Header("Follow")]
        [Tooltip("0 = camera is locked in place, higher = snappier follow.")]
        [SerializeField] private float followSharpness = 12f;

        private void Awake()
        {
            if (target == null)
            {
                var tagged = GameObject.FindWithTag("Player");
                if (tagged != null)
                    target = tagged.transform;
            }
        }

        private void LateUpdate()
        {
            if (target == null)
                return;

            Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 desired = target.position - (rot * Vector3.forward) * distance;

            transform.position = followSharpness <= 0f
                ? desired
                : Vector3.Lerp(transform.position, desired, 1f - Mathf.Exp(-followSharpness * Time.deltaTime));
            transform.rotation = rot;
        }
    }
}
