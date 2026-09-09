using UnityEngine;

namespace WeaponExperiment
{
    /// <summary>
    /// The minimal data a downstream system can read off ONE physical weapon-contact
    /// event. Deliberately more than a bare float so that, later, an HP system, an NPC
    /// reaction/memory system, etc. can each interpret the same event differently -
    /// without an event bus / combat framework existing yet.
    /// </summary>
    public struct DamageInfo
    {
        public float amount;
        public Vector3 hitPoint;
        public float weaponSpeed;      // actual weapon tip speed at contact (m/s, player-relative)
        public float weaponSwingSpan;  // actual weapon arc swept recently (deg)
        public GameObject source;      // attacker (the Player), if known
    }

    /// <summary>
    /// Damage 0.1 test dummy: HP only. No AI / death anim / ragdoll / loot / VFX / armor /
    /// body parts. HP reaching 0 just logs and flips <see cref="IsDead"/>.
    /// </summary>
    public class DamageTarget : MonoBehaviour
    {
        [SerializeField] private float maxHP = 100f;
        [SerializeField] private bool showDebug = true;

        private float _hp;
        private DamageInfo _lastHit;
        private bool _hasHit;

        public float HP => _hp;
        public float MaxHP => maxHP;
        public bool IsDead => _hp <= 0f;

        private void Awake() => _hp = maxHP;

        public void TakeDamage(in DamageInfo info)
        {
            if (IsDead) return;
            _hp = Mathf.Max(0f, _hp - info.amount);
            _lastHit = info;
            _hasHit = true;
            Debug.Log(
                $"[DamageTarget:{name}] -{info.amount:F1}  " +
                $"(weaponSpeed {info.weaponSpeed:F1} m/s, swingSpan {info.weaponSwingSpan:F0} deg)  " +
                $"->  HP {_hp:F0}/{maxHP:F0}{(IsDead ? "   *** DEAD ***" : "")}", this);
        }

        private void OnGUI()
        {
            if (!showDebug || Camera.main == null) return;
            Vector3 p = Camera.main.WorldToScreenPoint(transform.position + Vector3.up * 1.4f);
            if (p.z <= 0f) return;

            var r = new Rect(p.x - 95f, Screen.height - p.y - 32f, 190f, 46f);
            GUI.color = IsDead ? Color.red : Color.white;
            string s = $"{name}    HP {_hp:F0} / {maxHP:F0}" + (IsDead ? "   DEAD" : "");
            if (_hasHit) s += $"\nlast hit  -{_lastHit.amount:F1}";
            GUI.Label(r, s);
        }
    }
}
